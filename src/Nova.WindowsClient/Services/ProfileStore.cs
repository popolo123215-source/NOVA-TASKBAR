using System.IO;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal sealed class ProfileStore
{
    private const string FormatName = "NOVA-PROFILES";
    private const int CurrentVersion = 1;
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public string? LastError { get; private set; }
    public bool IsAvailable { get; private set; }

    public ProfileStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        Directory.CreateDirectory(stateDirectory);
        _filePath = Path.Combine(stateDirectory, "profiles.json");
    }

    public ProfileDocument Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                IsAvailable = true;
                return ProfileService.CreateDefaultDocument();
            }
            var document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(_filePath), JsonOptions);
            if (document is null || document.Format != FormatName ||
                document.Version is < 1 or > CurrentVersion ||
                document.Profiles.Count == 0)
                throw new JsonException("Неверный формат файла профилей.");
            IsAvailable = true;
            LastError = null;
            return document with
            {
                Format = FormatName,
                Version = CurrentVersion,
                Profiles = ProfileService.MergeBuiltInProfiles(document.Profiles)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            IsAvailable = false;
            LastError = $"Профили недоступны: не удалось прочитать profiles.json. {ex.Message}";
            if (File.Exists(_filePath))
            {
                try
                {
                    var backup = _filePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
                    File.Copy(_filePath, backup, true);
                }
                catch (Exception copyError) when (
                    copyError is IOException or UnauthorizedAccessException)
                {
                    // The original file remains untouched; startup uses the in-memory default state.
                }
            }
            return ProfileService.CreateDefaultDocument();
        }
    }

    public void Save(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                document with { Format = FormatName, Version = CurrentVersion },
                JsonOptions));
            File.Move(temporary, _filePath, true);
            IsAvailable = true;
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsAvailable = false;
            LastError = $"Не удалось сохранить профили: {ex.Message}";
        }
    }
}
