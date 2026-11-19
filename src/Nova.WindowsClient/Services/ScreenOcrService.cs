using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Windows.Globalization;
using Bitmap = System.Drawing.Bitmap;
using Rectangle = System.Drawing.Rectangle;

namespace Nova.WindowsClient.Services;

internal sealed record OcrLanguageOption(string DisplayName, string LanguageTag)
{
    public override string ToString() => DisplayName;
}

internal sealed class ScreenOcrService
{
    private const long MaximumCapturePixels = 16_000_000;

    public IReadOnlyList<OcrLanguageOption> GetAvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages
            .Select(language => new OcrLanguageOption(language.DisplayName, language.LanguageTag))
            .OrderBy(language => language.DisplayName, StringComparer.CurrentCulture)
            .ToArray();

    public async Task<string> RecognizeAsync(Rectangle screenRegion, string? languageTag, CancellationToken cancellationToken = default)
    {
        if (screenRegion.Width <= 0 || screenRegion.Height <= 0 ||
            (long)screenRegion.Width * screenRegion.Height > MaximumCapturePixels)
            throw new ArgumentOutOfRangeException(nameof(screenRegion), "Выделите непустую область не больше 16 мегапикселей.");

        var imageBytes = await Task.Run(() => CapturePng(screenRegion), cancellationToken);
        return await RecognizeImageAsync(imageBytes, languageTag, cancellationToken);
    }

    internal async Task<string> RecognizeImageAsync(byte[] imageBytes, string? languageTag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            throw new ArgumentException("Изображение пустое.", nameof(imageBytes));
        cancellationToken.ThrowIfCancellationRequested();

        var engine = string.IsNullOrWhiteSpace(languageTag)
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        if (engine is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(languageTag)
                ? "Windows OCR не нашёл языковой пакет для языков профиля пользователя. Установите языковой пакет с компонентом «Оптическое распознавание символов»."
                : $"Языковой пакет OCR «{languageTag}» не установлен. Добавьте его в языковых параметрах Windows.");

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccessStream))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        randomAccessStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(softwareBitmap);
        return NormalizeOcrText(result.Text);
    }

    private static byte[] CapturePng(Rectangle screenRegion)
    {
        using var bitmap = new Bitmap(screenRegion.Width, screenRegion.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(screenRegion.Location, Point.Empty, screenRegion.Size, CopyPixelOperation.SourceCopy);
            graphics.Flush();
        }

        var scale = screenRegion.Width < 1200 || screenRegion.Height < 800 ? 2 : 1;
        using var prepared = scale == 1
            ? (Bitmap)new Bitmap(bitmap)
            : new Bitmap(bitmap.Width * scale, bitmap.Height * scale, PixelFormat.Format32bppArgb);
        if (scale > 1)
        {
            using var graphics = Graphics.FromImage(prepared);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(bitmap, new Rectangle(0, 0, prepared.Width, prepared.Height));
        }

        using var imageStream = new MemoryStream();
        prepared.Save(imageStream, ImageFormat.Png);
        return imageStream.ToArray();
    }

    private static string NormalizeOcrText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return string.Concat(text
            .Replace('\0', ' ')
            .Replace('\uFFFD', ' ')
            .Select(character => char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'
                ? ' '
                : character))
            .Replace("  ", " ")
            .Trim();
    }
}
