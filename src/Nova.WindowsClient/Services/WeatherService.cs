using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nova.WindowsClient.Services;

internal sealed record WeatherLocation(
    string Name,
    string? Region,
    string? Country,
    double Latitude,
    double Longitude,
    string? TimeZone)
{
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var parts = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(Region) && !string.Equals(Name, Region, StringComparison.OrdinalIgnoreCase))
                parts.Add(Region);
            if (!string.IsNullOrWhiteSpace(Country) && !parts.Contains(Country, StringComparer.OrdinalIgnoreCase))
                parts.Add(Country);
            return string.Join(", ", parts);
        }
    }
}

internal sealed record WeatherSnapshot(string Place, double TemperatureC, int Code, bool IsDay, DateTimeOffset UpdatedAt)
{
    public string Condition => Code switch
    {
        0 => "Ясно",
        1 => "Преимущественно ясно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 or 48 => "Туман",
        >= 51 and <= 57 => "Морось",
        >= 61 and <= 67 => "Дождь",
        >= 71 and <= 77 => "Снег",
        >= 80 and <= 82 => "Ливень",
        85 or 86 => "Снегопад",
        >= 95 => "Гроза",
        _ => "Условия не определены"
    };

    public string Glyph => Code switch
    {
        0 or 1 => IsDay ? "☀" : "☾",
        2 => "⛅",
        45 or 48 => "≋",
        >= 51 and <= 67 or >= 80 and <= 82 => "☂",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⛈",
        _ => "☁"
    };
}

internal sealed class WeatherService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NOVA-TaskBar/1.0");
        return client;
    }

    public async Task<IReadOnlyList<WeatherLocation>> SearchLocationsAsync(string city, CancellationToken cancellationToken)
    {
        city = city.Trim();
        if (city.Length is < 2 or > 100)
            throw new ArgumentException("Укажите название города (от 2 до 100 символов).", nameof(city));

        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=8&language=ru&format=json";
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            new JsonDocumentOptions { MaxDepth = 16 });

        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var locations = new List<WeatherLocation>();
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("name", out var nameElement) ||
                !result.TryGetProperty("latitude", out var latitudeElement) ||
                !result.TryGetProperty("longitude", out var longitudeElement))
                continue;

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            locations.Add(new WeatherLocation(
                name,
                ReadOptionalString(result, "admin1"),
                ReadOptionalString(result, "country"),
                latitudeElement.GetDouble(),
                longitudeElement.GetDouble(),
                ReadOptionalString(result, "timezone")));
        }

        return locations;
    }

    public async Task<WeatherSnapshot> GetCurrentAsync(WeatherLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&current=temperature_2m,weather_code,is_day&temperature_unit=celsius&timezone=auto");
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            new JsonDocumentOptions { MaxDepth = 16 });

        var current = document.RootElement.GetProperty("current");
        var temperature = current.GetProperty("temperature_2m").GetDouble();
        var code = current.GetProperty("weather_code").GetInt32();
        var isDay = current.GetProperty("is_day").GetInt32() == 1;
        var updated = DateTimeOffset.TryParse(current.GetProperty("time").GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var time) ? time : DateTimeOffset.Now;
        return new WeatherSnapshot(location.DisplayName, temperature, code, isDay, updated);
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
