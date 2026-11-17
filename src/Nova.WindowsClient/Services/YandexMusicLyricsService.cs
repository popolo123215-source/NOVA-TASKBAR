using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nova.WindowsClient.Services;

internal sealed record TimedLyricLine(TimeSpan Position, string Text);

internal sealed record YandexLyricsDocument(IReadOnlyList<TimedLyricLine> Lines, string PlainText);

internal sealed class YandexMusicLyricsService
{
    private const string ApiBaseUrl = "https://api.music.yandex.net";
    private const string LyricsSigningKey = "p93jhgh689SBReK6ghtw62";
    private const string YandexMusicClientHeader = "YandexMusicAndroid/24023621";
    private const int MaximumApiResponseBytes = 2 * 1024 * 1024;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex TimestampPattern = new(
        @"\[(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?\]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<YandexLyricsDocument> GetLyricsAsync(
        string title,
        string artist,
        string oauthToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Для поиска текста требуется название трека.", nameof(title));
        if (string.IsNullOrWhiteSpace(artist))
            throw new ArgumentException("Для точного поиска текста требуется исполнитель.", nameof(artist));
        if (string.IsNullOrWhiteSpace(oauthToken))
            throw new ArgumentException("Введите OAuth-токен Яндекс Музыки.", nameof(oauthToken));

        var token = NormalizeToken(oauthToken);
        if (token.Length > 4096)
            throw new ArgumentException("OAuth-токен слишком длинный.", nameof(oauthToken));
        var trackId = await FindExactTrackAsync(title, artist, token, cancellationToken);
        return await DownloadLyricsAsync(trackId, token, cancellationToken);
    }

    internal static YandexLyricsDocument ParseLrc(string content)
    {
        var timedLines = new List<(TimedLyricLine Line, int Order)>();
        var plainLines = new List<string>();
        var order = 0;

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var matches = TimestampPattern.Matches(rawLine);
            var text = TimestampPattern.Replace(rawLine, "").Trim();
            if (matches.Count == 0)
            {
                if (!IsMetadataLine(text) && text.Length > 0)
                    plainLines.Add(text);
                continue;
            }

            if (text.Length == 0)
                continue;

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fractionText = match.Groups[3].Value;
                var milliseconds = fractionText.Length switch
                {
                    1 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 10,
                    3 => int.Parse(fractionText, CultureInfo.InvariantCulture),
                    _ => 0
                };
                var position = TimeSpan.FromMilliseconds((minutes * 60L + seconds) * 1000 + milliseconds);
                timedLines.Add((new TimedLyricLine(position, text), order++));
            }
        }

        var sortedLines = timedLines
            .OrderBy(item => item.Line.Position)
            .ThenBy(item => item.Order)
            .Select(item => item.Line)
            .ToArray();
        return new YandexLyricsDocument(sortedLines, string.Join(Environment.NewLine, plainLines));
    }

    private static async Task<string> FindExactTrackAsync(
        string title,
        string artist,
        string token,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"{title} {artist}");
        using var request = CreateAuthenticatedRequest(
            HttpMethod.Get,
            $"{ApiBaseUrl}/search?text={query}&type=track&page=0&nocorrect=false",
            token);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Яндекс Музыка отклонила OAuth-токен. Проверьте его и повторите попытку.");
        response.EnsureSuccessStatusCode();

        var json = await ReadBoundedContentAsync(response.Content, MaximumApiResponseBytes, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tracks", out var tracks) ||
            !tracks.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Ответ поиска Яндекс Музыки не содержит список треков.");
        var normalizedTitle = NormalizeMatchText(title);
        var normalizedArtists = artist
            .Split([',', ';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeMatchText)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var track in results.EnumerateArray())
        {
            var candidateTitle = GetString(track, "title");
            if (!string.Equals(NormalizeMatchText(candidateTitle), normalizedTitle, StringComparison.Ordinal))
                continue;

            if (!track.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
                continue;
            var candidateArtists = artists.EnumerateArray()
                .Select(item => NormalizeMatchText(GetString(item, "name")))
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var artistMatches = normalizedArtists.Count > 0 && normalizedArtists.All(candidateArtists.Contains);
            if (!artistMatches)
                continue;

            var id = track.TryGetProperty("id", out var idElement)
                ? idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.GetRawText()
                : null;
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }

        throw new KeyNotFoundException("В Яндекс Музыке не найдено точное совпадение названия и исполнителя.");
    }

    private static async Task<YandexLyricsDocument> DownloadLyricsAsync(
        string trackId,
        string token,
        CancellationToken cancellationToken)
    {
        var numericTrackId = trackId.Split(':', 2)[0];
        if (!long.TryParse(numericTrackId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("Яндекс Музыка вернула некорректный идентификатор трека.");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sign = CreateLyricsSignature(numericTrackId, timestamp);
        var escapedTrackId = Uri.EscapeDataString(trackId);
        var lyricsUri = $"{ApiBaseUrl}/tracks/{escapedTrackId}/lyrics?format=LRC&timeStamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, lyricsUri, token);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Яндекс Музыка не разрешила доступ к тексту. Проверьте OAuth-токен.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException("Для этого трека в Яндекс Музыке нет доступного текста.");
        response.EnsureSuccessStatusCode();

        var json = await ReadBoundedContentAsync(response.Content, MaximumApiResponseBytes, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("downloadUrl", out var downloadUrlElement) ||
            downloadUrlElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Яндекс Музыка не вернула ссылку для скачивания текста.");

        var downloadUrl = downloadUrlElement.GetString();
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(downloadUri.UserInfo) ||
            !(downloadUri.Host.Equals("yandex.net", StringComparison.OrdinalIgnoreCase) ||
              downloadUri.Host.EndsWith(".yandex.net", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Яндекс Музыка вернула небезопасную ссылку на текст песни.");

        using var lyricsResponse = await HttpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        lyricsResponse.EnsureSuccessStatusCode();
        var lyricsBytes = await ReadBoundedContentAsync(lyricsResponse.Content, MaximumApiResponseBytes, cancellationToken);
        return ParseLrc(Encoding.UTF8.GetString(lyricsBytes));
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        request.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", YandexMusicClientHeader);
        return request;
    }

    private static async Task<byte[]> ReadBoundedContentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Ответ Яндекс Музыки превышает допустимый размер.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("Ответ Яндекс Музыки превышает допустимый размер.");
            buffer.Write(chunk, 0, read);
        }
    }

    private static string CreateLyricsSignature(string trackId, long timestamp)
    {
        var message = Encoding.UTF8.GetBytes($"{trackId}{timestamp}");
        var key = Encoding.UTF8.GetBytes(LyricsSigningKey);
        return Convert.ToBase64String(HMACSHA256.HashData(key, message));
    }

    private static string NormalizeToken(string token)
    {
        var value = token.Trim();
        return value.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase)
            ? value["OAuth ".Length..].Trim()
            : value;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";

    private static string NormalizeMatchText(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static bool IsMetadataLine(string line) =>
        line.StartsWith("[ar:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[ti:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[al:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[by:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase);
}
