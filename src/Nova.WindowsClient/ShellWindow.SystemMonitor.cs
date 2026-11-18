using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private const int SystemGraphSampleCount = 60;
    private readonly List<double> _cpuHistory = [];
    private readonly List<double> _gpuHistory = [];
    private readonly List<double> _ramHistory = [];
    private readonly List<double> _networkHistory = [];
    private readonly List<double> _diskHistory = [];

    private void UpdateSystemMetricVisibility()
    {
        MonitorCpuPanel.Visibility = _showMonitorCpu ? Visibility.Visible : Visibility.Collapsed;
        MonitorGpuPanel.Visibility = _showMonitorGpu ? Visibility.Visible : Visibility.Collapsed;
        MonitorRamPanel.Visibility = _showMonitorRam ? Visibility.Visible : Visibility.Collapsed;
        MonitorNetworkPanel.Visibility = _showMonitorNetwork ? Visibility.Visible : Visibility.Collapsed;
        MonitorDiskPanel.Visibility = _showMonitorDisk ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSystemMonitorUi(SystemStatsService.Snapshot snapshot)
    {
        CpuUsageText.Text = FormatPercent(snapshot.CpuPercent);
        CpuUsageBar.Visibility = snapshot.CpuPercent is null ? Visibility.Collapsed : Visibility.Visible;
        CpuUsageBar.Value = snapshot.CpuPercent ?? 0;
        CpuDetailsText.Text =
            $"Частота: {FormatFrequency(snapshot.CpuFrequencyMhz)} · температура: {FormatTemperature(snapshot.CpuTemperatureCelsius)}";

        GpuUsageText.Text = FormatPercent(snapshot.Gpu3DPercent);
        GpuUsageBar.Visibility = snapshot.Gpu3DPercent is null ? Visibility.Collapsed : Visibility.Visible;
        GpuUsageBar.Value = snapshot.Gpu3DPercent ?? 0;
        var gpuMemory = snapshot.GpuMemoryUsedGb is { } usedMemory
            ? snapshot.GpuMemoryTotalGb is { } totalMemory
                ? $"{usedMemory:0.0}/{totalMemory:0.0} ГБ"
                : $"{usedMemory:0.0} ГБ · объём недоступен"
            : "н/д";
        GpuDetailsText.Text =
            $"Частота: {FormatFrequency(snapshot.GpuFrequencyMhz)} · температура: {FormatTemperature(snapshot.GpuTemperatureCelsius)} · VRAM: {gpuMemory}";

        var ramPercent = snapshot.RamTotalGb > 0 ? snapshot.RamUsedGb / snapshot.RamTotalGb * 100 : (double?)null;
        RamUsageText.Text = snapshot.RamTotalGb > 0
            ? $"{snapshot.RamUsedGb:0.0}/{snapshot.RamTotalGb:0.0} ГБ · {ramPercent:0}%"
            : "н/д";
        RamUsageBar.Value = Math.Clamp(ramPercent ?? 0, 0, 100);
        RamAvailableText.Text = snapshot.RamTotalGb > 0
            ? $"Доступно: {Math.Max(0, snapshot.RamTotalGb - snapshot.RamUsedGb):0.0} ГБ"
            : "Доступно: н/д";

        NetworkUsageText.Text =
            $"↓ {FormatRate(snapshot.NetworkDownloadBytesPerSecond)} · ↑ {FormatRate(snapshot.NetworkUploadBytesPerSecond)}";
        DiskUsageText.Text =
            $"Чтение {FormatRate(snapshot.DiskReadBytesPerSecond)} · запись {FormatRate(snapshot.DiskWriteBytesPerSecond)}";
        DiskSpaceText.Text = snapshot.DiskSpaces.Count == 0
            ? "Свободное место: н/д"
            : string.Join("   ·   ", snapshot.DiskSpaces.Select(disk =>
                $"{disk.Name} {disk.AvailableBytes / 1_073_741_824d:0.0}/{disk.TotalBytes / 1_073_741_824d:0.0} ГБ свободно"));

        AddGraphSample(_cpuHistory, snapshot.CpuPercent);
        AddGraphSample(_gpuHistory, snapshot.Gpu3DPercent);
        AddGraphSample(_ramHistory, ramPercent);
        AddGraphSample(_networkHistory, SumRates(snapshot.NetworkDownloadBytesPerSecond, snapshot.NetworkUploadBytesPerSecond));
        AddGraphSample(_diskHistory, SumRates(snapshot.DiskReadBytesPerSecond, snapshot.DiskWriteBytesPerSecond));
        UpdateGraph(CpuGraphLine, _cpuHistory, 100);
        UpdateGraph(GpuGraphLine, _gpuHistory, 100);
        UpdateGraph(RamGraphLine, _ramHistory, 100);
        UpdateGraph(NetworkGraphLine, _networkHistory, null);
        UpdateGraph(DiskGraphLine, _diskHistory, null);

        OverlayCpuText.Text = snapshot.CpuPercent is double cpu ? $"{cpu:0}%" : "н/д";
        OverlayGpuText.Text = snapshot.Gpu3DPercent is double gpu ? $"{gpu:0}%" : "н/д";
        OverlayRamText.Text = snapshot.RamTotalGb > 0
            ? $"{snapshot.RamUsedGb:0.0}/{snapshot.RamTotalGb:0.0} ГБ"
            : "н/д";
        UpdateCompactMetrics();

        var notices = new List<string>();
        if (snapshot.HardwareSensorError is { Length: > 0 } sensorError)
            notices.Add($"Аппаратные датчики: {sensorError}");
        else if (snapshot.CpuTemperatureCelsius is null &&
                 snapshot.GpuTemperatureCelsius is null &&
                 snapshot.CpuFrequencyMhz is null &&
                 snapshot.GpuFrequencyMhz is null)
            notices.Add("Датчики температуры и частоты не обнаружены или недоступны.");
        if (snapshot.CpuPercent is null || snapshot.Gpu3DPercent is null)
            notices.Add("Недоступные счётчики CPU/GPU отображаются как «н/д».");
        if (snapshot.NetworkError is { Length: > 0 } networkError)
            notices.Add($"Сеть: {networkError}");
        if (snapshot.DiskSpaceError is { Length: > 0 } diskError)
            notices.Add($"Диски: {diskError}");
        if (snapshot.PerformanceCounterError is { Length: > 0 } counterError)
            notices.Add(counterError);
        if (snapshot.DiskSpaces.Count == 0)
            notices.Add("Свободное место дисков недоступно.");
        notices.Add($"Обновление каждые {_systemStatsTimer.Interval.TotalSeconds:0} с.");
        SystemStatusText.Text = string.Join(" ", notices);
    }

    private static double? SumRates(double? first, double? second) =>
        first is null && second is null ? null : (first ?? 0) + (second ?? 0);

    private static void AddGraphSample(List<double> history, double? value)
    {
        if (value is not { } sample || !double.IsFinite(sample))
        {
            history.Clear();
            return;
        }
        history.Add(sample);
        if (history.Count > SystemGraphSampleCount)
            history.RemoveAt(0);
    }

    private static void UpdateGraph(Polyline graph, IReadOnlyList<double> history, double? fixedMaximum)
    {
        var points = new PointCollection();
        if (history.Count == 0)
        {
            graph.Points = points;
            return;
        }

        var maximum = fixedMaximum ?? Math.Max(1, history.Max());
        var divisor = Math.Max(1, history.Count - 1);
        for (var index = 0; index < history.Count; index++)
        {
            var x = index * 120d / divisor;
            var y = 22 - Math.Clamp(history[index] / maximum, 0, 1) * 20;
            points.Add(new System.Windows.Point(x, y));
        }
        graph.Points = points;
    }

    private static string FormatTemperature(double? value) =>
        value is { } celsius ? $"{celsius:0} °C" : "н/д";

    private static string FormatFrequency(double? value) =>
        value is { } megahertz
            ? megahertz >= 1000
                ? $"{megahertz / 1000:0.00} ГГц"
                : $"{megahertz:0} МГц"
            : "н/д";

    private static string FormatRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } rate)
            return "н/д";
        if (rate >= 1_048_576)
            return $"{rate / 1_048_576:0.0} МБ/с";
        if (rate >= 1024)
            return $"{rate / 1024:0} КБ/с";
        return $"{rate:0} Б/с";
    }
}
