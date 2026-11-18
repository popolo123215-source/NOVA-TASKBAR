using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Nova.WindowsClient.Services;

internal sealed class AudioMixerService : IDisposable
{
    internal sealed record AudioEndpoint(string Id, string Name);

    internal sealed record AudioSession(
        string Id,
        string SessionId,
        string DeviceId,
        string Name,
        string? ExecutablePath,
        BitmapSource? Icon,
        float Volume,
        bool IsMuted);

    internal sealed record Snapshot(
        IReadOnlyList<AudioEndpoint> Outputs,
        IReadOnlyList<AudioEndpoint> Inputs,
        IReadOnlyList<AudioSession> Sessions,
        string? DefaultOutputId,
        string? DefaultInputId,
        string? Error);

    private static readonly Guid PolicyConfigClassId = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private readonly MMDeviceEnumerator? _notificationEnumerator;
    private readonly MMDeviceNotificationClient? _notificationClient;
    private readonly string? _notificationError;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public AudioMixerService()
    {
        MMDeviceEnumerator? enumerator = null;
        MMDeviceNotificationClient? notificationClient = null;
        string? notificationError = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            notificationClient = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            notificationClient.DeviceAdded += OnDevicesChanged;
            notificationClient.DeviceRemoved += OnDevicesChanged;
            notificationClient.DeviceStateChanged += OnDevicesChanged;
            notificationClient.DefaultDeviceChanged += OnDevicesChanged;
        }
        catch (Exception ex) when (IsAudioException(ex))
        {
            notificationClient?.Dispose();
            enumerator?.Dispose();
            enumerator = null;
            notificationClient = null;
            notificationError = $"Автообнаружение аудиоустройств недоступно: {ex.Message}";
        }
        _notificationEnumerator = enumerator;
        _notificationClient = notificationClient;
        _notificationError = notificationError;
    }

    public Task<Snapshot> RefreshAsync() => Task.Run(() =>
    {
        var snapshot = CreateSnapshot();
        return _notificationError is null || snapshot.Error is not null
            ? snapshot
            : snapshot with { Error = _notificationError };
    });

    public Task SetSessionVolumeAsync(string deviceId, string sessionId, float volume) =>
        Task.Run(() => UpdateSession(deviceId, sessionId, control => control.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1)));

    public Task SetSessionMuteAsync(string deviceId, string sessionId, bool isMuted) =>
        Task.Run(() => UpdateSession(deviceId, sessionId, control => control.SimpleAudioVolume.Mute = isMuted));

    public Task SetDefaultDeviceAsync(string deviceId, DataFlow flow) => Task.Run(() =>
    {
        var type = Type.GetTypeFromCLSID(PolicyConfigClassId, throwOnError: true)
            ?? throw new InvalidOperationException("Windows Audio PolicyConfig недоступен.");
        var policy = (IPolicyConfig?)Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Не удалось создать Windows Audio PolicyConfig.");
        try
        {
            foreach (var role in new[] { AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications })
            {
                var result = policy.SetDefaultEndpoint(deviceId, role);
                Marshal.ThrowExceptionForHR(result);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    });

    private void OnDevicesChanged(object? sender, EventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);

    private static Snapshot CreateSnapshot()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var outputs = ReadEndpoints(enumerator, DataFlow.Render);
            var inputs = ReadEndpoints(enumerator, DataFlow.Capture);
            var defaultOutput = ReadDefaultDeviceId(enumerator, DataFlow.Render);
            var defaultInput = ReadDefaultDeviceId(enumerator, DataFlow.Capture);
            var sessions = ReadActiveSessions(enumerator);
            return new Snapshot(outputs, inputs, sessions, defaultOutput, defaultInput, null);
        }
        catch (Exception ex) when (IsAudioException(ex))
        {
            return new Snapshot(
                Array.Empty<AudioEndpoint>(),
                Array.Empty<AudioEndpoint>(),
                Array.Empty<AudioSession>(),
                null,
                null,
                $"Windows Audio недоступен: {ex.Message}");
        }
    }

    private static IReadOnlyList<AudioEndpoint> ReadEndpoints(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var result = new List<AudioEndpoint>();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        for (var index = 0; index < devices.Count; index++)
        {
            using var device = devices[index];
            result.Add(new AudioEndpoint(device.ID, device.FriendlyName));
        }
        return result;
    }

    private static string? ReadDefaultDeviceId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        if (!enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var device) || device is null)
            return null;
        using (device)
            return device.ID;
    }

    private static IReadOnlyList<AudioSession> ReadActiveSessions(MMDeviceEnumerator enumerator)
    {
        var result = new List<AudioSession>();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            using var device = devices[deviceIndex];
            using var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            using var sessions = manager.Sessions;
            for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
            {
                using var control = sessions[sessionIndex];
                if (control.State != AudioSessionState.AudioSessionStateActive || control.IsSystemSoundsSession)
                    continue;

                var processId = control.GetProcessID;
                var (processName, executablePath) = GetProcessDetails(processId);
                var name = FirstNotBlank(control.DisplayName, processName, Path.GetFileNameWithoutExtension(executablePath), "Приложение");
                var sessionId = FirstNotBlank(control.GetSessionInstanceIdentifier, control.GetSessionIdentifier);
                result.Add(new AudioSession(
                    $"{device.ID}|{sessionId}",
                    sessionId,
                    device.ID,
                    name,
                    executablePath,
                    TryLoadIcon(FirstNotBlank(control.IconPath, executablePath)),
                    control.SimpleAudioVolume.Volume,
                    control.SimpleAudioVolume.Mute));
            }
        }
        return result
            .OrderBy(session => session.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(session => session.DeviceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string? Name, string? ExecutablePath) GetProcessDetails(uint processId)
    {
        if (processId == 0)
            return (null, null);
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Access to another process' executable path is not always permitted.
            }
            return (process.ProcessName, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return (null, null);
        }
    }

    private static BitmapSource? TryLoadIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
            return null;
        var path = Environment.ExpandEnvironmentVariables(iconPath);
        var separator = path.IndexOf(',');
        if (separator > 0)
            path = path[..separator];
        if (!File.Exists(path))
            return null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;
            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }

    private static void UpdateSession(string deviceId, string sessionId, Action<AudioSessionControl> update)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(deviceId);
        using var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        using var sessions = manager.Sessions;
        for (var index = 0; index < sessions.Count; index++)
        {
            using var control = sessions[index];
            if (!string.Equals(control.GetSessionInstanceIdentifier, sessionId, StringComparison.Ordinal) &&
                !string.Equals(control.GetSessionIdentifier, sessionId, StringComparison.Ordinal))
                continue;
            update(control);
            return;
        }
        throw new InvalidOperationException("Аудиосессия уже завершена или устройство отключено.");
    }

    private static string FirstNotBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool IsAudioException(Exception ex) =>
        ex is COMException or InvalidOperationException or UnauthorizedAccessException or
            Win32Exception or ArgumentException or ObjectDisposedException or PlatformNotSupportedException;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_notificationClient is not null)
        {
            _notificationClient.DeviceAdded -= OnDevicesChanged;
            _notificationClient.DeviceRemoved -= OnDevicesChanged;
            _notificationClient.DeviceStateChanged -= OnDevicesChanged;
            _notificationClient.DefaultDeviceChanged -= OnDevicesChanged;
            _notificationClient.Dispose();
        }
        _notificationEnumerator?.Dispose();
    }

    private enum AudioRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, AudioRole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
