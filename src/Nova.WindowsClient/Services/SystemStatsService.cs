using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Nova.WindowsClient.Services;

// Adapted from Atoll's MIT-licensed PDH/GPU telemetry approach; see licenses/Atoll.LICENSE.
internal sealed class SystemStatsService : IDisposable
{
    internal sealed record Snapshot(
        double? CpuPercent,
        double? Gpu3DPercent,
        double RamUsedGb,
        double RamTotalGb,
        double? NetworkDownloadBytesPerSecond,
        double? NetworkUploadBytesPerSecond,
        bool? RadminVpnConnected,
        string? NetworkError);

    private const uint FormatDouble = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);
    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _cpuCounter;
    private IntPtr _gpuCounter;
    private bool _initialized;
    private bool _disposed;
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
            double? gpu = null;
            if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
            {
                if (_cpuCounter != IntPtr.Zero &&
                    PdhGetFormattedCounterValue(_cpuCounter, FormatDouble, out _, out var cpuValue) == 0 &&
                    cpuValue.CStatus == 0)
                    cpu = Math.Clamp(cpuValue.Value, 0, 100);

                if (_gpuCounter != IntPtr.Zero)
                    gpu = ReadPeakCounter(_gpuCounter);
            }

            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить состояние оперативной памяти.");

            var total = memory.TotalPhysical / 1_073_741_824d;
            var used = total - memory.AvailablePhysical / 1_073_741_824d;
            var network = SampleNetwork();
            return new Snapshot(
                cpu,
                gpu,
                Math.Max(0, used),
                total,
                network.DownloadBytesPerSecond,
                network.UploadBytesPerSecond,
                network.RadminVpnConnected,
                network.Error);
        }
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
        _initialized = true;
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            throw new Win32Exception("Не удалось открыть Windows Performance Counters.");

        if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != 0)
        {
            if (PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) != 0)
                _cpuCounter = IntPtr.Zero;
        }
        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
            _gpuCounter = IntPtr.Zero;

        PdhCollectQueryData(_query);
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
                if (item.Value.CStatus != 0) continue;
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
            _query = IntPtr.Zero;
            _cpuCounter = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
        }
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
