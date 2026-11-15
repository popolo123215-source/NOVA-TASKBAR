using System.Text.Json;

namespace Nova.TransferEngine;

public sealed class InternetModeSettingsStore
{
    private readonly string _path;
    public InternetModeSettingsStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "internet-mode.json");
        try { Endpoint = File.Exists(_path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path))?.Endpoint : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Endpoint = null; }
        if (Endpoint is not null && !TryValidate(Endpoint, out _)) Endpoint = null;
    }

    public string? Endpoint { get; private set; }

    public void SetEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Endpoint = null;
        }
        else
        {
            if (!TryValidate(endpoint, out var canonical)) throw new ArgumentException("Use a WSS URL ending in /v1/connect.", nameof(endpoint));
            Endpoint = canonical.ToString();
        }
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Settings(Endpoint)));
        File.Move(temp, _path, true);
    }

    public static bool TryValidate(string value, out Uri endpoint)
    {
        var valid = Uri.TryCreate(value, UriKind.Absolute, out endpoint!) && endpoint.Scheme == Uri.UriSchemeWss &&
            string.IsNullOrEmpty(endpoint.UserInfo) && endpoint.AbsolutePath == "/v1/connect";
        return valid;
    }

    private sealed record Settings(string? Endpoint);
}
