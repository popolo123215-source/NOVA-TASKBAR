using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Net.Http;
using Nova.WindowsClient.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfClipboard = System.Windows.Clipboard;
using WpfColor = System.Windows.Media.Color;
using WpfMedia = System.Windows.Media;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxResult = System.Windows.MessageBoxResult;

namespace Nova.WindowsClient;

internal sealed class ScreenTextResultWindow : Window
{
    private readonly WpfTextBox _result = new();
    private readonly TextBlock _status = new();
    private readonly WpfComboBox _sourceLanguage = new();
    private readonly WpfComboBox _targetLanguage = new();
    private readonly Func<Task<string>> _recognizeAgain;
    private readonly ScreenTextTranslationService _translationService = new();

    public ScreenTextResultWindow(Window owner, string text, string? recognizedLanguage, Func<Task<string>> recognizeAgain)
    {
        Owner = owner;
        _recognizeAgain = recognizeAgain;
        Title = "Текст с экрана";
        Width = 900;
        Height = 640;
        MinWidth = 540;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new WpfMedia.SolidColorBrush(WpfColor.FromRgb(22, 24, 29));
        Foreground = WpfBrushes.White;

        _result.AcceptsReturn = true;
        _result.AcceptsTab = true;
        _result.TextWrapping = TextWrapping.Wrap;
        _result.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _result.FontSize = 14;
        _result.Padding = new Thickness(12);
        _result.Background = new WpfMedia.SolidColorBrush(WpfColor.FromRgb(12, 14, 18));
        _result.Foreground = WpfBrushes.White;
        _result.Text = text;

        _sourceLanguage.ItemsSource = ScreenTextTranslationLanguages.All;
        _targetLanguage.ItemsSource = ScreenTextTranslationLanguages.All;
        _sourceLanguage.SelectedItem = ScreenTextTranslationLanguages.All.FirstOrDefault(language =>
            language.Code.Equals(NormalizeLanguage(recognizedLanguage), StringComparison.OrdinalIgnoreCase))
            ?? ScreenTextTranslationLanguages.All.First(language => language.Code == "en");
        _targetLanguage.SelectedItem = ScreenTextTranslationLanguages.All.First(language => language.Code == "ru");
        _status.Text = text.Length == 0
            ? "Текст не распознан. Выберите более крупную область или проверьте установленный языковой пакет."
            : "Распознанный текст редактируется. Снимок экрана не сохраняется.";
        _status.Foreground = new WpfMedia.SolidColorBrush(WpfColor.FromRgb(180, 185, 196));
        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        actions.Children.Add(CreateButton("Копировать", CopyText));
        actions.Children.Add(CreateButton("Очистить форматирование", CleanText));
        actions.Children.Add(CreateButton("Перевести", async () => await TranslateAsync()));
        actions.Children.Add(CreateButton("Повторить распознавание", async () => await RecognizeAgainAsync()));

        Grid.SetRow(_result, 1);
        var translatePanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 6)
        };
        translatePanel.Children.Add(new TextBlock { Text = "Перевод:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        translatePanel.Children.Add(_sourceLanguage);
        translatePanel.Children.Add(new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
        translatePanel.Children.Add(_targetLanguage);
        Grid.SetRow(translatePanel, 2);

        Grid.SetRow(_status, 3);
        _status.Margin = new Thickness(0, 6, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(actions);
        root.Children.Add(_result);
        root.Children.Add(translatePanel);
        root.Children.Add(_status);
        return root;
    }

    private static WpfButton CreateButton(string label, Action action)
    {
        var button = new WpfButton
        {
            Content = label,
            Margin = new Thickness(0, 0, 7, 5),
            Padding = new Thickness(10, 6, 10, 6),
            Foreground = WpfBrushes.White,
            Background = new WpfMedia.SolidColorBrush(WpfColor.FromRgb(44, 48, 57)),
            BorderBrush = new WpfMedia.SolidColorBrush(WpfColor.FromRgb(78, 82, 91))
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void CopyText()
    {
        try
        {
            WpfClipboard.SetText(_result.Text);
            _status.Text = "Текст скопирован.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _status.Text = $"Не удалось записать текст в буфер обмена: {ex.Message}";
        }
    }

    private void CleanText()
    {
        _result.Text = ScreenTextTranslationService.CleanFormatting(_result.Text);
        _status.Text = "Лишние пробелы и разрывы строк очищены; текст можно отредактировать вручную.";
    }

    private async Task TranslateAsync()
    {
        if (_sourceLanguage.SelectedItem is not TranslationLanguage source ||
            _targetLanguage.SelectedItem is not TranslationLanguage target)
            return;

        var approval = WpfMessageBox.Show(
            this,
            "Для перевода распознанный текст будет отправлен в сторонний сервис MyMemory через HTTPS. Не отправляйте конфиденциальную информацию. Продолжить?",
            "Передача текста для перевода",
            WpfMessageBoxButton.YesNo,
            WpfMessageBoxImage.Warning);
        if (approval != WpfMessageBoxResult.Yes) return;

        _status.Text = "Запрашиваем перевод…";
        try
        {
            _result.Text = await _translationService.TranslateAsync(_result.Text, source.Code, target.Code);
            _status.Text = "Перевод получен от MyMemory. Бесплатный запрос ограничен 500 символами.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        {
            _status.Text = $"Перевод не выполнен: {ex.Message}";
        }
    }

    private async Task RecognizeAgainAsync()
    {
        _status.Text = "Выберите область для повторного распознавания…";
        Hide();
        try
        {
            _result.Text = await _recognizeAgain();
            _status.Text = _result.Text.Length == 0
                ? "Текст не распознан. Попробуйте выбрать более крупную область."
                : "Распознавание завершено. Снимок не сохранялся.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or System.Runtime.InteropServices.COMException or OperationCanceledException)
        {
            _status.Text = $"Повторное распознавание не выполнено: {ex.Message}";
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private static string NormalizeLanguage(string? languageTag) =>
        string.IsNullOrWhiteSpace(languageTag) ? "" : languageTag.Split('-')[0];
}

internal sealed record TranslationLanguage(string Name, string Code)
{
    public override string ToString() => Name;
}

internal static class ScreenTextTranslationLanguages
{
    public static IReadOnlyList<TranslationLanguage> All { get; } =
    [
        new("Арабский", "ar"), new("Немецкий", "de"), new("Английский", "en"),
        new("Испанский", "es"), new("Французский", "fr"), new("Хинди", "hi"),
        new("Итальянский", "it"), new("Японский", "ja"), new("Корейский", "ko"),
        new("Нидерландский", "nl"), new("Польский", "pl"), new("Португальский", "pt"),
        new("Русский", "ru"), new("Турецкий", "tr"), new("Украинский", "uk"),
        new("Китайский", "zh")
    ];
}
