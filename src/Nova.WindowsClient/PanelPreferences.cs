using System.IO;
using System.Text.Json;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

internal sealed record PanelPreferences(
    string? DockEdge,
    double Left,
    double Top,
    double SizeScale,
    double Opacity,
    string City = "",
    string? ScreenDevice = null,
    WeatherLocation? WeatherLocation = null,
    double OverlayLeft = 24,
    double OverlayTop = 24,
    double OverlayScale = 1,
    string? OverlayScreenDevice = null,
    bool? KeepOnTop = null,
    bool? HoverExpandEnabled = null,
    int? HoverCollapseDelayMs = null,
    int? WeatherRefreshMinutes = null,
    int? SystemRefreshSeconds = null,
    bool? ShowCapsuleCpu = null,
    bool? ShowCapsuleGpu = null,
    bool? ShowCapsuleRam = null,
    bool? ShowOverlayCpu = null,
    bool? ShowOverlayGpu = null,
    bool? ShowOverlayRam = null,
    bool? TimerCompletionSound = null,
    bool? EnableMotion = null,
    bool? EnableAudioReactive = null,
    double? AudioReactiveIntensity = null,
    string? SelectedWidget = null,
    double? WorkspaceLeft = null,
    double? WorkspaceTop = null,
    double? WorkspaceWidth = null,
    double? WorkspaceHeight = null,
    string? WorkspaceScreenDevice = null,
    bool? BrowserAdaptationEnabled = null,
    bool? ShowCapsuleClock = null,
    bool? ShowCapsuleWeather = null,
    bool? ShowCapsuleMedia = null,
    bool? ShowCapsuleArtwork = null,
    bool? ShowCapsulePlaybackStatus = null,
    bool? ShowCapsuleNetwork = null,
    bool? ShowCapsuleVpn = null,
    string? CapsuleContentOrder = null,
    double? WidgetIconSize = null)
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NOVA", "TaskBar", "settings.json");

    public static PanelPreferences Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<PanelPreferences>(File.ReadAllText(SettingsPath)) ?? Defaults;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return Defaults;
    }

    public static PanelPreferences Defaults => new("top", -1, -1, 1, 1, "", null, null, 24, 24, 1, null);

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
            File.Move(temporaryPath, SettingsPath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
