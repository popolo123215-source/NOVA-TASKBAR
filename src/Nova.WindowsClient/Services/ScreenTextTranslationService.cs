using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace Nova.WindowsClient.Services;

internal sealed class ScreenTextTranslationService
{
    private const int MaximumCharacters = 500;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "de", "en", "es", "fr", "hi", "it", "ja", "ko", "nl", "pl", "pt", "ru", "tr", "uk", "zh"
    };

    public static string CleanFormatting(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\f', '\n');
        var output = new List<string>();
        var previousWasBlank = false;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = string.Join(' ', rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0)
            {
                if (output.Count > 0 && !previousWasBlank)
                    output.Add("");
                previousWasBlank = true;
                continue;
            }

            var isListItem = line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("• ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal) ||
                line.StartsWith("1. ", StringComparison.Ordinal);
            if (output.Count > 0 && !previousWasBlank && !isListItem)
                output[^1] = $"{output[^1]} {line}";
            else
                output.Add(line);
            previousWasBlank = false;
        }

        return string.Join(Environment.NewLine, output).Trim();
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Нет текста для перевода.", nameof(text));
        if (text.Length > MaximumCharacters)
            throw new ArgumentException($"Бесплатный сервис принимает не больше {MaximumCharacters} символов за запрос. Выделите меньшую область.", nameof(text));

        var source = NormalizeLanguage(sourceLanguage);
        var target = NormalizeLanguage(targetLanguage);
        if (!SupportedLanguages.Contains(source) || !SupportedLanguages.Contains(target))
            throw new ArgumentException("Выбранная пара языков не поддерживается используемым сервисом.");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Исходный и целевой языки совпадают.");

        var requestUri = new Uri(
            $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(source)}%7C{Uri.EscapeDataString(target)}");
        using var response = await Client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("responseStatus", out var responseStatus) ||
            responseStatus.GetInt32() is not 200)
        {
            var details = root.TryGetProperty("responseDetails", out var responseDetails)
                ? responseDetails.GetString()
                : null;
            throw new InvalidOperationException(details ?? "Сервис перевода не выполнил запрос.");
        }

        var translated = root.GetProperty("responseData").GetProperty("translatedText").GetString();
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidDataException("Сервис перевода вернул пустой результат.");
        return WebUtility.HtmlDecode(translated);
    }

    private static string NormalizeLanguage(string language) =>
        language.Split('-', StringSplitOptions.RemoveEmptyEntries)[0].Trim().ToLowerInvariant();
}
