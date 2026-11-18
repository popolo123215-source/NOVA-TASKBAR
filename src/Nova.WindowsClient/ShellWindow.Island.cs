using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Nova.WindowsClient.Services;
using Forms = System.Windows.Forms;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private readonly MediaSessionService _mediaService = new();
    private readonly WeatherService _weatherService = new();
    private readonly SystemStatsService _systemStatsService = new();
    private readonly AudioMeterService _audioMeterService = new();
    private readonly SystemMasterVolumeService _masterVolumeService = new();
    private readonly YandexMusicLyricsService _yandexLyricsService = new();
    private readonly ObservableCollection<TimedLyricLine> _lyricsLines = [];
    private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _mediaRefreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _audioReactiveTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _timerPulse = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _systemStatsTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _weatherRefreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer _masterVolumeRefreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _masterVolumeWriteTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _hoverCollapseTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(420) };
    private CancellationTokenSource? _weatherCancellation;
    private CancellationTokenSource? _weatherSearchCancellation;
    private CancellationTokenSource? _lyricsCancellation;
    private readonly ObservableCollection<WeatherLocation> _weatherLocations = [];
    private WeatherSnapshot? _lastWeather;
    private WeatherLocation? _selectedWeatherLocation;
    private bool _mediaUiUpdating;
    private bool _mediaProgressDragging;
    private bool _systemStatsBusy;
    private bool _weatherBusy;
    private bool _weatherSearchBusy;
    private bool _weatherResultsUpdating;
    private bool _masterVolumeUiUpdating;
    private bool _lyricsUiUpdating;
    private bool _lyricsEnabledForSession;
    private bool _islandStarted;
    private bool _islandDisposed;
    private bool _isDragging;
    private bool _isGamingOverlay;
    private bool _overlayEditMode;
    private bool _inciUriAvailable;
    private float _smoothedAudioLevel;
    private float _smoothedAudioBass;
    private float _smoothedAudioTreble;
    private float _smoothedBeat;
    private float _capsuleMotion;
    private double _bassWavePhase;
    private double _trebleWavePhase;
    private long _lastAudioUiTimestamp;
    private string? _screenDeviceName;
    private string? _overlayScreenDeviceName;
    private double _overlayScale = 1;
    private SystemStatsService.Snapshot? _latestStats;
    private string? _lyricsTrackKey;
    private double _lastCapsuleGeometryWidth = -1;
    private double _lastCapsuleGeometryHeight = -1;
    private PanelState _lastCapsuleGeometryState;
    private bool _capsuleGeometryIsReactive;

    public bool IsGamingOverlayActive => _isGamingOverlay;

    private void InitializeIsland()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (_ready)
                UpdateAudioReactiveUi();
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _mediaRefreshTimer.Tick += async (_, _) => await _mediaService.RefreshSessionAsync();
        _audioReactiveTimer.Tick += (_, _) => UpdateAudioReactiveUi();
        _timerPulse.Tick += (_, _) =>
        {
            RefreshProductivityTimers();
            UpdateMediaProgress();
        };
        _systemStatsTimer.Tick += async (_, _) => await RefreshSystemStatsAsync();
        _weatherRefreshTimer.Tick += (_, _) => RefreshWeather();
        _masterVolumeRefreshTimer.Tick += (_, _) =>
        {
            if (_widgetHost.SelectedWidget?.Id == "Music")
                RefreshMasterVolume();
        };
        _masterVolumeWriteTimer.Tick += (_, _) => ApplyMasterVolume();
        _hoverCollapseTimer.Tick += (_, _) =>
        {
            _hoverCollapseTimer.Stop();
            if (_hoverExpandEnabled && !_isDragging && !IsMouseOver && _state == PanelState.Normal)
                SetState(PanelState.Compact);
        };
        _mediaService.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateMediaUi));
    }

    private void StartIslandServices()
    {
        if (_islandStarted) return;
        _islandStarted = true;
        UpdateClock();
        UpdateMediaUi();
        RefreshProductivityTimers();
        _clockTimer.Start();
        _mediaRefreshTimer.Start();
        _timerPulse.Start();
        _systemStatsTimer.Start();
        _weatherRefreshTimer.Start();
        UpdateCapsuleGeometry(0);
        UpdateAudioReactiveUi();
        _ = InitializeMediaAsync();
        _ = RefreshSystemStatsAsync();
        RefreshWeather();
        RefreshInciStatus();
    }

    private async Task InitializeMediaAsync()
    {
        await _mediaService.InitializeAsync();
        UpdateMediaUi();
    }

    private void StopIslandServices()
    {
        if (_islandDisposed) return;
        _islandDisposed = true;
        _clockTimer.Stop();
        _mediaRefreshTimer.Stop();
        _audioReactiveTimer.Stop();
        _timerPulse.Stop();
        _systemStatsTimer.Stop();
        _weatherRefreshTimer.Stop();
        _masterVolumeRefreshTimer.Stop();
        _masterVolumeWriteTimer.Stop();
        _hoverCollapseTimer.Stop();
        _weatherCancellation?.Cancel();
        _weatherCancellation?.Dispose();
        _weatherCancellation = null;
        _weatherSearchCancellation?.Cancel();
        _weatherSearchCancellation?.Dispose();
        _weatherSearchCancellation = null;
        _lyricsCancellation?.Cancel();
        _lyricsCancellation = null;
        _mediaService.Dispose();
        _audioMeterService.Dispose();
        _systemStatsService.Dispose();
        DisposeAudioMixerUi();
        DisposeNotificationCenterUi();
        DisposeProductivityUi();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        ClockDateText.Text = now.ToString("ddd, d MMM", CultureInfo.GetCultureInfo("ru-RU"));
        if (_widgetHost.SelectedWidget?.Id == "Schedule")
            RefreshTrainSchedule();
    }

    private void UpdateMediaUi()
    {
        var media = _mediaService.Current;
        var hasTrack = media is not null && (!string.IsNullOrWhiteSpace(media.Title) || !string.IsNullOrWhiteSpace(media.Artist));
        CompactMediaTitle.Text = !string.IsNullOrWhiteSpace(media?.Title)
            ? media.Title
            : hasTrack ? media!.Source : "Нет активного трека";
        CompactMediaTitle.ToolTip = !string.IsNullOrWhiteSpace(media?.Title) ? media.Title : null;
        CompactMediaArtist.Text = !string.IsNullOrWhiteSpace(media?.Artist)
            ? media.Artist
            : hasTrack ? "" : "Ожидание медиасессии";
        CompactMediaArtist.ToolTip = !string.IsNullOrWhiteSpace(media?.Artist) ? media.Artist : null;
        CompactMediaArtist.Visibility = hasTrack && string.IsNullOrWhiteSpace(media?.Artist)
            ? Visibility.Collapsed
            : Visibility.Visible;
        CompactMediaState.Text = media is null ? "Нет медиасессии" : media.IsPlaying ? "Играет" : "Пауза";
        CompactMediaState.Foreground = media?.IsPlaying == true
            ? new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xC2, 0x7A))
            : new SolidColorBrush(WpfColor.FromRgb(0x9A, 0xA1, 0xAE));
        CompactMediaState.ToolTip = media is null
            ? "Активная медиасессия Windows не найдена."
            : media.IsPlaying ? "Трек воспроизводится." : "Воспроизведение приостановлено.";
        CompactArtworkImage.Source = media?.Artwork;
        MediaArtworkImage.Source = media?.Artwork;
        MediaArtworkPlaceholder.Visibility = media?.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        MediaTitleText.Text = !string.IsNullOrWhiteSpace(media?.Title)
            ? media.Title
            : hasTrack ? "Название не указано" : "Ничего не воспроизводится";
        MediaTitleText.ToolTip = !string.IsNullOrWhiteSpace(media?.Title) ? media.Title : null;
        MediaArtistText.Text = !string.IsNullOrWhiteSpace(media?.Artist)
            ? media.Artist
            : hasTrack ? "Исполнитель не указан" : "Запустите приложение, поддерживающее Windows Media Session";
        MediaArtistText.ToolTip = !string.IsNullOrWhiteSpace(media?.Artist) ? media.Artist : null;
        MediaAlbumText.Text = !string.IsNullOrWhiteSpace(media?.Album)
            ? $"Альбом · {media.Album}"
            : hasTrack ? "Альбом не указан медиаплеером" : "";
        MediaSourceText.Text = hasTrack ? media!.Source : "";
        MediaPlayPauseButton.Content = media?.IsPlaying == true ? "Ⅱ" : "▶";
        MediaStatusText.Text = media is null
            ? _mediaService.Error is { Length: > 0 } error ? $"Медиасессия недоступна: {error}" : "Ожидание медиасессии Windows…"
            : _mediaService.Error is { Length: > 0 } sessionError ? $"Команда медиаплеера: {sessionError}" : media.IsPlaying ? "Воспроизводится" : "На паузе";
        UpdateMediaProgress();
        UpdateCompactActivity();
        UpdateCompactCapsuleContent();
        ResetLyricsForCurrentTrack(media, hasTrack);
    }

    private void ResetLyricsForCurrentTrack(MediaSnapshot? media, bool hasTrack)
    {
        var trackKey = hasTrack ? $"{media!.Title}\0{media.Artist}" : null;
        if (string.Equals(trackKey, _lyricsTrackKey, StringComparison.Ordinal))
            return;

        _lyricsTrackKey = trackKey;
        _lyricsCancellation?.Cancel();
        _lyricsCancellation = null;
        _lyricsLines.Clear();
        LyricsLinesList.Visibility = Visibility.Collapsed;
        PlainLyricsScroll.Visibility = Visibility.Collapsed;
        PlainLyricsText.Text = "";
        LyricsSourceStatusText.Text = "";
        LyricsStatusText.Visibility = Visibility.Visible;
        LyricsStatusText.Text = hasTrack
            ? "Введите OAuth-токен Яндекс Музыки и загрузите синхронный текст."
            : "Ожидание названия и исполнителя из Windows Media Session.";

        if (_lyricsEnabledForSession &&
            !string.IsNullOrWhiteSpace(YandexMusicTokenBox.Password) &&
            hasTrack)
            _ = LoadYandexLyricsAsync();
    }

    private async void LoadYandexLyrics_Click(object sender, RoutedEventArgs e)
    {
        _lyricsEnabledForSession = true;
        await LoadYandexLyricsAsync();
    }

    private async Task LoadYandexLyricsAsync()
    {
        var media = _mediaService.Current;
        if (media is null || string.IsNullOrWhiteSpace(media.Title) || string.IsNullOrWhiteSpace(media.Artist))
        {
            LyricsStatusText.Text = "Нет полного названия и исполнителя в текущей медиасессии.";
            return;
        }

        var token = YandexMusicTokenBox.Password;
        if (string.IsNullOrWhiteSpace(token))
        {
            LyricsStatusText.Text = "Введите OAuth-токен Яндекс Музыки. Он используется только в памяти текущего запуска NOVA.";
            return;
        }

        _lyricsCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _lyricsCancellation = cancellation;
        LyricsLinesList.Visibility = Visibility.Collapsed;
        PlainLyricsScroll.Visibility = Visibility.Collapsed;
        PlainLyricsText.Text = "";
        LyricsSourceStatusText.Text = "";
        LyricsStatusText.Visibility = Visibility.Visible;
        LyricsStatusText.Text = "Ищем точное совпадение трека и запрашиваем LRC…";

        try
        {
            var result = await _yandexLyricsService.GetLyricsAsync(
                media.Title,
                media.Artist,
                token,
                cancellation.Token);
            if (!ReferenceEquals(_lyricsCancellation, cancellation) || cancellation.IsCancellationRequested)
                return;

            _lyricsLines.Clear();
            foreach (var line in result.Lines)
                _lyricsLines.Add(line);

            if (_lyricsLines.Count > 0)
            {
                LyricsStatusText.Text = $"Текст найден · синхронных строк: {_lyricsLines.Count}.";
                PlainLyricsScroll.Visibility = Visibility.Collapsed;
                LyricsLinesList.Visibility = Visibility.Visible;
                LyricsSourceStatusText.Text = "Источник: Яндекс Музыка · нажмите строку для перемотки.";
                SelectCurrentLyricLine();
            }
            else if (!string.IsNullOrWhiteSpace(result.PlainText))
            {
                LyricsLinesList.Visibility = Visibility.Collapsed;
                PlainLyricsScroll.Visibility = Visibility.Visible;
                PlainLyricsText.Text = result.PlainText;
                LyricsStatusText.Text = "Текст найден, но без таймкодов; синхронизация недоступна.";
                LyricsSourceStatusText.Text = "Источник: Яндекс Музыка.";
            }
            else
            {
                LyricsStatusText.Text = YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.NoLyrics);
                LyricsSourceStatusText.Text = "";
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = cancellation.IsCancellationRequested
                    ? "Загрузка текста отменена."
                    : "Ошибка сети: Яндекс Музыка не ответила за время ожидания. Повторите попытку.";
        }
        catch (YandexLyricsException ex)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
            {
                LyricsStatusText.Text = YandexMusicLyricsService.GetUserFacingFailure(ex.Kind, ex.StatusCode);
                LyricsSourceStatusText.Text = "";
            }
        }
        catch (HttpRequestException ex)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = ex.StatusCode is { } status
                    ? YandexMusicLyricsService.GetUserFacingFailure(
                        YandexMusicLyricsService.ClassifyHttpFailure(status, YandexLyricsRequestStage.Lyrics),
                        status)
                    : YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.Network);
        }
        catch (JsonException)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.InvalidResponse);
        }
        catch (InvalidDataException ex)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = ex.Message;
        }
        catch (UnauthorizedAccessException)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.Authorization);
        }
        catch (ArgumentException ex)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = ex.Message;
        }
        catch (FormatException)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.InvalidResponse);
        }
        catch (IOException)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = YandexMusicLyricsService.GetUserFacingFailure(YandexLyricsFailureKind.Network);
        }
        catch (Exception ex)
        {
            if (IsCurrentLyricsRequest(cancellation, media))
                LyricsStatusText.Text = $"Ошибка загрузки текста ({ex.GetType().Name}). Повторите попытку.";
        }
        finally
        {
            if (ReferenceEquals(_lyricsCancellation, cancellation))
                _lyricsCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool IsCurrentLyricsRequest(CancellationTokenSource cancellation, MediaSnapshot media) =>
        ReferenceEquals(_lyricsCancellation, cancellation) &&
        string.Equals(_lyricsTrackKey, $"{media.Title}\0{media.Artist}", StringComparison.Ordinal);

    private async void LyricsLinesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_lyricsUiUpdating || LyricsLinesList.SelectedItem is not TimedLyricLine line)
            return;

        if (!await _mediaService.SeekAsync(line.Position))
            MediaStatusText.Text = "Эта Windows Media Session не поддерживает перемотку к выбранной строке.";
    }

    private void SelectCurrentLyricLine()
    {
        var position = _mediaService.Current?.CurrentPosition ?? TimeSpan.Zero;
        var selectedIndex = -1;
        for (var index = 0; index < _lyricsLines.Count; index++)
        {
            if (_lyricsLines[index].Position > position)
                break;
            selectedIndex = index;
        }

        if (LyricsLinesList.SelectedIndex == selectedIndex)
            return;

        _lyricsUiUpdating = true;
        try
        {
            LyricsLinesList.SelectedIndex = selectedIndex;
            if (selectedIndex >= 0)
                LyricsLinesList.ScrollIntoView(_lyricsLines[selectedIndex]);
        }
        finally
        {
            _lyricsUiUpdating = false;
        }
    }

    private void RefreshMasterVolume()
    {
        try
        {
            var level = _masterVolumeService.ReadLevel();
            _masterVolumeUiUpdating = true;
            MediaMasterVolumeSlider.Value = Math.Clamp(level, 0, 1);
            MediaMasterVolumeSlider.IsEnabled = true;
            MediaMasterVolumeText.Text = $"{Math.Round(level * 100):0}%";
            MediaMasterVolumeStatusText.Text = "Управляется общая громкость Windows для всех приложений.";
        }
        catch (COMException ex)
        {
            MediaMasterVolumeSlider.IsEnabled = false;
            MediaMasterVolumeText.Text = "Недоступно";
            MediaMasterVolumeStatusText.Text = $"Не удалось прочитать громкость Windows: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            MediaMasterVolumeSlider.IsEnabled = false;
            MediaMasterVolumeText.Text = "Недоступно";
            MediaMasterVolumeStatusText.Text = $"Устройство вывода звука недоступно: {ex.Message}";
        }
        catch (NAudio.MmException ex)
        {
            MediaMasterVolumeSlider.IsEnabled = false;
            MediaMasterVolumeText.Text = "Недоступно";
            MediaMasterVolumeStatusText.Text = $"Ошибка аудиоустройства Windows: {ex.Message}";
        }
        finally
        {
            _masterVolumeUiUpdating = false;
        }
    }

    private void MediaMasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_masterVolumeUiUpdating || !MediaMasterVolumeSlider.IsEnabled)
            return;

        MediaMasterVolumeText.Text = $"{Math.Round(e.NewValue * 100):0}%";
        _masterVolumeWriteTimer.Stop();
        _masterVolumeWriteTimer.Start();
    }

    private void ApplyMasterVolume()
    {
        _masterVolumeWriteTimer.Stop();
        if (_masterVolumeUiUpdating || !MediaMasterVolumeSlider.IsEnabled)
            return;

        try
        {
            _masterVolumeService.SetLevel((float)MediaMasterVolumeSlider.Value);
            MediaMasterVolumeStatusText.Text = "Управляется общая громкость Windows для всех приложений.";
        }
        catch (COMException ex)
        {
            MediaMasterVolumeStatusText.Text = $"Не удалось изменить громкость Windows: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            MediaMasterVolumeStatusText.Text = $"Устройство вывода звука недоступно: {ex.Message}";
        }
        catch (NAudio.MmException ex)
        {
            MediaMasterVolumeStatusText.Text = $"Ошибка аудиоустройства Windows: {ex.Message}";
        }
    }

    private void UpdateAudioReactiveUi()
    {
        if (_islandDisposed) return;
        var captureEnabled = _enableAudioReactive && _enableMotion && IsVisible && !_isGamingOverlay &&
            _state == PanelState.Compact;
        if (captureEnabled)
        {
            if (!_audioReactiveTimer.IsEnabled)
                _audioReactiveTimer.Start();
        }
        else
        {
            _audioReactiveTimer.Stop();
            _smoothedAudioLevel = 0;
            _smoothedAudioBass = 0;
            _smoothedAudioTreble = 0;
            _smoothedBeat = 0;
            _capsuleMotion = 0;
            _lastAudioUiTimestamp = 0;
        }

        _audioMeterService.SetEnabled(captureEnabled);
        var sample = _audioMeterService.Sample();
        if (sample.Error is { Length: > 0 } error)
        {
            SetAudioVisualizationStatus($"Аудиореактивность WASAPI недоступна: {error}");
        }
        else
        {
            SetAudioVisualizationStatus(captureEnabled
                ? "WASAPI анализирует громкость, басы, высокие частоты и резкие пики общего аудиомикса."
                : !_enableMotion
                    ? "Плавные анимации выключены в настройках."
                    : !_enableAudioReactive
                        ? "Аудиоанимация выключена в настройках."
                        : _isGamingOverlay
                            ? "Аудиореактивность приостановлена в игровом оверлее."
                            : (!IsVisible || _state is PanelState.Normal or PanelState.Expanded or PanelState.Fullscreen)
                                ? "Аудиореактивность капсулы приостановлена в рабочем пространстве."
                                : "Аудиореактивность приостановлена.");
        }

        if (!captureEnabled)
        {
            UpdateCapsuleGeometry(0);
            return;
        }

        var elapsed = _lastAudioUiTimestamp == 0
            ? 0.033
            : Math.Clamp((Stopwatch.GetTimestamp() - _lastAudioUiTimestamp) / (double)Stopwatch.Frequency, 0, 0.2);
        _lastAudioUiTimestamp = Stopwatch.GetTimestamp();
        var targetLevel = captureEnabled && sample.Error is null ? sample.Level : 0;
        var targetBass = captureEnabled && sample.Error is null ? sample.Bass : 0;
        var targetTreble = captureEnabled && sample.Error is null ? sample.Treble : 0;
        var targetBeat = captureEnabled && sample.Error is null ? sample.Beat : 0;
        _smoothedAudioLevel = SmoothSignal(_smoothedAudioLevel, targetLevel, elapsed, targetLevel > _smoothedAudioLevel ? 0.025 : 0.3);
        _smoothedAudioBass = SmoothSignal(_smoothedAudioBass, targetBass, elapsed, targetBass > _smoothedAudioBass ? 0.035 : 0.34);
        _smoothedAudioTreble = SmoothSignal(_smoothedAudioTreble, targetTreble, elapsed, targetTreble > _smoothedAudioTreble ? 0.018 : 0.2);
        _smoothedBeat = SmoothSignal(_smoothedBeat, targetBeat, elapsed, targetBeat > _smoothedBeat ? 0.025 : 0.2);
        if (_smoothedAudioBass > 0.005f)
            _bassWavePhase = (_bassWavePhase + elapsed * (1.3 + _smoothedAudioBass * 4)) % (Math.PI * 2);
        if (_smoothedAudioTreble > 0.005f)
            _trebleWavePhase = (_trebleWavePhase + elapsed * (4 + _smoothedAudioTreble * 13)) % (Math.PI * 2);
        _capsuleMotion = _enableAudioReactive && _enableMotion && !_isGamingOverlay
            ? (float)Math.Clamp(_smoothedAudioLevel * 0.28 + _smoothedAudioBass * 0.32 +
                _smoothedAudioTreble * 0.12 + _smoothedBeat * 0.85, 0, 1.4)
            : 0;

        UpdateCapsuleGeometry((float)(_capsuleMotion * _audioReactiveIntensity));
    }

    private void SetAudioVisualizationStatus(string status)
    {
        if (!string.Equals(AudioVisualizationStatusText.Text, status, StringComparison.Ordinal))
            AudioVisualizationStatusText.Text = status;
    }

    private static float SmoothSignal(float current, float target, double elapsed, double timeConstant)
    {
        var amount = (float)(1 - Math.Exp(-elapsed / timeConstant));
        return current + (target - current) * amount;
    }

    private void UpdateCapsuleGeometry(float motion)
    {
        if (!IsLoaded || _isDimensionsAnimating || _isGamingOverlay || _state == PanelState.Fullscreen)
            return;

        var width = PanelSurface.ActualWidth;
        var height = PanelSurface.ActualHeight;
        if (width <= 20 || height <= 20)
            return;

        var reactiveScale = _enableAudioReactive && _enableMotion && !_isGamingOverlay &&
            _state == PanelState.Compact
                ? _audioReactiveIntensity
                : 0;
        var hasReactiveInput = reactiveScale > 0 &&
            (_smoothedAudioLevel > 0.001f || _smoothedAudioBass > 0.001f ||
             _smoothedAudioTreble > 0.001f || _smoothedBeat > 0.001f);
        if (!hasReactiveInput && !_capsuleGeometryIsReactive &&
            width == _lastCapsuleGeometryWidth &&
            height == _lastCapsuleGeometryHeight &&
            _state == _lastCapsuleGeometryState)
            return;

        const double inset = 7;
        var radius = Math.Min(_state switch
        {
            PanelState.Compact => 40,
            _ => 24
        }, Math.Min(width, height) / 2 - inset);
        var left = inset + radius;
        var right = width - inset - radius;
        var top = inset;
        var bottom = height - inset;
        var topCenterY = top + radius;
        var bottomCenterY = bottom - radius;
        var points = new List<SurfacePoint>(64);

        AddLine(points, new Point(left, top), new Point(right, top), new Vector(0, -1), 10);
        AddArc(points, new Point(right, topCenterY), radius, -Math.PI / 2, 0, new Vector(1, 0), 8);
        AddLine(points, new Point(width - inset, topCenterY), new Point(width - inset, bottomCenterY), new Vector(1, 0), 8);
        AddArc(points, new Point(right, bottomCenterY), radius, 0, Math.PI / 2, new Vector(0, 1), 8);
        AddLine(points, new Point(right, bottom), new Point(left, bottom), new Vector(0, 1), 10);
        AddArc(points, new Point(left, bottomCenterY), radius, Math.PI / 2, Math.PI, new Vector(-1, 0), 8);
        AddLine(points, new Point(inset, bottomCenterY), new Point(inset, topCenterY), new Vector(-1, 0), 8);
        AddArc(points, new Point(left, topCenterY), radius, Math.PI, Math.PI * 1.5, new Vector(0, -1), 8, includeEnd: false);

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var contourPhase = Math.PI * 2 * index / points.Count;
            var bassWave = Math.Sin(contourPhase * 2.7 + _bassWavePhase) * 0.62 +
                Math.Sin(contourPhase * 5.1 - _bassWavePhase * 0.63) * 0.38;
            var fineRipple = Math.Sin(contourPhase * 10 + _trebleWavePhase) * 0.55 +
                Math.Sin(contourPhase * 17 - _trebleWavePhase * 0.58) * 0.45;
            var pulseShape = 0.78 + 0.22 * Math.Sin(contourPhase * 1.6 + _bassWavePhase * 0.28);
            var displacement = (float)(
                _smoothedAudioLevel * 1.6 +
                _smoothedAudioBass * bassWave * 4.8 +
                _smoothedAudioTreble * fineRipple * 1.8 +
                _smoothedBeat * pulseShape * 7.2) * reactiveScale;
            displacement = Math.Clamp(displacement, -13f, 15f);
            points[index] = point with { Position = point.Position + point.Normal * displacement };
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0].Position, isFilled: true, isClosed: true);
            for (var index = 0; index < points.Count; index++)
            {
                var previous = points[(index - 1 + points.Count) % points.Count].Position;
                var current = points[index].Position;
                var next = points[(index + 1) % points.Count].Position;
                var afterNext = points[(index + 2) % points.Count].Position;
                var firstControl = current + (next - previous) / 6;
                var secondControl = next - (afterNext - current) / 6;
                context.BezierTo(firstControl, secondControl, next, isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        CapsuleSurfacePath.Data = geometry;
        CapsuleGlowPath.Data = geometry;
        CapsuleGlowPath.Opacity = Math.Clamp(0.13 + motion * 0.25, 0.13, 0.72);
        CapsuleGlowPath.StrokeThickness = 5 + Math.Min(6, Math.Max(0, motion) * 3.2);
        _lastCapsuleGeometryWidth = width;
        _lastCapsuleGeometryHeight = height;
        _lastCapsuleGeometryState = _state;
        _capsuleGeometryIsReactive = hasReactiveInput;
    }

    private static void AddLine(List<SurfacePoint> points, Point start, Point end, Vector normal, int segments)
    {
        for (var index = 0; index <= segments; index++)
        {
            if (points.Count > 0 && index == 0)
                continue;
            var amount = index / (double)segments;
            points.Add(new SurfacePoint(start + (end - start) * amount, normal));
        }
    }

    private static void AddArc(
        List<SurfacePoint> points,
        Point center,
        double radius,
        double startAngle,
        double endAngle,
        Vector fallbackNormal,
        int segments,
        bool includeEnd = true)
    {
        var finalIndex = includeEnd ? segments : segments - 1;
        for (var index = 1; index <= finalIndex; index++)
        {
            var angle = startAngle + (endAngle - startAngle) * index / segments;
            var normal = new Vector(Math.Cos(angle), Math.Sin(angle));
            points.Add(new SurfacePoint(center + normal * radius, normal.LengthSquared > 0 ? normal : fallbackNormal));
        }
    }

    private readonly record struct SurfacePoint(Point Position, Vector Normal);

    private void UpdateCompactMetrics()
    {
        var snapshot = _latestStats;
        if (snapshot is null)
        {
            CompactMetricsText.Text = "";
            CompactMetricsText.Visibility = Visibility.Collapsed;
            CompactNetworkText.Text = "Сеть: ожидание";
            CompactNetworkText.ToolTip = "Ожидание первого образца сетевых счётчиков Windows.";
            CompactNetworkText.Visibility = _showCapsuleNetwork ? Visibility.Visible : Visibility.Collapsed;
            CompactVpnText.Text = "Radmin VPN: проверка";
            CompactVpnText.ToolTip = "Ожидание проверки локального сетевого адаптера.";
            CompactVpnText.Visibility = _showCapsuleVpn ? Visibility.Visible : Visibility.Collapsed;
            UpdateCompactCapsuleContent();
            return;
        }

        var values = new List<string>(3);
        if (_showCapsuleCpu) values.Add($"CPU {FormatPercent(snapshot.CpuPercent)}");
        if (_showCapsuleGpu) values.Add($"GPU {FormatPercent(snapshot.Gpu3DPercent)}");
        if (_showCapsuleRam)
        {
            var ramPercent = snapshot.RamTotalGb > 0 ? snapshot.RamUsedGb / snapshot.RamTotalGb * 100 : 0;
            values.Add($"RAM {ramPercent:0}%");
        }

        CompactMetricsText.Text = string.Join(" · ", values);
        CompactMetricsText.Visibility = values.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CompactNetworkText.Text = $"↓ {FormatNetworkRate(snapshot.NetworkDownloadBytesPerSecond)} ↑ {FormatNetworkRate(snapshot.NetworkUploadBytesPerSecond)}";
        CompactNetworkText.ToolTip = snapshot.NetworkError is null
            ? "Суммарная скорость активных IPv4-сетевых адаптеров Windows."
            : $"Не удалось получить скорость сети: {snapshot.NetworkError}";
        CompactNetworkText.Visibility = _showCapsuleNetwork ? Visibility.Visible : Visibility.Collapsed;
        CompactVpnText.Text = snapshot.RadminVpnConnected switch
        {
            true => "Radmin VPN: подключён",
            false => "Radmin VPN: отключён",
            _ => "Radmin VPN: не найден"
        };
        CompactVpnText.ToolTip = snapshot.RadminVpnConnected is null
            ? "Адаптер Radmin VPN не обнаружен. Это не проверка доступности удалённого пользователя."
            : "Показывается состояние локального сетевого адаптера, а не доступность удалённого пользователя.";
        CompactVpnText.Visibility = _showCapsuleVpn ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompactCapsuleContent();
    }

    private static string FormatPercent(double? value) => value is double percent ? $"{percent:0}%" : "н/д";

    private static string FormatNetworkRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not double rate) return "—";
        if (rate >= 1_048_576) return $"{rate / 1_048_576:0.0} MB/s";
        if (rate >= 1024) return $"{rate / 1024:0} KB/s";
        return $"{rate:0} B/s";
    }

    private void UpdateCompactCapsuleContent()
    {
        if (CapsuleContentGrid is null) return;

        CapsuleClockBlock.Visibility = _showCapsuleClock ? Visibility.Visible : Visibility.Collapsed;
        CapsuleWeatherBlock.Visibility = _showCapsuleWeather ? Visibility.Visible : Visibility.Collapsed;
        CompactMediaInfo.Visibility = _showCapsuleMedia ? Visibility.Visible : Visibility.Collapsed;
        CompactArtworkFrame.Visibility = _showCapsuleArtwork && CompactArtworkImage.Source is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactMediaState.Visibility = _showCapsulePlaybackStatus ? Visibility.Visible : Visibility.Collapsed;

        var media = _mediaService.Current;
        var mediaVisible = _showCapsuleMedia ||
            (_showCapsuleArtwork && media?.Artwork is not null) ||
            (_showCapsulePlaybackStatus && media is not null);
        CompactMediaButton.Visibility = mediaVisible ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement[] order = _capsuleContentOrder switch
        {
            "MediaClockWeatherStatus" => [CompactMediaButton, CapsuleClockBlock, CapsuleWeatherBlock, CapsuleStatusBlock],
            "WeatherClockMediaStatus" => [CapsuleWeatherBlock, CapsuleClockBlock, CompactMediaButton, CapsuleStatusBlock],
            "StatusMediaClockWeather" => [CapsuleStatusBlock, CompactMediaButton, CapsuleClockBlock, CapsuleWeatherBlock],
            _ => [CapsuleClockBlock, CapsuleWeatherBlock, CompactMediaButton, CapsuleStatusBlock]
        };

        var weights = new Dictionary<FrameworkElement, double>
        {
            [CapsuleClockBlock] = 1.05,
            [CapsuleWeatherBlock] = 1.35,
            [CompactMediaButton] = 3.8,
            [CapsuleStatusBlock] = 1.75 + (_showCapsuleNetwork ? 0.3 : 0) + (_showCapsuleVpn ? 0.3 : 0)
        };

        var vertical = _compactIsVertical == true;
        for (var index = 0; index < order.Length; index++)
        {
            var element = order[index];
            if (vertical)
            {
                Grid.SetRow(element, index);
                Grid.SetColumn(element, 0);
                CapsuleContentGrid.RowDefinitions[index].Height = element.Visibility == Visibility.Visible
                    ? GridLength.Auto
                    : new GridLength(0, GridUnitType.Pixel);
            }
            else
            {
                Grid.SetRow(element, 0);
                Grid.SetColumn(element, index);
                CapsuleContentGrid.ColumnDefinitions[index].Width = element.Visibility == Visibility.Visible
                    ? new GridLength(weights[element], GridUnitType.Star)
                    : new GridLength(0, GridUnitType.Pixel);
            }
        }
    }

    private void UpdateOverlayMetricVisibility()
    {
        OverlayCpuRow.Visibility = _showOverlayCpu ? Visibility.Visible : Visibility.Collapsed;
        OverlayGpuRow.Visibility = _showOverlayGpu ? Visibility.Visible : Visibility.Collapsed;
        OverlayRamRow.Visibility = _showOverlayRam ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMediaProgress()
    {
        if (_mediaProgressDragging) return;
        var media = _mediaService.Current;
        _mediaUiUpdating = true;
        if (media is null || media.Duration <= TimeSpan.Zero)
        {
            MediaProgressSlider.IsEnabled = false;
            MediaProgressSlider.Value = 0;
            MediaPositionText.Text = "0:00";
            MediaDurationText.Text = "0:00";
        }
        else
        {
            MediaProgressSlider.IsEnabled = true;
            var position = media.CurrentPosition;
            MediaProgressSlider.Value = Math.Clamp(position.TotalSeconds / media.Duration.TotalSeconds * MediaProgressSlider.Maximum, 0, MediaProgressSlider.Maximum);
            MediaPositionText.Text = FormatMediaTime(position);
            MediaDurationText.Text = FormatMediaTime(media.Duration);
        }
        _mediaUiUpdating = false;
        if (_lyricsLines.Count > 0)
            SelectCurrentLyricLine();
    }

    private static string FormatMediaTime(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    private void UpdateCompactActivity()
    {
        var activeTimer = _productivityService.IsAvailable
            ? _productivityService.GetTimerSnapshots(DateTimeOffset.UtcNow)
                .FirstOrDefault(timer => timer.IsRunning ||
                    !timer.Timer.IsCompleted && timer.Elapsed > TimeSpan.Zero)
            : null;
        if (activeTimer is not null)
        {
            var value = activeTimer.Timer.Kind == ProductivityTimerKind.Countdown
                ? TimeSpan.FromSeconds(Math.Ceiling(activeTimer.Remaining.TotalSeconds))
                : activeTimer.Elapsed;
            var display = value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
            CompactActivityText.Text = $"{activeTimer.Timer.Name} {display}";
            ActivityDot.Fill = activeTimer.IsRunning
                ? new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xC2, 0x7A))
                : new SolidColorBrush(WpfColor.FromRgb(0x96, 0xA1, 0xB8));
        }
        else if (_incomingOffers.Count > 0)
        {
            CompactActivityText.Text = "Входящий файл";
            ActivityDot.Fill = new SolidColorBrush(WpfColor.FromRgb(0xFF, 0xC2, 0x7A));
        }
        else if (_activities.FirstOrDefault(activity => activity.IsActive) is { } transfer)
        {
            CompactActivityText.Text = transfer.FileName;
            ActivityDot.Fill = new SolidColorBrush(WpfColor.FromRgb(0x70, 0xC9, 0xFF));
        }
        else if (_mediaService.Current?.IsPlaying == true)
        {
            CompactActivityText.Text = "Музыка";
            ActivityDot.Fill = new SolidColorBrush(WpfColor.FromRgb(0xB4, 0x9B, 0xFF));
        }
        else
        {
            CompactActivityText.Text = "Готово";
            ActivityDot.Fill = new SolidColorBrush(WpfColor.FromRgb(0x63, 0xD7, 0xB3));
        }
    }

    private async Task RefreshSystemStatsAsync()
    {
        if (_systemStatsBusy || _islandDisposed) return;
        _systemStatsBusy = true;
        try
        {
            var snapshot = await Task.Run(_systemStatsService.Sample);
            if (_islandDisposed) return;
            _latestStats = snapshot;
            UpdateSystemMonitorUi(snapshot);
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or ObjectDisposedException)
        {
            if (!_islandDisposed) SystemStatusText.Text = $"Не удалось получить системные показатели: {ex.Message}";
        }
        finally
        {
            _systemStatsBusy = false;
        }
    }

    private void RefreshWeather()
    {
        if (!_islandDisposed) _ = RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        if (_weatherBusy || _islandDisposed) return;
        var city = CityBox.Text.Trim();
        if (_selectedWeatherLocation is null)
        {
            WeatherStatusText.Text = city.Length == 0
                ? "Укажите город в настройках, найдите его и выберите нужное местоположение."
                : "Сначала найдите город в настройках и выберите нужное местоположение.";
            CompactWeatherTemperature.Text = "—°";
            CompactWeatherPlace.Text = "Настроить";
            WeatherPlaceText.Text = "Выберите местоположение в настройках";
            WeatherTemperatureText.Text = "—°";
            WeatherConditionText.Text = "Нет выбранного местоположения.";
            return;
        }

        _weatherBusy = true;
        _weatherCancellation?.Cancel();
        _weatherCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _weatherCancellation = cancellation;
        WeatherStatusText.Text = "Получаем актуальные данные…";
        try
        {
            var snapshot = await _weatherService.GetCurrentAsync(_selectedWeatherLocation, cancellation.Token);
            if (cancellation.IsCancellationRequested || _islandDisposed) return;
            _lastWeather = snapshot;
            CompactWeatherIcon.Text = snapshot.Glyph;
            CompactWeatherTemperature.Text = $"{snapshot.TemperatureC:0}°";
            CompactWeatherPlace.Text = snapshot.Place;
            WeatherIconText.Text = snapshot.Glyph;
            WeatherTemperatureText.Text = $"{snapshot.TemperatureC:0}°";
            WeatherPlaceText.Text = snapshot.Place;
            WeatherConditionText.Text = snapshot.Condition;
            WeatherStatusText.Text = $"Обновлено: {DateTime.Now:HH:mm} · источник: Open-Meteo";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or TaskCanceledException)
        {
            WeatherStatusText.Text = _lastWeather is null
                ? $"Не удалось загрузить погоду: {ex.Message}"
                : $"Нет связи; показаны последние данные ({_lastWeather.UpdatedAt.LocalDateTime:HH:mm}). {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_weatherCancellation, cancellation))
            {
                _weatherBusy = false;
                _weatherCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async void CityBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchWeatherLocationsAsync();
    }

    private async void SearchWeatherLocations_Click(object sender, RoutedEventArgs e) =>
        await SearchWeatherLocationsAsync();

    private async Task SearchWeatherLocationsAsync()
    {
        if (_weatherSearchBusy || _islandDisposed) return;
        var city = CityBox.Text.Trim();
        if (city.Length is < 2 or > 100)
        {
            WeatherSearchStatusText.Text = "Введите от 2 до 100 символов для поиска.";
            return;
        }

        _weatherSearchBusy = true;
        _weatherSearchCancellation?.Cancel();
        _weatherSearchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _weatherSearchCancellation = cancellation;
        WeatherSearchStatusText.Text = "Ищем населённые пункты…";
        try
        {
            var locations = await _weatherService.SearchLocationsAsync(city, cancellation.Token);
            if (cancellation.IsCancellationRequested || _islandDisposed) return;

            _weatherResultsUpdating = true;
            try
            {
                _weatherLocations.Clear();
                foreach (var location in locations) _weatherLocations.Add(location);
                WeatherSearchResults.SelectedItem = null;
            }
            finally
            {
                _weatherResultsUpdating = false;
            }

            WeatherSearchStatusText.Text = locations.Count == 0
                ? $"По запросу «{city}» ничего не найдено."
                : $"Найдено: {locations.Count}. Выберите правильный регион — выбор сохранится.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or TaskCanceledException)
        {
            WeatherSearchStatusText.Text = $"Ошибка поиска местоположения: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_weatherSearchCancellation, cancellation))
            {
                _weatherSearchBusy = false;
                _weatherSearchCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void WeatherSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_weatherResultsUpdating || WeatherSearchResults.SelectedItem is not WeatherLocation location) return;
        _selectedWeatherLocation = location;
        CityBox.Text = location.Name;
        _preferences = _preferences with { City = location.Name, WeatherLocation = location };
        SavePreferences();
        WeatherSearchStatusText.Text = $"Сохранено: {location.DisplayName}";
        RefreshWeather();
    }

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!await _mediaService.TogglePlayPauseAsync())
            MediaStatusText.Text = _mediaService.Current is null ? "Нет активной медиасессии." : "Плеер не поддерживает воспроизведение/паузу.";
    }

    private async void MediaPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (!await _mediaService.SkipPreviousAsync())
            MediaStatusText.Text = "Нет активной сессии или предыдущий трек не поддерживается плеером.";
    }

    private async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        if (!await _mediaService.SkipNextAsync())
            MediaStatusText.Text = "Нет активной сессии или следующий трек не поддерживается плеером.";
    }

    private void CompactMedia_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Music");

    private void MediaMainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MediaArtworkFrame is null) return;
        var artworkSize = Math.Clamp(e.NewSize.Width * 0.23, 112, 148);
        if (Math.Abs(MediaArtworkFrame.Width - artworkSize) < 1)
            return;
        MediaArtworkFrame.Width = artworkSize;
        MediaArtworkFrame.Height = artworkSize;
    }

    private void MediaProgress_DragStarted(object sender, MouseButtonEventArgs e) => _mediaProgressDragging = true;
    private void MediaProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_mediaUiUpdating && _mediaProgressDragging)
            MediaPositionText.Text = FormatMediaTime(TimeSpan.FromSeconds((_mediaService.Current?.Duration.TotalSeconds ?? 0) * e.NewValue / MediaProgressSlider.Maximum));
    }

    private async void MediaProgress_Seek(object sender, MouseButtonEventArgs e)
    {
        _mediaProgressDragging = false;
        var duration = _mediaService.Current?.Duration ?? TimeSpan.Zero;
        if (duration <= TimeSpan.Zero) return;
        var target = TimeSpan.FromSeconds(duration.TotalSeconds * MediaProgressSlider.Value / MediaProgressSlider.Maximum);
        if (!await _mediaService.SeekAsync(target))
            MediaStatusText.Text = "Перемотка не поддерживается этой медиасессией.";
        UpdateMediaProgress();
    }

    private void VpnRefresh_Click(object sender, RoutedEventArgs e) => RefreshInciStatus();

    private void RefreshInciStatus()
    {
        try
        {
            using var commandKey = Registry.ClassesRoot.OpenSubKey(@"incy\shell\open\command");
            var command = commandKey?.GetValue(null) as string;
            var executable = ExtractExecutablePath(command);
            _inciUriAvailable = !string.IsNullOrWhiteSpace(executable) && File.Exists(executable);
            IncyConnectButton.IsEnabled = _inciUriAvailable;
            IncyDisconnectButton.IsEnabled = _inciUriAvailable;
            IncyStatusButton.IsEnabled = _inciUriAvailable;

            if (_inciUriAvailable)
            {
                VpnStatusText.Text = "Статус подключения не подтверждён";
                VpnDetailText.Text = "Обработчик INCY найден. NOVA может передать команды connect и disconnect, но не получает ответ о результате, список серверов или выбранный сервер. Кнопка «Открыть INCY» запускает интерфейс клиента.";
            }
            else
            {
                VpnStatusText.Text = "INCY не найден";
                VpnDetailText.Text = "Не найден зарегистрированный обработчик incy:// или его исполняемый файл. Подключение и отключение недоступны.";
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            _inciUriAvailable = false;
            IncyConnectButton.IsEnabled = false;
            IncyDisconnectButton.IsEnabled = false;
            IncyStatusButton.IsEnabled = false;
            VpnStatusText.Text = "Не удалось проверить INCY";
            VpnDetailText.Text = ex.Message;
        }
    }

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            var endQuote = command.IndexOf('"', 1);
            return endQuote > 1 ? command[1..endQuote] : null;
        }

        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private void IncyConnect_Click(object sender, RoutedEventArgs e) =>
        SendIncyCommand("connect", "Команда подключения передана INCY; NOVA не получает подтверждение состояния.");

    private void IncyDisconnect_Click(object sender, RoutedEventArgs e) =>
        SendIncyCommand("disconnect", "Команда отключения передана INCY; NOVA не получает подтверждение состояния.");

    private void IncyStatus_Click(object sender, RoutedEventArgs e) =>
        SendIncyCommand("open", "Окно INCY открыто. NOVA не получает машинно-читаемый статус подключения.");

    private void SendIncyCommand(string action, string message)
    {
        if (!_inciUriAvailable)
        {
            RefreshInciStatus();
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = $"incy://{action}",
                UseShellExecute = true
            });
            VpnStatusText.Text = "Запрос передан INCY";
            VpnDetailText.Text = message;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            VpnStatusText.Text = "Не удалось передать запрос INCY";
            VpnDetailText.Text = ex.Message;
        }
    }

    private void GamingOverlay_Click(object sender, RoutedEventArgs e) => EnterGamingOverlay();

    private void OverlaySizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _overlayScale = e.NewValue;
        if (_isGamingOverlay) ApplyDimensions();
        SchedulePreferenceSave();
    }

    private void EnterGamingOverlay()
    {
        if (_isGamingOverlay) return;
        _overlayScreenDeviceName ??= _screenDeviceName ?? ScreenAtWindow().DeviceName;
        var screen = CurrentOverlayScreen();
        var scale = GetScreenScale(screen);
        var work = screen.WorkingArea;
        var workLeft = work.Left / scale.X;
        var workTop = work.Top / scale.Y;
        var workRight = (work.Left + work.Width) / scale.X;
        var workBottom = (work.Top + work.Height) / scale.Y;
        var firstUse = string.IsNullOrWhiteSpace(_preferences.OverlayScreenDevice);
        _isGamingOverlay = true;
        UpdateAudioReactiveUi();
        UpdateSettingsOptionUi();
        _overlayEditMode = false;
        CompactView.Visibility = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Collapsed;
        GamingOverlayView.Visibility = Visibility.Visible;
        CapsuleSurfacePath.Visibility = Visibility.Collapsed;
        PanelSurface.Background = WpfBrushes.Transparent;
        PanelSurface.BorderBrush = WpfBrushes.Transparent;
        PanelSurface.BorderThickness = new Thickness(0);
        PanelSurface.Opacity = 1;
        MinWidth = 160;
        MinHeight = 50;
        Left = firstUse ? workLeft + 24 : Math.Clamp(_preferences.OverlayLeft, workLeft, workRight - 160);
        Top = firstUse ? workTop + 24 : Math.Clamp(_preferences.OverlayTop, workTop, workBottom - 50);
        ApplyDimensions();

        if (!UpdateOverlayInputMode())
        {
            _isGamingOverlay = false;
            UpdateSettingsOptionUi();
            GamingOverlayView.Visibility = Visibility.Collapsed;
            RestorePanelSurface();
            SetState(PanelState.Expanded);
            SystemStatusText.Text = "Не удалось включить безопасный click-through для оверлея.";
            return;
        }

        SavePreferences();
        UpdateOverlayMetricVisibility();
    }

    public void BeginOverlayMove()
    {
        if (!_isGamingOverlay) return;
        _overlayEditMode = true;
        if (!UpdateOverlayInputMode())
        {
            _overlayEditMode = false;
            SystemStatusText.Text = "Не удалось временно разрешить ввод для перемещения оверлея.";
        }
    }

    public void ExitGamingOverlay()
    {
        if (!_isGamingOverlay) return;
        _isGamingOverlay = false;
        _overlayEditMode = false;
        if (!UpdateOverlayInputMode())
        {
            _isGamingOverlay = true;
            return;
        }
        GamingOverlayView.Visibility = Visibility.Collapsed;
        RestorePanelSurface();
        SetState(PanelState.Compact);
        Show();
        Activate();
        UpdateSettingsOptionUi();
        SavePreferences();
    }

    private void RestorePanelSurface()
    {
        PanelSurface.Background = WpfBrushes.Transparent;
        PanelSurface.BorderBrush = WpfBrushes.Transparent;
        PanelSurface.BorderThickness = new Thickness(0);
        PanelSurface.Margin = new Thickness(0);
        CapsuleSurfacePath.Visibility = Visibility.Visible;
        Topmost = _keepOnTop;
        MinWidth = 380;
        MinHeight = 64;
    }

    private Forms.Screen CurrentOverlayScreen()
    {
        if (!string.IsNullOrWhiteSpace(_overlayScreenDeviceName))
        {
            var stored = Forms.Screen.AllScreens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, _overlayScreenDeviceName, StringComparison.OrdinalIgnoreCase));
            if (stored is not null) return stored;
        }

        var current = ScreenAtWindow();
        _overlayScreenDeviceName = current.DeviceName;
        return current;
    }

    private bool UpdateOverlayInputMode()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;

        const int extendedStyleIndex = -20;
        const long transparentStyle = 0x00000020L;
        const long noActivateStyle = 0x08000000L;
        Marshal.SetLastPInvokeError(0);
        var currentStyle = GetWindowLongPtr(handle, extendedStyleIndex).ToInt64();
        if (currentStyle == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            SystemStatusText.Text = $"Не удалось прочитать стиль игрового окна: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
            return false;
        }

        var desiredStyle = _isGamingOverlay && !_overlayEditMode
            ? currentStyle | transparentStyle | noActivateStyle
            : currentStyle & ~transparentStyle & ~noActivateStyle;
        Marshal.SetLastPInvokeError(0);
        var previousStyle = SetWindowLongPtr(handle, extendedStyleIndex, new IntPtr(desiredStyle));
        if (previousStyle == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            SystemStatusText.Text = $"Не удалось изменить стиль игрового окна: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
            return false;
        }

        const uint noSize = 0x0001;
        const uint noMove = 0x0002;
        const uint noZOrder = 0x0004;
        const uint noActivate = 0x0010;
        const uint frameChanged = 0x0020;
        Marshal.SetLastPInvokeError(0);
        if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, noSize | noMove | noZOrder | noActivate | frameChanged))
        {
            var error = Marshal.GetLastPInvokeError();
            _ = SetWindowLongPtr(handle, extendedStyleIndex, new IntPtr(currentStyle));
            _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, noSize | noMove | noZOrder | noActivate | frameChanged);
            var message = error == 0 ? "Windows не применила стиль окна." : new Win32Exception(error).Message;
            SystemStatusText.Text = $"Не удалось применить режим ввода оверлея: {message}";
            return false;
        }

        return true;
    }

    private void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isGamingOverlay || !_hoverExpandEnabled) return;
        _hoverCollapseTimer.Stop();
        if (_state == PanelState.Compact)
            SetState(PanelState.Normal);
    }

    private void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isGamingOverlay || !_hoverExpandEnabled) return;
        if (_state == PanelState.Normal && !_isDragging)
        {
            _hoverCollapseTimer.Stop();
            _hoverCollapseTimer.Start();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isGamingOverlay || _state == PanelState.Fullscreen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_state == PanelState.Compact || IsMouseOver || IsForegroundOwnedByThisWindow()) return;
            SetState(PanelState.Compact);
        }));
    }

    private bool IsForegroundOwnedByThisWindow()
    {
        var owner = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        var visited = new HashSet<IntPtr>();
        while (foreground != IntPtr.Zero && visited.Add(foreground))
        {
            if (foreground == owner) return true;
            foreground = GetWindow(foreground, 4);
        }
        return false;
    }

    private Forms.Screen CurrentScreen()
    {
        if (!string.IsNullOrWhiteSpace(_screenDeviceName))
        {
            var stored = Forms.Screen.AllScreens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, _screenDeviceName, StringComparison.OrdinalIgnoreCase));
            if (stored is not null) return stored;
        }
        var current = ScreenAtWindow();
        _screenDeviceName = current.DeviceName;
        return current;
    }

    private Forms.Screen ScreenAtWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero ? Forms.Screen.FromHandle(handle) : Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
    }

    private (double X, double Y) GetScreenScale(Forms.Screen screen)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && ScreenAtWindow().DeviceName == screen.DeviceName)
        {
            var dpi = GetDpiForWindow(handle);
            if (dpi > 0) return (dpi / 96d, dpi / 96d);
        }

        var center = new NativePoint(
            screen.Bounds.Left + screen.Bounds.Width / 2,
            screen.Bounds.Top + screen.Bounds.Height / 2);
        var monitor = MonitorFromPoint(center, 2);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0 &&
            dpiX > 0 && dpiY > 0)
            return (dpiX / 96d, dpiY / 96d);

        if (handle != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(handle);
            if (dpi > 0) return (dpi / 96d, dpi / 96d);
        }
        var fallbackDpi = VisualTreeHelper.GetDpi(this);
        return (fallbackDpi.DpiScaleX, fallbackDpi.DpiScaleY);
    }

    private void PositionAtEdge(string edge, double? targetWidth = null, double? targetHeight = null)
    {
        var actualWidth = targetWidth ?? (ActualWidth > 0 ? ActualWidth : Width);
        var actualHeight = targetHeight ?? (ActualHeight > 0 ? ActualHeight : Height);
        var position = GetPositionAtEdge(edge, actualWidth, actualHeight);
        Left = position.Left;
        Top = position.Top;
    }

    private (double Left, double Top) GetPositionAtEdge(string edge, double actualWidth, double actualHeight)
    {
        var screen = CurrentScreen();
        var scale = GetScreenScale(screen);
        var work = screen.WorkingArea;
        var workLeft = work.Left / scale.X;
        var workTop = work.Top / scale.Y;
        var workWidth = work.Width / scale.X;
        var workHeight = work.Height / scale.Y;
        return PanelDockGeometry.GetPositionAtEdge(
            edge, workLeft, workTop, workWidth, workHeight, actualWidth, actualHeight);
    }

    private void RestoreFreePosition(double? targetWidth = null, double? targetHeight = null)
    {
        var actualWidth = targetWidth ?? (ActualWidth > 0 ? ActualWidth : Width);
        var actualHeight = targetHeight ?? (ActualHeight > 0 ? ActualHeight : Height);
        var position = GetFreePosition(actualWidth, actualHeight);
        Left = position.Left;
        Top = position.Top;
    }

    private (double Left, double Top) GetFreePosition(double targetWidth, double targetHeight)
    {
        var screen = CurrentScreen();
        var scale = GetScreenScale(screen);
        var bounds = screen.WorkingArea;
        var left = bounds.Left / scale.X;
        var top = bounds.Top / scale.Y;
        var right = (bounds.Left + bounds.Width) / scale.X;
        var bottom = (bounds.Top + bounds.Height) / scale.Y;
        var maxWidth = Math.Max(1, right - left);
        var maxHeight = Math.Max(1, bottom - top);
        var width = Math.Clamp(targetWidth, 1, maxWidth);
        var height = Math.Clamp(targetHeight, 1, maxHeight);
        return (
            _preferences.Left < 0 ? right - width - 10 : Math.Clamp(_preferences.Left, left, right - width),
            _preferences.Top < 0 ? top + 8 : Math.Clamp(_preferences.Top, top, bottom - height));
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Slider
                or System.Windows.Controls.ScrollViewer or System.Windows.Controls.ListBox or System.Windows.Controls.ComboBox)
                return true;
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }
}
