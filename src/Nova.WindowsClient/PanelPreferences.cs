using System.IO;
using System.Text.Json;

namespace Nova.WindowsClient;

internal sealed record PanelPreferences(string? DockEdge, double Left, double Top, double SizeScale, double Opacity)
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

    public static PanelPreferences Defaults => new("right", -1, -1, 1, 0.96);

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
