using System.Text.Json;
using Nova.Core.Contracts;

namespace Nova.TransferEngine;

public sealed class TransferSettingsStore
{
    private readonly string _path;
    private string _destinationDirectory;
    private TransferTransportPreference _transportPreference;

    public TransferSettingsStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "transfer-settings.json");
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NOVA", "Полученные файлы");
        try
        {
            var saved = File.Exists(_path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) : null;
            _destinationDirectory = string.IsNullOrWhiteSpace(saved?.DestinationDirectory) ? defaultPath : saved.DestinationDirectory;
            _transportPreference = saved is not null && Enum.IsDefined(typeof(TransferTransportPreference), saved.TransportPreference)
                ? saved.TransportPreference : TransferTransportPreference.Automatic;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _destinationDirectory = defaultPath;
            _transportPreference = TransferTransportPreference.Automatic;
        }
        Directory.CreateDirectory(_destinationDirectory);
    }

    public string DestinationDirectory => _destinationDirectory;
    public TransferTransportPreference TransportPreference => _transportPreference;

    public void SetDestinationDirectory(string directory)
    {
        var canonical = Path.GetFullPath(directory);
        Directory.CreateDirectory(canonical);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(canonical, _transportPreference)));
        File.Move(temporary, _path, true);
        _destinationDirectory = canonical;
    }

    public void SetTransportPreference(TransferTransportPreference preference)
    {
        if (!Enum.IsDefined(typeof(TransferTransportPreference), preference))
            throw new ArgumentOutOfRangeException(nameof(preference));
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(_destinationDirectory, preference)));
        File.Move(temporary, _path, true);
        _transportPreference = preference;
    }

    private sealed record Settings(string DestinationDirectory, TransferTransportPreference TransportPreference = TransferTransportPreference.Automatic);
}
