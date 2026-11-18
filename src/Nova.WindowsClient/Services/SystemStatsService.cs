using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Nova.WindowsClient.Services;

// Adapted from Atoll's MIT-licensed PDH/GPU telemetry approach; see licenses/Atoll.LICENSE.
internal sealed class SystemStatsService : IDisposable
{
    internal sealed record Snapshot(
        double? CpuPercent,
        double? CpuFrequencyMhz,
        double? CpuTemperatureCelsius,
        double? Gpu3DPercent,
        double? GpuFrequencyMhz,
        double? GpuTemperatureCelsius,
        double? GpuMemoryUsedGb,
        double? GpuMemoryTotalGb,
        double RamUsedGb,
        double RamTotalGb,
        double? NetworkDownloadBytesPerSecond,
        double? NetworkUploadBytesPerSecond,
        double? DiskReadBytesPerSecond,
        double? DiskWriteBytesPerSecond,
        IReadOnlyList<DiskSpace> DiskSpaces,
        bool? RadminVpnConnected,
        string? NetworkError,
        string? HardwareSensorError,
        string? DiskSpaceError,
        string? PerformanceCounterError);

    internal sealed record DiskSpace(string Name, long AvailableBytes, long TotalBytes);

    private const uint FormatDouble = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);
    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _cpuCounter;
    private IntPtr _cpuFrequencyCounter;
    private IntPtr _gpuCounter;
    private IntPtr _diskReadCounter;
    private IntPtr _diskWriteCounter;
    private readonly Computer _hardwareComputer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    private bool _initialized;
    private bool _hardwareInitializationAttempted;
    private bool _hardwareOpened;
    private bool _disposed;
    private string? _hardwareSensorError;
    private string? _performanceCounterError;
    private long _previousNetworkReceived;
    private long _previousNetworkSent;
    private long _previousNetworkTimestamp;
    private bool _hasPreviousNetworkSample;

    public Snapshot Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureInitialized();

            double? cpu = null;
            double? cpuFrequency = null;
            double? gpu = null;
            double? diskRead = null;
            double? diskWrite = null;
            if (_query != IntPtr.Zero)
            {
                if (PdhCollectQueryData(_query) == 0)
                {
                    if (_cpuCounter != IntPtr.Zero &&
                        PdhGetFormattedCounterValue(_cpuCounter, FormatDouble, out _, out var cpuValue) == 0 &&
                        cpuValue.CStatus == 0 &&
                        double.IsFinite(cpuValue.Value))
                        cpu = Math.Clamp(cpuValue.Value, 0, 100);

                    if (_gpuCounter != IntPtr.Zero)
                        gpu = ReadPeakCounter(_gpuCounter);
                    cpuFrequency = ReadCounter(_cpuFrequencyCounter);
                    diskRead = ReadCounter(_diskReadCounter);
                    diskWrite = ReadCounter(_diskWriteCounter);
                }
                else
                {
                    _performanceCounterError = "Не удалось обновить Windows Performance Counters.";
                }
            }

            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить состояние оперативной памяти.");

            var total = memory.TotalPhysical / 1_073_741_824d;
            var used = total - memory.AvailablePhysical / 1_073_741_824d;
            var network = SampleNetwork();
            var hardware = SampleHardwareSensors();
            var diskSample = SampleDiskSpaces();
            return new Snapshot(
                cpu,
                hardware.CpuFrequencyMhz ?? cpuFrequency,
                hardware.CpuTemperatureCelsius,
                gpu,
                hardware.GpuFrequencyMhz,
                hardware.GpuTemperatureCelsius,
                hardware.GpuMemoryUsedGb,
                hardware.GpuMemoryTotalGb,
                Math.Max(0, used),
                total,
                network.DownloadBytesPerSecond,
                network.UploadBytesPerSecond,
                diskRead,
                diskWrite,
                diskSample.Spaces,
                network.RadminVpnConnected,
                network.Error,
                hardware.Error,
                diskSample.Error,
                _performanceCounterError);
        }
    }

    private HardwareSensorSample SampleHardwareSensors()
    {
        EnsureHardwareOpened();
        if (!_hardwareOpened)
            return HardwareSensorSample.Empty(_hardwareSensorError);

        var cpuTemperatures = new List<ISensor>();
        var cpuClocks = new List<ISensor>();
        var gpuTemperatures = new List<ISensor>();
        var gpuClocks = new List<ISensor>();
        var gpuMemoryUsed = new List<double>();
        var gpuMemoryTotal = new List<double>();

        foreach (var hardware in EnumerateHardware(_hardwareComputer.Hardware))
        {
            try
            {
                hardware.Update();
                var sensors = hardware.Sensors.Where(sensor => sensor.Value is not null).ToArray();
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    cpuTemperatures.AddRange(sensors.Where(sensor => sensor.SensorType == SensorType.Temperature));
                    cpuClocks.AddRange(sensors.Where(sensor => sensor.SensorType == SensorType.Clock));
                }
                else if (IsGpu(hardware.HardwareType))
                {
                    gpuTemperatures.AddRange(sensors.Where(sensor => sensor.SensorType == SensorType.Temperature));
                    gpuClocks.AddRange(sensors.Where(sensor => sensor.SensorType == SensorType.Clock));
                    foreach (var sensor in sensors.Where(sensor =>
                                 sensor.SensorType is SensorType.Data or SensorType.SmallData))
                    {
                        var name = sensor.Name;
                        var value = sensor.Value!.Value;
                        if (!float.IsFinite(value) || value < 0)
                            continue;
                        if (ContainsAny(name, "memory used", "dedicated memory used", "gpu memory used"))
                            gpuMemoryUsed.Add(value);
                        else if (ContainsAny(name, "memory total", "dedicated memory total", "gpu memory total"))
                            gpuMemoryTotal.Add(value);
                    }
                }
            }
            catch (Exception ex) when (IsSensorException(ex))
            {
                _hardwareSensorError ??= $"{hardware.Name}: {ex.Message}";
            }
        }

        return new HardwareSensorSample(
            SelectCpuTemperature(cpuTemperatures),
            AverageClock(cpuClocks),
            MaximumValue(gpuTemperatures),
            MaximumValue(gpuClocks),
            gpuMemoryUsed.Count == 0 ? null : gpuMemoryUsed.Sum(),
            gpuMemoryTotal.Count == 0 ? null : gpuMemoryTotal.Sum(),
            _hardwareSensorError);
    }

    private void EnsureHardwareOpened()
    {
        if (_hardwareInitializationAttempted)
            return;

        _hardwareInitializationAttempted = true;
        try
        {
            _hardwareComputer.Open();
            _hardwareOpened = true;
        }
        catch (Exception ex) when (IsSensorException(ex))
        {
            _hardwareSensorError = $"Аппаратные датчики недоступны: {ex.Message}";
        }
    }

    private static IEnumerable<IHardware> EnumerateHardware(IEnumerable<IHardware> hardware)
    {
        foreach (var device in hardware)
        {
            yield return device;
            foreach (var child in EnumerateHardware(device.SubHardware))
                yield return child;
        }
    }

    private static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static double? SelectCpuTemperature(IReadOnlyCollection<ISensor> sensors)
    {
        var preferred = sensors.Where(sensor =>
            ContainsAny(sensor.Name, "package", "tctl", "tdie")).ToArray();
        return MaximumValue(preferred.Length > 0 ? preferred : sensors);
    }

    private static double? AverageClock(IReadOnlyCollection<ISensor> sensors)
    {
        var cores = sensors.Where(sensor => sensor.Name.Contains("core", StringComparison.OrdinalIgnoreCase)).ToArray();
        var values = (cores.Length > 0 ? cores : sensors)
            .Select(sensor => sensor.Value)
            .Where(value => value is { } current && float.IsFinite(current) && current > 0)
            .Select(value => (double)value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double? MaximumValue(IEnumerable<ISensor> sensors)
    {
        var values = sensors
            .Select(sensor => sensor.Value)
            .Where(value => value is { } current && float.IsFinite(current))
            .Select(value => (double)value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static bool IsSensorException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            DllNotFoundException or TypeInitializationException or Win32Exception or
            COMException or PlatformNotSupportedException or ArgumentException;

    private static (IReadOnlyList<DiskSpace> Spaces, string? Error) SampleDiskSpaces()
    {
        var spaces = new List<DiskSpace>();
        string? error = null;
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return (spaces, ex.Message);
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    spaces.Add(new DiskSpace(drive.Name.TrimEnd('\\'), drive.AvailableFreeSpace, drive.TotalSize));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                error ??= $"{drive.Name}: {ex.Message}";
            }
        }
        return (spaces, error);
    }

    private NetworkSample SampleNetwork()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            _hasPreviousNetworkSample = false;
            return new NetworkSample(null, null, null, ex.Message);
        }

        long received = 0;
        long sent = 0;
        var hasActiveNetwork = false;
        var hasRadminAdapter = false;
        var radminConnected = false;
        string? error = null;

        foreach (var adapter in interfaces)
        {
            try
            {
                var addresses = adapter.GetIPProperties().UnicastAddresses;
                var hasIpv4 = addresses.Any(address =>
                    address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(address.Address));
                var isRadmin = adapter.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
                    adapter.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase);

                if (isRadmin)
                {
                    hasRadminAdapter = true;
                    radminConnected |= adapter.OperationalStatus == OperationalStatus.Up && hasIpv4;
                }

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.OperationalStatus != OperationalStatus.Up || !hasIpv4)
                    continue;

                var statistics = adapter.GetIPv4Statistics();
                received = SaturatingAdd(received, statistics.BytesReceived);
                sent = SaturatingAdd(sent, statistics.BytesSent);
                hasActiveNetwork = true;
            }
            catch (NetworkInformationException ex)
            {
                error ??= ex.Message;
            }
        }

        bool? radminState = hasRadminAdapter ? radminConnected : null;
        if (!hasActiveNetwork)
        {
            _hasPreviousNetworkSample = false;
            return new NetworkSample(null, null, radminState, error);
        }

        var timestamp = Stopwatch.GetTimestamp();
        double? download = null;
        double? upload = null;
        if (error is null && _hasPreviousNetworkSample)
        {
            var elapsed = (timestamp - _previousNetworkTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed > 0 && received >= _previousNetworkReceived && sent >= _previousNetworkSent)
            {
                download = (received - _previousNetworkReceived) / elapsed;
                upload = (sent - _previousNetworkSent) / elapsed;
            }
        }

        _previousNetworkReceived = received;
        _previousNetworkSent = sent;
        _previousNetworkTimestamp = timestamp;
        _hasPreviousNetworkSample = error is null;
        return new NetworkSample(download, upload, radminState, error);
    }

    private static long SaturatingAdd(long current, long value) =>
        value > long.MaxValue - current ? long.MaxValue : current + value;

    private readonly record struct NetworkSample(
        double? DownloadBytesPerSecond,
        double? UploadBytesPerSecond,
        bool? RadminVpnConnected,
        string? Error);

    private void EnsureInitialized()
    {
        if (_initialized) return;
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _performanceCounterError = "Не удалось открыть Windows Performance Counters.";
            _initialized = true;
            return;
        }
        _initialized = true;

        if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != 0)
        {
            if (PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) != 0)
                _cpuCounter = IntPtr.Zero;
        }
        if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _cpuFrequencyCounter) != 0)
            _cpuFrequencyCounter = IntPtr.Zero;
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
            _gpuCounter = IntPtr.Zero;
        if (PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _diskReadCounter) != 0)
            _diskReadCounter = IntPtr.Zero;
        if (PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWriteCounter) != 0)
            _diskWriteCounter = IntPtr.Zero;

        if (PdhCollectQueryData(_query) != 0)
            _performanceCounterError = "Windows Performance Counters не вернули первый образец данных.";
    }

    private static double? ReadCounter(IntPtr counter)
    {
        if (counter == IntPtr.Zero ||
            PdhGetFormattedCounterValue(counter, FormatDouble, out _, out var value) != 0 ||
            value.CStatus != 0 ||
            !double.IsFinite(value.Value) ||
            value.Value < 0)
            return null;
        return value.Value;
    }

    private static double? ReadPeakCounter(IntPtr counter)
    {
        uint bytes = 0;
        var result = PdhGetFormattedCounterArray(counter, FormatDouble, ref bytes, out var count, IntPtr.Zero);
        if (result != PdhMoreData || bytes == 0 || count == 0) return null;

        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (PdhGetFormattedCounterArray(counter, FormatDouble, ref bytes, out count, buffer) != 0)
                return null;

            var itemSize = Marshal.SizeOf<CounterValueItem>();
            double peak = 0;
            var hasValue = false;
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterValueItem>(buffer + index * itemSize);
                if (item.Value.CStatus != 0 || !double.IsFinite(item.Value.Value) || item.Value.Value < 0) continue;
                peak = Math.Max(peak, item.Value.Value);
                hasValue = true;
            }
            return hasValue ? Math.Clamp(peak, 0, 100) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            if (_hardwareOpened)
                _hardwareComputer.Close();
            _query = IntPtr.Zero;
            _cpuCounter = IntPtr.Zero;
            _cpuFrequencyCounter = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
            _diskReadCounter = IntPtr.Zero;
            _diskWriteCounter = IntPtr.Zero;
        }
    }

    private sealed record HardwareSensorSample(
        double? CpuTemperatureCelsius,
        double? CpuFrequencyMhz,
        double? GpuTemperatureCelsius,
        double? GpuFrequencyMhz,
        double? GpuMemoryUsedGb,
        double? GpuMemoryTotalGb,
        string? Error)
    {
        public static HardwareSensorSample Empty(string? error) => new(null, null, null, null, null, null, error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint CStatus; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem { public IntPtr Name; public CounterValue Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr values);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
