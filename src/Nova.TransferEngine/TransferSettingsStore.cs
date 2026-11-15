using System.Text.Json;

namespace Nova.TransferEngine;

public sealed class TransferSettingsStore
{
    private readonly string _path;
    private string _destinationDirectory;

    public TransferSettingsStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "transfer-settings.json");
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NOVA", "Полученные файлы");
        try
        {
            var saved = File.Exists(_path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) : null;
            _destinationDirectory = string.IsNullOrWhiteSpace(saved?.DestinationDirectory) ? defaultPath : saved.DestinationDirectory;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _destinationDirectory = defaultPath;
        }
        Directory.CreateDirectory(_destinationDirectory);
    }

    public string DestinationDirectory => _destinationDirectory;

    public void SetDestinationDirectory(string directory)
    {
        var canonical = Path.GetFullPath(directory);
        Directory.CreateDirectory(canonical);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(canonical)));
        File.Move(temporary, _path, true);
        _destinationDirectory = canonical;
    }

    private sealed record Settings(string DestinationDirectory);
}
