using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;

namespace Nova.WindowsClient.Services;

internal sealed class BrowserWindowAdapter : IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwRestore = 9;
    private static readonly HashSet<string> BrowserNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc"
        };

    private readonly Func<Rectangle?> _capsuleBounds;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<IntPtr, AdaptedWindow> _adapted = [];
    private bool _enabled;
    private bool _disposed;

    public BrowserWindowAdapter(Func<Rectangle?> capsuleBounds)
    {
        _capsuleBounds = capsuleBounds;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }

    public event Action<string>? StatusChanged;
    public bool IsEnabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        if (_enabled)
        {
            _timer.Start();
            Refresh();
        }
        else
        {
            _timer.Stop();
            var failure = RestoreAll();
            StatusChanged?.Invoke(failure ?? "Адаптация окон браузеров выключена.");
        }
    }

    public void Refresh()
    {
        if (!_enabled || _disposed) return;

        var capsule = _capsuleBounds();
        if (capsule is null)
        {
            var restoreFailure = RestoreAll();
            StatusChanged?.Invoke(restoreFailure ??
                "Адаптация активна для компактной капсулы; в раскрытой панели и полноэкранном режиме она приостанавливается.");
            return;
        }

        var seen = new HashSet<IntPtr>();
        var adjusted = 0;
        var failure = "";
        EnumWindows((handle, _) =>
        {
            if (!IsBrowserWindow(handle, out var processId)) return true;
            seen.Add(handle);
            if (IsIconic(handle) || !TryGetWindowRectangle(handle, out var currentBounds)) return true;

            var monitor = Screen.FromRectangle(currentBounds);
            var capsuleOnMonitor = Rectangle.Intersect(capsule.Value, monitor.Bounds);
            if (capsuleOnMonitor.IsEmpty)
            {
                failure = RestoreIfNoLongerNeeded(handle, capsule.Value) ?? failure;
                return true;
            }

            var placement = NewWindowPlacement();
            if (!GetWindowPlacement(handle, ref placement))
            {
                if (IsWindow(handle))
                    failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return true;
            }

            _adapted.TryGetValue(handle, out var existing);
            if (existing is not null && currentBounds != existing.LastAppliedBounds)
            {
                existing = new AdaptedWindow(processId, placement, currentBounds, Rectangle.Empty);
                _adapted[handle] = existing;
            }

            var referenceBounds = existing is not null
                ? existing.OriginalBounds
                : currentBounds;
            if (!Screen.FromRectangle(referenceBounds).DeviceName.Equals(monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                referenceBounds = currentBounds;
                if (existing is not null)
                {
                    existing = new AdaptedWindow(processId, placement, currentBounds, Rectangle.Empty);
                    _adapted[handle] = existing;
                }
            }

            if (!referenceBounds.IntersectsWith(capsuleOnMonitor))
            {
                failure = RestoreIfNoLongerNeeded(handle, capsule.Value) ?? failure;
                return true;
            }

            var target = BrowserWindowGeometry.FindNonOverlappingBounds(referenceBounds, capsuleOnMonitor, monitor.WorkingArea);
            if (target is null)
            {
                failure = "Не удалось подобрать свободную область для окна браузера на этом мониторе.";
                return true;
            }
            if (target.Value == currentBounds) return true;

            existing ??= new AdaptedWindow(processId, placement, currentBounds, Rectangle.Empty);
            if (placement.ShowCommand == (uint)ShowWindowCommand.Maximize)
                ShowWindowAsync(handle, SwRestore);

            if (SetWindowPos(handle, IntPtr.Zero, target.Value.Left, target.Value.Top, target.Value.Width, target.Value.Height,
                    SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged))
            {
                existing = existing with { LastAppliedBounds = target.Value };
                _adapted[handle] = existing;
                adjusted++;
            }
            else
            {
                failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            }
            return true;
        }, IntPtr.Zero);

        foreach (var handle in _adapted.Keys.Where(handle => !seen.Contains(handle)).ToArray())
            _adapted.Remove(handle);

        if (!string.IsNullOrEmpty(failure))
            StatusChanged?.Invoke($"Адаптация окон: {failure}");
        else
            StatusChanged?.Invoke(adjusted > 0
                ? $"Адаптация активна: скорректировано окон браузеров — {adjusted}."
                : "Адаптация активна; окна поддерживаемых браузеров не перекрывают область капсулы.");
    }

    private bool IsBrowserWindow(IntPtr handle, out int processId)
    {
        processId = 0;
        if (!IsWindowVisible(handle) || GetWindow(handle, 4) != IntPtr.Zero) return false;
        var extendedStyle = IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64()
            : GetWindowLong32(handle, GwlExStyle);
        if ((extendedStyle & (WsExToolWindow | WsExNoActivate)) != 0) return false;

        GetWindowThreadProcessId(handle, out var id);
        processId = unchecked((int)id);
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return BrowserNames.Contains(process.ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private string? RestoreIfNoLongerNeeded(IntPtr handle, Rectangle capsule)
    {
        if (!_adapted.TryGetValue(handle, out var adapted) || adapted.OriginalBounds.IntersectsWith(capsule))
            return null;
        return Restore(handle, adapted);
    }

    private string? RestoreAll()
    {
        string? failure = null;
        foreach (var (handle, adapted) in _adapted.ToArray())
        {
            var restoreFailure = Restore(handle, adapted);
            failure ??= restoreFailure;
        }
        return failure;
    }

    private string? Restore(IntPtr handle, AdaptedWindow adapted)
    {
        GetWindowThreadProcessId(handle, out var processId);
        if (processId != adapted.ProcessId || !IsWindow(handle))
        {
            _adapted.Remove(handle);
            return null;
        }
        var placement = adapted.OriginalPlacement;
        if (!SetWindowPlacement(handle, ref placement))
            return $"Не удалось восстановить исходное положение окна браузера: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        _adapted.Remove(handle);
        return null;
    }

    private static bool TryGetWindowRectangle(IntPtr handle, out Rectangle rectangle)
    {
        if (GetWindowRect(handle, out var bounds))
        {
            rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            return true;
        }
        rectangle = Rectangle.Empty;
        return false;
    }

    private static WindowPlacement NewWindowPlacement() => new()
    {
        Length = Marshal.SizeOf<WindowPlacement>()
    };

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        _ = RestoreAll();
        _disposed = true;
    }

    private sealed record AdaptedWindow(int ProcessId, WindowPlacement OriginalPlacement, Rectangle OriginalBounds, Rectangle LastAppliedBounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public uint ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }
    private enum ShowWindowCommand : uint { Maximize = 3 }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr handle, int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr handle, int command);
}
