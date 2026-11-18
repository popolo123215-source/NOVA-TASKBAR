using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Nova.Core.Contracts;
using Nova.TransferEngine;
using Nova.WindowsClient.Services;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Forms = System.Windows.Forms;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Nova.WindowsClient;

public partial class ShellWindow : Window
{
    private enum PanelState { Compact, Normal, Expanded, Fullscreen }
    private PanelState _state = PanelState.Compact;
    private PanelState _stateBeforeFullscreen = PanelState.Expanded;
    private bool _allowClose;
    private bool _ready;
    private bool _isDimensionsAnimating;
    private bool _loadingTransportPreference;
    private int _dimensionsAnimationVersion;
    private double _sizeScale = 1;
    private double _widgetIconSize = 24;
    private string? _dockEdge;
    private PanelPreferences _preferences = PanelPreferences.Defaults;
    private bool _keepOnTop = true;
    private bool _hoverExpandEnabled = true;
    private bool _timerCompletionSound = true;
    private bool _enableMotion = true;
    private bool _enableAudioReactive = true;
    private double _audioReactiveIntensity = 1;
    private bool _showCapsuleClock = true;
    private bool _showCapsuleWeather = true;
    private bool _showCapsuleMedia = true;
    private bool _showCapsuleArtwork = true;
    private bool _showCapsulePlaybackStatus = true;
    private bool _showCapsuleCpu = true;
    private bool _showCapsuleGpu = true;
    private bool _showCapsuleRam = true;
    private bool _showCapsuleNetwork;
    private bool _showCapsuleVpn;
    private string _capsuleContentOrder = "ClockWeatherMediaStatus";
    private bool _showOverlayCpu = true;
    private bool _showOverlayGpu = true;
    private bool _showOverlayRam = true;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _displayRefreshTimer;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly DispatcherTimer _obsRefreshTimer;
    private readonly LocalTransferService _transferService;
    private readonly WidgetRegistry _widgetRegistry = new();
    private readonly WidgetHost _widgetHost;
    private readonly ScreenOcrService _screenOcrService = new();
    private readonly BrowserWindowAdapter _browserWindowAdapter;
    private readonly ObservableCollection<DiscoveredDevice> _devices = [];
    private readonly ObservableCollection<TransferFileRow> _outgoingFiles = [];
    private readonly ObservableCollection<IncomingOfferRow> _incomingOffers = [];
    private readonly ObservableCollection<TransferActivityRow> _activities = [];
    private readonly ObservableCollection<TransferHistoryRow> _historyRows = [];
    private readonly ObservableCollection<string> _obsScenes = [];
    private readonly Dictionary<string, TransferActivityRow> _activityById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferFileRow> _fileByTransferId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferFileRow> _webRtcFileByTransferId = new(StringComparer.Ordinal);
    private double _workspaceWidth = 1040;
    private double _workspaceHeight = 700;
    private double? _workspaceLeft;
    private double? _workspaceTop;
    private string? _workspaceScreenDeviceName;
    private string? _obsExecutablePath;
    private ObsWebSocketClient? _obsClient;
    private bool _browserAdaptationEnabled;
    private bool _obsSnapshotInProgress;
    private bool? _workspaceIsVertical;
    private bool? _compactIsVertical;
    private bool _displaySettingsSubscribed;
    private HwndSource? _windowSource;

    internal ShellWindow(LocalTransferService transferService, TrainReminderScheduler trainReminderScheduler)
    {
        InitializeComponent();
        _transferService = transferService;
        _trainReminderScheduler = trainReminderScheduler;
        InitializeTrainScheduleUi();
        _browserWindowAdapter = new BrowserWindowAdapter(GetCapsuleBoundsForAdaptation);
        _browserWindowAdapter.StatusChanged += status => BrowserAdaptationStatusText.Text = status;
        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer.Stop(); SavePreferences(); };
        _displayRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _displayRefreshTimer.Tick += (_, _) =>
        {
            _displayRefreshTimer.Stop();
            RefreshDisplayLayout();
        };
        _deviceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _deviceRefreshTimer.Tick += (_, _) => RefreshDevices();
        _obsRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _obsRefreshTimer.Tick += async (_, _) => await RefreshObsSnapshotAsync();
        _widgetHost = new WidgetHost(WidgetPages, _widgetRegistry);
        RegisterWidgets();
        WidgetNavigationList.ItemsSource = _widgetRegistry.Widgets;
        SelectWidget("Overview", activate: false, save: false);
        SendWebRtcFilesButton.Content = transferService.Settings.TransportPreference == TransferTransportPreference.DirectVpn
            ? "Direct VPN / TCP-TLS"
            : "WebRTC / Internet";
        _loadingTransportPreference = true;
        TransferTransportCombo.SelectedItem = TransferTransportCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag &&
                tag == transferService.Settings.TransportPreference.ToString());
        _loadingTransportPreference = false;

        DevicesList.ItemsSource = _devices;
        OutgoingFilesList.ItemsSource = _outgoingFiles;
        IncomingOffersList.ItemsSource = _incomingOffers;
        TransferProgressList.ItemsSource = _activities;
        HistoryList.ItemsSource = _historyRows;
        ObsSceneComboBox.ItemsSource = _obsScenes;
        LyricsLinesList.ItemsSource = _lyricsLines;

        _transferService.IncomingOffer += OnIncomingOffer;
        _transferService.WebRtcIncomingOffer += OnWebRtcIncomingOffer;
        _transferService.ProgressChanged += OnTransferProgress;
        _transferService.WebRtcProgressChanged += OnWebRtcTransferProgress;
        _transferService.HistoryChanged += OnHistoryChanged;
        _transferService.InternetStatusChanged += OnInternetStatusChanged;
        InitializeIsland();
    }

    private void RegisterWidgets()
    {
        _widgetRegistry.Register(new WidgetDefinition("Music", "Музыка", "Windows Media Session", "♫", MediaContent,
            onActivated: () =>
            {
                RefreshMasterVolume();
                _masterVolumeRefreshTimer.Start();
            },
            onDeactivated: _masterVolumeRefreshTimer.Stop));
        _widgetRegistry.Register(new WidgetDefinition("VPN", "VPN", "Статус и управление Inci", "◇", VpnContent,
            onActivated: RefreshInciStatus));
        _widgetRegistry.Register(new WidgetDefinition("Timer", "Таймер", "Обратный отсчёт и секундомер", "◷", TimerContent));
        _widgetRegistry.Register(new WidgetDefinition("Schedule", "Расписание", "Царское Село → Купчино · локально", "↦", ScheduleContent,
            onActivated: () => RefreshTrainSchedule(force: true)));
        _widgetRegistry.Register(new WidgetDefinition("Monitoring", "Мониторинг", "CPU · GPU · RAM", "⌁", SystemContent));
        _widgetRegistry.Register(new WidgetDefinition("OBS", "OBS", "Сцены и запись · OBS WebSocket", "◉", ObsContent,
            onActivated: () =>
            {
                if (_obsClient is null)
                {
                    ObsStatusText.Text = _obsExecutablePath is null
                        ? "OBS Studio не найден. Установите OBS или выберите «Запустить OBS» после установки."
                        : "Подключитесь к локальному OBS WebSocket. Если OBS закрыт, сначала нажмите «Запустить OBS».";
                }
                else
                {
                    _obsRefreshTimer.Start();
                    _ = RefreshObsSnapshotAsync();
                }
            },
            onDeactivated: _obsRefreshTimer.Stop));
        _widgetRegistry.Register(new WidgetDefinition("Tools", "Инструменты", "Погода, файлы и настройки", "⚒", ToolsContent));
        _widgetRegistry.Register(new WidgetDefinition("Overview", "Обзор", "Главный экран NOVA", "⌂", OverviewContent));
        _widgetRegistry.Register(new WidgetDefinition("Weather", "Погода", "Текущая погода · Open-Meteo", "☁", WeatherContent,
            onActivated: RefreshWeather));
        _widgetRegistry.Register(new WidgetDefinition("Transfer", "Передача файлов", "LAN · Direct VPN · WebRTC", "⇧", TransferContent,
            onActivated: () =>
            {
                TransferStatusText.Text = _transferService.IsStarted
                    ? "Поиск устройств NOVA в локальной сети…"
                    : "Сетевой обмен пока недоступен. Перезапустите NOVA с доступной сетью.";
                RefreshDevices();
            }));
        _widgetRegistry.Register(new WidgetDefinition("History", "История", "Недавние передачи", "↺", HistoryContent,
            onActivated: RefreshHistory));
        _widgetRegistry.Register(new WidgetDefinition("Settings", "Настройки", "Внешний вид и поведение", "⚙", SettingsContent,
            onActivated: () => SetSettingsCategory("General")));
    }

    private void SelectWidget(string id, bool activate = true, bool save = true)
    {
        var widget = _widgetHost.Select(id, activate);
        IslandHeaderTitle.Text = widget.Title;
        IslandHeaderSubtitle.Text = widget.Subtitle;
        if (save) SchedulePreferenceSave();
    }

    private void InitializeOcrLanguages()
    {
        try
        {
            var languages = _screenOcrService.GetAvailableLanguages();
            var choices = new[] { new OcrLanguageOption("Авто · языки профиля Windows", "") }
                .Concat(languages)
                .ToArray();
            OcrLanguageComboBox.ItemsSource = choices;
            OcrLanguageComboBox.SelectedItem = choices[0];
            ScreenTextStatusText.Text = languages.Count == 0
                ? "В Windows не установлены языковые пакеты OCR. Добавьте компонент «Оптическое распознавание символов» в языковых параметрах."
                : $"Доступны языки OCR Windows: {languages.Count}. Снимок экрана не сохраняется.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            OcrLanguageComboBox.ItemsSource = new[] { new OcrLanguageOption("Авто · языки профиля Windows", "") };
            OcrLanguageComboBox.SelectedIndex = 0;
            ScreenTextStatusText.Text = $"Windows OCR недоступен: {ex.Message}";
        }
    }

    private void OpenDestination_Click(object sender, RoutedEventArgs e)
    {
        var destination = _transferService.Settings.DestinationDirectory;
        try
        {
            Directory.CreateDirectory(destination);
            Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TransferStatusText.Text = $"Не удалось открыть папку «{destination}»: {ex.Message}";
        }
    }

    private void RefreshObsInstallationStatus()
    {
        try
        {
            _obsExecutablePath = ObsInstallationLocator.FindExecutable();
            if (_obsExecutablePath is null)
            {
                ObsInstallStatusText.Text = "OBS Studio не найден. Установите OBS, чтобы использовать управление.";
                ObsLaunchButton.IsEnabled = false;
                return;
            }

            var version = FileVersionInfo.GetVersionInfo(_obsExecutablePath).FileVersion;
            ObsInstallStatusText.Text = string.IsNullOrWhiteSpace(version)
                ? $"OBS найден: {_obsExecutablePath}"
                : $"OBS найден · версия {version}";
            ObsLaunchButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _obsExecutablePath = null;
            ObsInstallStatusText.Text = $"Не удалось проверить установку OBS: {ex.Message}";
            ObsLaunchButton.IsEnabled = false;
        }
    }

    private static bool IsObsRunning()
    {
        foreach (var processName in new[] { "obs64", "obs32" })
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var process in processes) process.Dispose();
            if (processes.Length > 0) return true;
        }
        return false;
    }

    private void LaunchObsIfNeeded()
    {
        if (IsObsRunning()) return;
        if (_obsExecutablePath is null || !File.Exists(_obsExecutablePath))
            throw new FileNotFoundException("OBS Studio не установлен или его исполняемый файл недоступен.");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = _obsExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_obsExecutablePath)!,
            UseShellExecute = true
        });
        if (process is null)
            throw new InvalidOperationException("Windows не запустила OBS Studio.");
        process.Dispose();
    }

    private void ObsLaunch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LaunchObsIfNeeded();
            ObsStatusText.Text = "OBS запущен или уже работает. Включите WebSocket-сервер в настройках OBS, затем подключитесь.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ObsStatusText.Text = $"Не удалось запустить OBS: {ex.Message}";
        }
    }

    private async void ObsConnect_Click(object sender, RoutedEventArgs e)
    {
        ObsConnectButton.IsEnabled = false;
        var password = ObsPasswordBox.Password;
        ObsPasswordBox.Clear();
        try
        {
            if (!IsObsRunning())
                throw new InvalidOperationException("OBS не запущен. Сначала нажмите «Запустить OBS».");

            await DisconnectObsAsync();
            if (!int.TryParse(ObsPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "Укажите корректный порт WebSocket OBS.");

            ObsStatusText.Text = $"Подключаемся к OBS на 127.0.0.1:{port}…";
            var client = await ObsWebSocketClient.ConnectAsync(password, port, CancellationToken.None);
            _obsClient = client;
            client.Disconnected += OnObsDisconnected;
            _obsRefreshTimer.Start();
            UpdateObsControls(true, false);
            await RefreshObsSnapshotAsync();
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            ObsStatusText.Text = $"Подключение не выполнено: {ex.Message} Проверьте WebSocket-сервер, пароль и локальный порт OBS.";
            UpdateObsControls(false, false);
        }
        finally
        {
            password = string.Empty;
            ObsConnectButton.IsEnabled = true;
        }
    }

    private async void ObsDisconnect_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectObsAsync();
        ObsStatusText.Text = "Отключено от OBS.";
    }

    private async Task DisconnectObsAsync()
    {
        _obsRefreshTimer.Stop();
        var client = _obsClient;
        _obsClient = null;
        if (client is not null)
        {
            client.Disconnected -= OnObsDisconnected;
            await client.DisposeAsync();
        }
        UpdateObsControls(false, false);
    }

    private void OnObsDisconnected(string reason)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_obsClient is null) return;
            var client = _obsClient;
            _obsClient = null;
            _obsRefreshTimer.Stop();
            UpdateObsControls(false, false);
            ObsStatusText.Text = $"Соединение с OBS потеряно: {reason}";
            _ = client.DisposeAsync();
        }));
    }

    private async Task RefreshObsSnapshotAsync()
    {
        var client = _obsClient;
        if (client is null || !client.IsConnected || _obsSnapshotInProgress) return;
        _obsSnapshotInProgress = true;
        try
        {
            var snapshot = await client.ReadSnapshotAsync(CancellationToken.None);
            if (!ReferenceEquals(client, _obsClient)) return;

            var previousScene = ObsSceneComboBox.SelectedItem as string;
            _obsScenes.Clear();
            foreach (var scene in snapshot.Scenes) _obsScenes.Add(scene);
            ObsSceneComboBox.SelectedItem = snapshot.Scenes.Contains(previousScene ?? "", StringComparer.Ordinal)
                ? previousScene
                : snapshot.CurrentScene;

            ObsStatusText.Text = $"Подключено к OBS {snapshot.Version} · " +
                $"{(snapshot.IsRecording ? "ЗАПИСЬ ИДЁТ" : "запись остановлена")} · " +
                $"сцена: {snapshot.CurrentScene} · {snapshot.RecordingState}";
            UpdateObsControls(true, snapshot.IsRecording);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException or WebSocketException or OperationCanceledException or KeyNotFoundException)
        {
            ObsStatusText.Text = $"Не удалось получить состояние OBS: {ex.Message}";
            if (client.IsConnected) UpdateObsControls(true, false);
        }
        finally { _obsSnapshotInProgress = false; }
    }

    private void UpdateObsControls(bool connected, bool isRecording)
    {
        ObsDisconnectButton.IsEnabled = connected;
        ObsSceneComboBox.IsEnabled = connected && _obsScenes.Count > 0;
        ObsStartRecordingButton.IsEnabled = connected && !isRecording;
        ObsStopRecordingButton.IsEnabled = connected && isRecording;
    }

    private async Task RunObsCommandAsync(Func<ObsWebSocketClient, Task<JsonElement>> command, string successStatus)
    {
        if (_obsClient is not { IsConnected: true } client)
        {
            ObsStatusText.Text = "Сначала подключитесь к OBS.";
            return;
        }

        try
        {
            await command(client);
            ObsStatusText.Text = successStatus;
            await RefreshObsSnapshotAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException or WebSocketException or OperationCanceledException or KeyNotFoundException)
        {
            ObsStatusText.Text = $"Команда OBS не выполнена: {ex.Message}";
        }
    }

    private async void ObsStartRecording_Click(object sender, RoutedEventArgs e) =>
        await RunObsCommandAsync(client => client.StartRecordingAsync(CancellationToken.None), "OBS начал запись.");

    private async void ObsStopRecording_Click(object sender, RoutedEventArgs e) =>
        await RunObsCommandAsync(client => client.StopRecordingAsync(CancellationToken.None), "OBS остановил запись.");

    private async void ObsSetScene_Click(object sender, RoutedEventArgs e)
    {
        if (ObsSceneComboBox.SelectedItem is not string scene)
        {
            ObsStatusText.Text = "Выберите сцену OBS.";
            return;
        }
        await RunObsCommandAsync(
            client => client.SetProgramSceneAsync(scene, CancellationToken.None),
            $"Переключено на сцену «{scene}».");
    }

    private async void ScreenText_Click(object sender, RoutedEventArgs e)
    {
        var languageTag = (OcrLanguageComboBox.SelectedItem as OcrLanguageOption)?.LanguageTag;
        try
        {
            ScreenTextStatusText.Text = "Выделите область экрана…";
            var text = await SelectAndRecognizeScreenRegionAsync(languageTag);
            var resultWindow = new ScreenTextResultWindow(
                this,
                text,
                languageTag,
                () => SelectAndRecognizeScreenRegionAsync(languageTag));
            resultWindow.Show();
            ScreenTextStatusText.Text = text.Length == 0
                ? "Текст не распознан. Выберите более крупную область или проверьте языковой пакет."
                : "Текст распознан. Снимок экрана не сохранялся.";
        }
        catch (OperationCanceledException)
        {
            ScreenTextStatusText.Text = "Распознавание отменено.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or COMException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ScreenTextStatusText.Text = $"Ошибка OCR: {ex.Message}";
        }
    }

    private async Task<string> SelectAndRecognizeScreenRegionAsync(string? languageTag)
    {
        var wasVisible = IsVisible;
        if (wasVisible) Hide();
        try
        {
            using var selector = new ScreenRegionSelector();
            if (selector.ShowDialog() != Forms.DialogResult.OK || selector.SelectedRegion is not { } region)
                throw new OperationCanceledException("Выделение области отменено.");
            return await _screenOcrService.RecognizeAsync(
                region,
                string.IsNullOrWhiteSpace(languageTag) ? null : languageTag);
        }
        finally
        {
            if (wasVisible)
            {
                Show();
                Activate();
            }
        }
    }

    private Rectangle? GetCapsuleBoundsForAdaptation()
    {
        if (!_ready || _isGamingOverlay || !IsVisible || _state != PanelState.Compact)
            return null;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds))
            return null;
        return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            _displaySettingsSubscribed = true;
            var handle = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(handle);
            _windowSource?.AddHook(WindowMessageHook);
        }

        _preferences = PanelPreferences.Load();
        _screenDeviceName = _preferences.ScreenDevice;
        _dockEdge = _preferences.DockEdge is "top" or "bottom" or "left" or "right"
            ? _preferences.DockEdge
            : null;
        _workspaceScreenDeviceName = _preferences.WorkspaceScreenDevice;
        _workspaceLeft = _preferences.WorkspaceLeft;
        _workspaceTop = _preferences.WorkspaceTop;
        _workspaceWidth = Math.Clamp(_preferences.WorkspaceWidth ?? 1040, 900, 1600);
        _workspaceHeight = Math.Clamp(_preferences.WorkspaceHeight ?? 700, 620, 1200);
        var savedWidgetId = _preferences.SelectedWidget == "System" ? "Monitoring" : _preferences.SelectedWidget;
        var savedWidget = _widgetRegistry.Widgets.Any(widget => widget.Id == savedWidgetId)
            ? savedWidgetId!
            : "Overview";
        SelectWidget(savedWidget, activate: false, save: false);
        _overlayScreenDeviceName = _preferences.OverlayScreenDevice;
        _selectedWeatherLocation = _preferences.WeatherLocation;
        WeatherSearchResults.ItemsSource = _weatherLocations;
        CityBox.Text = _selectedWeatherLocation?.Name ?? _preferences.City;
        _sizeScale = Math.Clamp(_preferences.SizeScale, 0.75, 1.1);
        _widgetIconSize = Math.Clamp(_preferences.WidgetIconSize ?? 24, 20, 50);
        _widgetRegistry.SetIconSize(_widgetIconSize);
        _overlayScale = Math.Clamp(_preferences.OverlayScale, 0.8, 1.6);
        _keepOnTop = _preferences.KeepOnTop ?? true;
        _hoverExpandEnabled = _preferences.HoverExpandEnabled ?? true;
        _timerCompletionSound = _preferences.TimerCompletionSound ?? true;
        _enableMotion = _preferences.EnableMotion ?? true;
        _enableAudioReactive = _preferences.EnableAudioReactive ?? true;
        _audioReactiveIntensity = Math.Clamp(_preferences.AudioReactiveIntensity ?? 1, 0.5, 2);
        _showCapsuleCpu = _preferences.ShowCapsuleCpu ?? true;
        _showCapsuleGpu = _preferences.ShowCapsuleGpu ?? true;
        _showCapsuleRam = _preferences.ShowCapsuleRam ?? true;
        _showCapsuleClock = _preferences.ShowCapsuleClock ?? true;
        _showCapsuleWeather = _preferences.ShowCapsuleWeather ?? true;
        _showCapsuleMedia = _preferences.ShowCapsuleMedia ?? true;
        _showCapsuleArtwork = _preferences.ShowCapsuleArtwork ?? true;
        _showCapsulePlaybackStatus = _preferences.ShowCapsulePlaybackStatus ?? true;
        _showCapsuleNetwork = _preferences.ShowCapsuleNetwork ?? false;
        _showCapsuleVpn = _preferences.ShowCapsuleVpn ?? false;
        _capsuleContentOrder = _preferences.CapsuleContentOrder ?? "ClockWeatherMediaStatus";
        _showOverlayCpu = _preferences.ShowOverlayCpu ?? true;
        _showOverlayGpu = _preferences.ShowOverlayGpu ?? true;
        _showOverlayRam = _preferences.ShowOverlayRam ?? true;
        _browserAdaptationEnabled = _preferences.BrowserAdaptationEnabled ?? false;
        _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_preferences.HoverCollapseDelayMs ?? 420, 250, 1500));
        _weatherRefreshTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_preferences.WeatherRefreshMinutes ?? 15, 5, 60));
        _systemStatsTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_preferences.SystemRefreshSeconds ?? 2, 1, 10));
        Topmost = _keepOnTop;
        SizeSlider.Value = _sizeScale;
        WidgetIconSizeSlider.Value = _widgetIconSize;
        OverlaySizeSlider.Value = _overlayScale;
        KeepOnTopCheckBox.IsChecked = _keepOnTop;
        HoverExpandCheckBox.IsChecked = _hoverExpandEnabled;
        TimerCompletionSoundCheckBox.IsChecked = _timerCompletionSound;
        EnableMotionCheckBox.IsChecked = _enableMotion;
        EnableAudioReactiveCheckBox.IsChecked = _enableAudioReactive;
        AudioReactiveIntensitySlider.Value = _audioReactiveIntensity;
        HoverCollapseDelaySlider.Value = _hoverCollapseTimer.Interval.TotalMilliseconds;
        WeatherRefreshSlider.Value = _weatherRefreshTimer.Interval.TotalMinutes;
        SystemRefreshSlider.Value = _systemStatsTimer.Interval.TotalSeconds;
        ShowCapsuleCpuCheckBox.IsChecked = _showCapsuleCpu;
        ShowCapsuleGpuCheckBox.IsChecked = _showCapsuleGpu;
        ShowCapsuleRamCheckBox.IsChecked = _showCapsuleRam;
        ShowCapsuleClockCheckBox.IsChecked = _showCapsuleClock;
        ShowCapsuleWeatherCheckBox.IsChecked = _showCapsuleWeather;
        ShowCapsuleMediaCheckBox.IsChecked = _showCapsuleMedia;
        ShowCapsuleArtworkCheckBox.IsChecked = _showCapsuleArtwork;
        ShowCapsulePlaybackCheckBox.IsChecked = _showCapsulePlaybackStatus;
        ShowCapsuleNetworkCheckBox.IsChecked = _showCapsuleNetwork;
        ShowCapsuleVpnCheckBox.IsChecked = _showCapsuleVpn;
        CapsuleLayoutCombo.SelectedItem = CapsuleLayoutCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string tag && tag == _capsuleContentOrder)
            ?? CapsuleLayoutCombo.Items[0];
        _capsuleContentOrder = (CapsuleLayoutCombo.SelectedItem as ComboBoxItem)?.Tag as string
            ?? "ClockWeatherMediaStatus";
        ShowOverlayCpuCheckBox.IsChecked = _showOverlayCpu;
        ShowOverlayGpuCheckBox.IsChecked = _showOverlayGpu;
        ShowOverlayRamCheckBox.IsChecked = _showOverlayRam;
        BrowserAdaptationCheckBox.IsChecked = _browserAdaptationEnabled;
        UpdateDockUi();
        UpdateAdaptiveLayout();
        UpdateSettingsOptionUi();
        UpdateOverlayMetricVisibility();
        UpdateCompactCapsuleContent();
        PanelSurface.Opacity = 1;
        LocalFingerprintText.Text = FormatFingerprint(_transferService.Identity.Fingerprint);
        LocalPairingCodeBox.Text = CreatePairingCode(_transferService.Identity.Device.DeviceId,
            _transferService.Identity.Device.DisplayName, _transferService.Identity.Fingerprint);
        DestinationPathText.Text = _transferService.Settings.DestinationDirectory;
        DestinationPathText.ToolTip = _transferService.Settings.DestinationDirectory;
        InitializeOcrLanguages();
        RefreshObsInstallationStatus();
        DirectVpnPortText.Text = _transferService.TcpPort > 0
            ? $"Ваш TCP-порт NOVA: {_transferService.TcpPort} · сообщите его доверенному получателю"
            : "Direct VPN listener не запущен · перезапустите NOVA с доступной сетью";
        InternetEndpointBox.Text = _transferService.InternetSettings.Endpoint ?? "";
        OnInternetStatusChanged(_transferService.Internet.Status);
        RefreshDevices();
        RefreshHistory();
        _deviceRefreshTimer.Start();
        var initialWidth = 520 * _sizeScale;
        var initialHeight = 78 * _sizeScale;
        if (_dockEdge is { } dockEdge)
            PositionAtEdge(dockEdge, initialWidth, initialHeight);
        else
            RestoreFreePosition(initialWidth, initialHeight);
        _ready = true;
        SetState(PanelState.Compact);
        UpdateDockUi();
        StartIslandServices();
        _browserWindowAdapter.SetEnabled(_browserAdaptationEnabled);
    }

    private void SetState(PanelState state)
    {
        if (_isGamingOverlay) return;
        if (state == _state && CurrentStateView().Visibility == Visibility.Visible) return;

        var previousState = _state;
        if (state == PanelState.Fullscreen)
        {
            if (previousState is not (PanelState.Normal or PanelState.Expanded)) return;
            _stateBeforeFullscreen = previousState;
            if (_dockEdge is null)
            {
                _workspaceLeft = Left;
                _workspaceTop = Top;
                _workspaceWidth = Width;
                _workspaceHeight = Height;
                _workspaceScreenDeviceName = ScreenAtWindow().DeviceName;
            }
            SavePreferences();
        }
        else if (previousState == PanelState.Fullscreen && state == PanelState.Expanded)
            state = _stateBeforeFullscreen;

        var oldView = CurrentStateView();
        _state = state;
        UpdateAdaptiveLayout();
        UpdateAudioReactiveUi();
        UpdateSurfaceStyle();
        FullscreenToggleButton.Content = _state == PanelState.Fullscreen ? "□  Оконный режим" : "⛶  На весь экран";
        FullscreenToggleButton.ToolTip = _state == PanelState.Fullscreen
            ? "Вернуться в раскрытую панель"
            : "Открыть полноэкранное пространство";
        WorkspaceModeToggleButton.Visibility = _state is PanelState.Normal or PanelState.Expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        WorkspaceModeToggleButton.Content = _state == PanelState.Normal ? "⤢" : "⤡";
        WorkspaceModeToggleButton.ToolTip = _state == PanelState.Normal
            ? "Расширить рабочее пространство"
            : "Перейти в обычный режим";
        WorkspaceFooterText.Text = _state == PanelState.Fullscreen
            ? "F11 ИЛИ ESC — ВЫЙТИ ИЗ ПОЛНОГО ЭКРАНА"
            : "F11 — ПОЛНЫЙ ЭКРАН · NOVA";
        WorkspaceResizeGrip.Visibility = _state == PanelState.Expanded && _dockEdge is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        var next = CurrentStateView();
        var views = new[] { CompactView, ExpandedView };
        if (!_enableMotion)
        {
            foreach (var view in views)
            {
                view.BeginAnimation(OpacityProperty, null);
                view.Visibility = view == next ? Visibility.Visible : Visibility.Collapsed;
                view.Opacity = 1;
            }
            ApplyDimensions();
            SchedulePreferenceSave();
            return;
        }

        if (oldView == next)
        {
            next.Visibility = Visibility.Visible;
            next.Opacity = 1;
            ApplyDimensions();
            SchedulePreferenceSave();
            return;
        }

        foreach (var view in views)
        {
            if (view == next) continue;
            if (view.Visibility != Visibility.Visible)
            {
                view.BeginAnimation(OpacityProperty, null);
                view.Visibility = Visibility.Collapsed;
                view.Opacity = 1;
                continue;
            }

            var fadeOut = new DoubleAnimation(view.Opacity, 0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                if (view == CurrentStateView()) return;
                view.BeginAnimation(OpacityProperty, null);
                view.Visibility = Visibility.Collapsed;
                view.Opacity = 1;
            };
            view.BeginAnimation(OpacityProperty, fadeOut);
        }
        next.Visibility = Visibility.Visible;
        next.Opacity = 0;
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        ApplyDimensions();
        SchedulePreferenceSave();
    }

    private void ApplyDimensions()
    {
        var fromWidth = ActualWidth > 0 ? ActualWidth : Width;
        var fromHeight = ActualHeight > 0 ? ActualHeight : Height;
        var fromLeft = Left;
        var fromTop = Top;
        var animationVersion = ++_dimensionsAnimationVersion;
        _isDimensionsAnimating = true;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        double targetWidth;
        double targetHeight;
        var targetLeft = Left;
        var targetTop = Top;
        if (_isGamingOverlay)
        {
            MinWidth = 160;
            MinHeight = 50;
            targetWidth = Math.Max(MinWidth, 224 * _overlayScale);
            targetHeight = Math.Max(MinHeight, 72 * _overlayScale);
        }
        else if (_state == PanelState.Fullscreen)
        {
            var screen = CurrentWorkspaceScreen();
            var scale = GetScreenScale(screen);
            targetLeft = screen.Bounds.Left / scale.X;
            targetTop = screen.Bounds.Top / scale.Y;
            targetWidth = screen.Bounds.Width / scale.X;
            targetHeight = screen.Bounds.Height / scale.Y;
            MinWidth = 0;
            MinHeight = 0;
        }
        else if (_state is PanelState.Normal or PanelState.Expanded)
        {
            var screen = _dockEdge is null ? CurrentWorkspaceScreen() : CurrentScreen();
            var scale = GetScreenScale(screen);
            var work = screen.WorkingArea;
            var workLeft = work.Left / scale.X;
            var workTop = work.Top / scale.Y;
            var maxWidth = Math.Max(1, work.Width / scale.X - 24);
            var maxHeight = Math.Max(1, work.Height / scale.Y - 24);
            var vertical = PanelDockGeometry.IsVertical(_dockEdge);
            if (_state == PanelState.Normal)
            {
                var normalSize = _dockEdge switch
                {
                    "top" or "bottom" => (W: 920d, H: 520d),
                    "left" or "right" => (W: 620d, H: 820d),
                    _ => (W: 820d, H: 560d)
                };
                MinWidth = Math.Min(vertical ? 500 : 680, maxWidth);
                MinHeight = Math.Min(vertical ? 620 : 440, maxHeight);
                targetWidth = Math.Clamp(normalSize.W * _sizeScale, MinWidth, maxWidth);
                targetHeight = Math.Clamp(normalSize.H * _sizeScale, MinHeight, maxHeight);
            }
            else if (_dockEdge is "top" or "bottom")
            {
                MinWidth = Math.Min(820, maxWidth);
                MinHeight = Math.Min(560, maxHeight);
                targetWidth = Math.Clamp(1240, MinWidth, maxWidth);
                targetHeight = Math.Clamp(700, MinHeight, maxHeight);
            }
            else if (vertical)
            {
                MinWidth = Math.Min(600, maxWidth);
                MinHeight = Math.Min(620, maxHeight);
                targetWidth = Math.Clamp(700, MinWidth, maxWidth);
                targetHeight = Math.Clamp(860, MinHeight, maxHeight);
            }
            else
            {
                MinWidth = Math.Min(900, maxWidth);
                MinHeight = Math.Min(620, maxHeight);
                targetWidth = Math.Clamp(_workspaceWidth, MinWidth, maxWidth);
                targetHeight = Math.Clamp(_workspaceHeight, MinHeight, maxHeight);
            }
            var workRight = workLeft + maxWidth;
            var workBottom = workTop + maxHeight;
            if (_dockEdge is { } edge)
            {
                var position = GetPositionAtEdge(edge, targetWidth, targetHeight);
                targetLeft = position.Left;
                targetTop = position.Top;
            }
            else
            {
                targetLeft = Math.Clamp(_workspaceLeft ?? (workLeft + (maxWidth - targetWidth) / 2), workLeft, workRight - targetWidth);
                targetTop = Math.Clamp(_workspaceTop ?? (workTop + (maxHeight - targetHeight) / 2), workTop, workBottom - targetHeight);
                _workspaceWidth = targetWidth;
                _workspaceHeight = targetHeight;
            }
        }
        else
        {
            var size = _state == PanelState.Compact && PanelDockGeometry.IsVertical(_dockEdge)
                ? (W: 190d, H: 320d)
                : (W: 520d, H: 78d);
            targetWidth = size.W * _sizeScale;
            targetHeight = size.H * _sizeScale;
        }

        if (_isGamingOverlay || _state == PanelState.Compact)
        {
            var targetScreen = _isGamingOverlay ? CurrentOverlayScreen() : CurrentScreen();
            var scale = GetScreenScale(targetScreen);
            var maxWidth = Math.Max(1, targetScreen.WorkingArea.Width / scale.X - 20);
            var maxHeight = Math.Max(1, targetScreen.WorkingArea.Height / scale.Y - 20);
            MinWidth = Math.Min(_isGamingOverlay ? 160 : (_dockEdge is "left" or "right" ? 170 : 380), maxWidth);
            MinHeight = Math.Min(_isGamingOverlay ? 50 : (_dockEdge is "left" or "right" ? 260 : 64), maxHeight);
            targetWidth = Math.Clamp(targetWidth, MinWidth, maxWidth);
            targetHeight = Math.Clamp(targetHeight, MinHeight, maxHeight);
        }

        if (!_isGamingOverlay && _state == PanelState.Compact)
        {
            var position = _dockEdge is { } edge
                ? GetPositionAtEdge(edge, targetWidth, targetHeight)
                : GetFreePosition(targetWidth, targetHeight);
            targetLeft = position.Left;
            targetTop = position.Top;
        }

        var animations = new List<(DependencyProperty Property, double From, double To)>(4);
        AddDimensionAnimation(WidthProperty, fromWidth, targetWidth);
        AddDimensionAnimation(HeightProperty, fromHeight, targetHeight);
        AddDimensionAnimation(LeftProperty, fromLeft, targetLeft);
        AddDimensionAnimation(TopProperty, fromTop, targetTop);

        void AddDimensionAnimation(DependencyProperty property, double from, double to)
        {
            if (Math.Abs(from - to) > 0.1)
                animations.Add((property, from, to));
        }

        Width = targetWidth;
        Height = targetHeight;
        Left = targetLeft;
        Top = targetTop;
        var duration = TimeSpan.FromMilliseconds(_ready && _enableMotion ? 230 : 0);
        if (duration == TimeSpan.Zero || animations.Count == 0)
        {
            CompleteDimensionsChange(animationVersion);
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (var index = 0; index < animations.Count; index++)
        {
            var (property, from, to) = animations[index];
            var animation = new DoubleAnimation(from, to, duration) { EasingFunction = easing };
            if (index == animations.Count - 1)
                animation.Completed += (_, _) => CompleteDimensionsChange(animationVersion);
            BeginAnimation(property, animation);
        }
    }

    private void CompleteDimensionsChange(int animationVersion)
    {
        if (animationVersion != _dimensionsAnimationVersion)
            return;

        _isDimensionsAnimating = false;
        if (_ready && !_isGamingOverlay)
        {
            if ((_state is PanelState.Normal or PanelState.Expanded) && _dockEdge is null)
            {
                _workspaceLeft = Left;
                _workspaceTop = Top;
                _workspaceScreenDeviceName = ScreenAtWindow().DeviceName;
                if (_state == PanelState.Expanded)
                {
                    _workspaceWidth = ActualWidth;
                    _workspaceHeight = ActualHeight;
                }
            }
            else if (_state == PanelState.Compact)
            {
                _screenDeviceName = ScreenAtWindow().DeviceName;
                _preferences = _preferences with { Left = Left, Top = Top, ScreenDevice = _screenDeviceName };
            }
        }

        if (_state is PanelState.Normal or PanelState.Expanded)
            UpdateWorkspaceLayout(GetWorkspaceVerticalOrientation(ActualWidth, ActualHeight));
        UpdateCapsuleGeometry(_capsuleMotion);
        _browserWindowAdapter.ScheduleRefresh();
        if (_ready)
            SchedulePreferenceSave();
    }

    private FrameworkElement CurrentStateView() =>
        _state == PanelState.Compact ? CompactView : ExpandedView;

    private void UpdateSurfaceStyle()
    {
        if (_isGamingOverlay) return;
        if (_state == PanelState.Fullscreen)
        {
            PanelSurface.Background = new WpfSolidColorBrush(WpfColor.FromArgb(0xF2, 0x11, 0x13, 0x17));
            PanelSurface.BorderBrush = new WpfSolidColorBrush(WpfColor.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
            PanelSurface.BorderThickness = new Thickness(1);
            PanelSurface.CornerRadius = new CornerRadius(0);
            CapsuleSurfacePath.Visibility = Visibility.Collapsed;
            return;
        }

        PanelSurface.Background = WpfBrushes.Transparent;
        PanelSurface.BorderBrush = WpfBrushes.Transparent;
        PanelSurface.BorderThickness = new Thickness(0);
        PanelSurface.CornerRadius = new CornerRadius(40);
        CapsuleSurfacePath.Visibility = Visibility.Visible;
    }

    private Forms.Screen CurrentWorkspaceScreen()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceScreenDeviceName))
        {
            var stored = Forms.Screen.AllScreens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, _workspaceScreenDeviceName, StringComparison.OrdinalIgnoreCase));
            if (stored is not null) return stored;
        }

        var current = ScreenAtWindow();
        _workspaceScreenDeviceName = current.DeviceName;
        return current;
    }

    private void SetExpandedSection(string section, bool expand = true)
    {
        if (section == "System")
            section = "Monitoring";

        if (_widgetRegistry.Widgets.Any(widget => widget.Id == section))
        {
            if (expand && _state != PanelState.Fullscreen) SetState(PanelState.Expanded);
            SelectWidget(section);
            return;
        }

        if (expand && _state != PanelState.Fullscreen) SetState(PanelState.Expanded);
        OverviewContent.Visibility = section == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        TransferContent.Visibility = section == "Transfer" ? Visibility.Visible : Visibility.Collapsed;
        HistoryContent.Visibility = section == "History" ? Visibility.Visible : Visibility.Collapsed;
        MediaContent.Visibility = section == "Music" ? Visibility.Visible : Visibility.Collapsed;
        WeatherContent.Visibility = section == "Weather" ? Visibility.Visible : Visibility.Collapsed;
        VpnContent.Visibility = section == "VPN" ? Visibility.Visible : Visibility.Collapsed;
        TimerContent.Visibility = section == "Timer" ? Visibility.Visible : Visibility.Collapsed;
        SystemContent.Visibility = section == "Monitoring" ? Visibility.Visible : Visibility.Collapsed;
        ModuleContent.Visibility = section == "Remote" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "Settings") SetSettingsCategory("General");
        IslandHeaderTitle.Text = section switch
        {
            "Music" => "Музыка",
            "Weather" => "Погода",
            "VPN" => "VPN · Incy",
            "Timer" => "Таймер",
            "Monitoring" => "Мониторинг",
            "OBS" => "OBS Studio",
            "Tools" => "Инструменты",
            "Transfer" => "Передача устройств",
            "History" => "История",
            "Settings" => "Настройки",
            "Remote" => "Удалённое управление",
            _ => "Главная"
        };
        IslandHeaderSubtitle.Text = section switch
        {
            "Music" => "Windows Media Session",
            "Weather" => "Текущая погода · Open-Meteo",
            "VPN" => "Только проверенное управление Inci",
            "Timer" => "Обратный отсчёт и секундомер",
            "Monitoring" => "Показатели Windows",
            "OBS" => "Управление пока не подключено",
            "Tools" => "Быстрый доступ к функциям NOVA",
            _ => "NOVA · Dynamic Island"
        };
        if (section == "Transfer")
        {
            TransferStatusText.Text = _transferService.IsStarted
                ? "Поиск устройств NOVA в локальной сети…"
                : "Сетевой обмен пока недоступен. Перезапустите приложение и попробуйте снова.";
            RefreshDevices();
        }
        else if (section == "History") RefreshHistory();
        else if (section == "VPN") RefreshInciStatus();
        else if (section == "Weather") RefreshWeather();
        else if (section == "Remote")
        {
            ModuleTitle.Text = "Удалённое управление";
            ModuleDescription.Text = "Защищённое управление рабочим столом с вашего разрешения появится в следующих версиях.";
            ModuleStatus.Text = "Удалённый доступ пока недоступен";
        }
    }

    private void Compact_Click(object sender, RoutedEventArgs e) => SetState(PanelState.Compact);
    private void CompactOpen_Click(object sender, RoutedEventArgs e) => SetState(PanelState.Normal);
    private void WorkspaceModeToggle_Click(object sender, RoutedEventArgs e) =>
        SetState(_state == PanelState.Normal ? PanelState.Expanded : PanelState.Normal);
    private void Expand_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Overview");
    private void Overview_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Overview");
    private void Music_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Music");
    private void Weather_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Weather");
    private void Vpn_Click(object sender, RoutedEventArgs e) => SetExpandedSection("VPN");
    private void Timer_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Timer");
    private void System_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Monitoring");
    private void HomeIsland_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Overview", expand: false);
    private void Transfers_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Transfer");
    private void History_Click(object sender, RoutedEventArgs e) => SetExpandedSection("History");
    private void Remote_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Remote");
    private void Settings_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Settings");
    private void SettingsGeneral_Click(object sender, RoutedEventArgs e) => SetSettingsCategory("General");
    private void SettingsIsland_Click(object sender, RoutedEventArgs e) => SetSettingsCategory("Island");
    private void SettingsWeather_Click(object sender, RoutedEventArgs e) => SetSettingsCategory("Weather");
    private void SettingsGaming_Click(object sender, RoutedEventArgs e) => SetSettingsCategory("Gaming");
    private void SettingsMonitoring_Click(object sender, RoutedEventArgs e) => SetSettingsCategory("Monitoring");
    private void SettingsAppearance_Click(object sender, RoutedEventArgs e) => SetSettingsCategory("Appearance");

    private void WidgetNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string widgetId })
            SetExpandedSection(widgetId, expand: false);
    }

    private void OpenToolWidget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string widgetId })
            SetExpandedSection(widgetId);
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) =>
        SetState(_state == PanelState.Fullscreen ? PanelState.Expanded : PanelState.Fullscreen);

    private void CollapseToCapsule_Click(object sender, RoutedEventArgs e)
    {
        if (_state == PanelState.Fullscreen) SetState(PanelState.Expanded);
        SetState(PanelState.Compact);
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (_state == PanelState.Fullscreen) SetState(PanelState.Expanded);
        Hide();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _state == PanelState.Fullscreen)
        {
            SetState(PanelState.Expanded);
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && (_state is PanelState.Normal or PanelState.Expanded or PanelState.Fullscreen))
        {
            SetState(_state == PanelState.Fullscreen ? PanelState.Expanded : PanelState.Fullscreen);
            e.Handled = true;
        }
    }

    private void WorkspaceLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if ((_state is PanelState.Normal or PanelState.Expanded) && !_isDimensionsAnimating)
            UpdateWorkspaceLayout(GetWorkspaceVerticalOrientation(e.NewSize.Width, e.NewSize.Height));
    }

    private bool GetWorkspaceVerticalOrientation(double width, double height)
    {
        if (PanelDockGeometry.IsVertical(_dockEdge)) return true;
        if (_dockEdge is "top" or "bottom") return false;
        if (_state == PanelState.Normal) return false;
        if (_state == PanelState.Compact)
        {
            width = _workspaceWidth;
            height = _workspaceHeight;
        }
        return _workspaceIsVertical == true
            ? height > width * 0.92
            : height > width * 1.08;
    }

    private void UpdateAdaptiveLayout()
    {
        UpdateWorkspaceLayout(GetWorkspaceVerticalOrientation(ActualWidth, ActualHeight));
        var compactVertical = PanelDockGeometry.IsVertical(_dockEdge);
        if (_compactIsVertical == compactVertical) return;

        _compactIsVertical = compactVertical;
        CapsuleContentGrid.RowDefinitions.Clear();
        CapsuleContentGrid.ColumnDefinitions.Clear();
        var blocks = new UIElement[] { CapsuleClockBlock, CapsuleWeatherBlock, CompactMediaButton, CapsuleStatusBlock };
        if (compactVertical)
        {
            CapsuleContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < blocks.Length; index++)
            {
                CapsuleContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(blocks[index], index);
                Grid.SetColumn(blocks[index], 0);
            }
            CapsuleContentGrid.VerticalAlignment = VerticalAlignment.Center;
            CompactView.Margin = new Thickness(10, 18, 10, 18);
            CapsuleClockBlock.Margin = new Thickness(0, 0, 0, 4);
            CapsuleWeatherBlock.Margin = new Thickness(0, 0, 0, 4);
            CompactMediaButton.Margin = new Thickness(0, 0, 0, 4);
            CapsuleStatusBlock.Margin = new Thickness(0);
        }
        else
        {
            CapsuleContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            CapsuleContentGrid.VerticalAlignment = VerticalAlignment.Stretch;
            for (var index = 0; index < blocks.Length; index++)
            {
                CapsuleContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(blocks[index], 0);
                Grid.SetColumn(blocks[index], index);
            }
            CompactView.Margin = new Thickness(18, 10, 18, 10);
            CapsuleClockBlock.Margin = new Thickness(0, 0, 7, 0);
            CapsuleWeatherBlock.Margin = new Thickness(0, 0, 5, 0);
            CompactMediaButton.Margin = new Thickness(5, 4, 5, 4);
            CapsuleStatusBlock.Margin = new Thickness(1, 0, 0, 0);
        }
        UpdateCompactCapsuleContent();
    }

    private void UpdateWorkspaceLayout(bool vertical)
    {
        if (_workspaceIsVertical == vertical) return;
        _workspaceIsVertical = vertical;
        WorkspaceLayout.RowDefinitions.Clear();
        WorkspaceLayout.ColumnDefinitions.Clear();

        if (vertical)
        {
            WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(WidgetNavigationHost, 0);
            Grid.SetColumn(WidgetNavigationHost, 0);
            Grid.SetColumnSpan(WidgetNavigationHost, 1);
            Grid.SetRow(WidgetContentHost, 0);
            Grid.SetColumn(WidgetContentHost, 1);
            Grid.SetColumnSpan(WidgetContentHost, 1);
            WidgetNavigationHost.Width = 148;
            WidgetNavigationHost.Margin = new Thickness(0, 0, 10, 0);
            WidgetNavigationList.ItemsPanel = (ItemsPanelTemplate)FindResource("VerticalWidgetNavigationPanel");
            WidgetNavigationScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            WidgetNavigationScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        else
        {
            WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(WidgetNavigationHost, 0);
            Grid.SetColumn(WidgetNavigationHost, 0);
            Grid.SetColumnSpan(WidgetNavigationHost, 1);
            Grid.SetRow(WidgetContentHost, 1);
            Grid.SetColumn(WidgetContentHost, 0);
            Grid.SetColumnSpan(WidgetContentHost, 1);
            WidgetNavigationHost.Width = double.NaN;
            WidgetNavigationHost.Margin = new Thickness(0, 0, 0, 10);
            WidgetNavigationList.ItemsPanel = (ItemsPanelTemplate)FindResource("HorizontalWidgetNavigationPanel");
            WidgetNavigationScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            WidgetNavigationScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private void WidgetNavigation_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button)
            AnimateNavigationButton(button, 110);
    }

    private void WidgetNavigation_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button)
            AnimateNavigationButton(button, button.DataContext is WidgetDefinition { IsSelected: true } ? 100 : 56);
    }

    private void AnimateNavigationButton(System.Windows.Controls.Button button, double targetWidth)
    {
        var fromWidth = button.ActualWidth > 0 ? button.ActualWidth : button.Width;
        button.BeginAnimation(WidthProperty, null);
        button.Width = fromWidth;
        if (!_enableMotion || Math.Abs(fromWidth - targetWidth) < 0.5)
        {
            button.Width = targetWidth;
            return;
        }

        var animation = new DoubleAnimation(fromWidth, targetWidth, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            button.BeginAnimation(WidthProperty, null);
            button.Width = targetWidth;
        };
        button.BeginAnimation(WidthProperty, animation);
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(QueueDisplayRefresh));

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x02E0 or 0x007E)
            QueueDisplayRefresh();
        return IntPtr.Zero;
    }

    private void QueueDisplayRefresh()
    {
        _displayRefreshTimer.Stop();
        _displayRefreshTimer.Start();
    }

    private void RefreshDisplayLayout()
    {
        if (!_ready) return;
        if (_dockEdge is not null || _state is PanelState.Compact or PanelState.Normal)
            _screenDeviceName = ScreenAtWindow().DeviceName;
        if ((_state is PanelState.Normal or PanelState.Expanded) && _dockEdge is null)
            _workspaceScreenDeviceName = CurrentWorkspaceScreen().DeviceName;
        UpdateAdaptiveLayout();
        ApplyDimensions();
    }

    private void WorkspaceResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_state != PanelState.Expanded || _dockEdge is not null) return;
        var screen = CurrentWorkspaceScreen();
        var scale = GetScreenScale(screen);
        var work = screen.WorkingArea;
        var maxWidth = Math.Max(1, work.Width / scale.X - 24);
        var maxHeight = Math.Max(1, work.Height / scale.Y - 24);
        var minWidth = Math.Min(900, maxWidth);
        var minHeight = Math.Min(620, maxHeight);
        var newWidth = Math.Clamp(ActualWidth + e.HorizontalChange, minWidth, maxWidth);
        var newHeight = Math.Clamp(ActualHeight + e.VerticalChange, minHeight, maxHeight);
        Width = newWidth;
        Height = newHeight;
        _workspaceWidth = newWidth;
        _workspaceHeight = newHeight;
        var workLeft = work.Left / scale.X;
        var workTop = work.Top / scale.Y;
        Left = Math.Clamp(Left, workLeft, workLeft + maxWidth - newWidth);
        Top = Math.Clamp(Top, workTop, workTop + maxHeight - newHeight);
        _workspaceLeft = Left;
        _workspaceTop = Top;
        SchedulePreferenceSave();
    }

    private void SetSettingsCategory(string category)
    {
        GeneralSettingsPanel.Visibility = category == "General" ? Visibility.Visible : Visibility.Collapsed;
        IslandSettingsPanel.Visibility = category == "Island" ? Visibility.Visible : Visibility.Collapsed;
        WeatherSettingsPanel.Visibility = category == "Weather" ? Visibility.Visible : Visibility.Collapsed;
        GamingSettingsPanel.Visibility = category == "Gaming" ? Visibility.Visible : Visibility.Collapsed;
        MonitoringSettingsPanel.Visibility = category == "Monitoring" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSettingsPanel.Visibility = category == "Appearance" ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = new WpfSolidColorBrush(WpfColor.FromArgb(0x38, 0xA9, 0x91, 0xFF));
        var idleBrush = new WpfSolidColorBrush(WpfColor.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        foreach (var (button, key) in new[]
        {
            (SettingsGeneralButton, "General"),
            (SettingsIslandButton, "Island"),
            (SettingsWeatherButton, "Weather"),
            (SettingsGamingButton, "Gaming"),
            (SettingsMonitoringButton, "Monitoring"),
            (SettingsAppearanceButton, "Appearance")
        })
        {
            button.Background = key == category ? activeBrush : idleBrush;
            button.BorderBrush = key == category ? new WpfSolidColorBrush(WpfColor.FromArgb(0x65, 0xA9, 0x91, 0xFF)) : new WpfSolidColorBrush(WpfColor.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }
    }

    private void SettingsOption_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not WpfCheckBox checkBox) return;
        var isChecked = checkBox.IsChecked == true;
        switch (checkBox.Name)
        {
            case nameof(KeepOnTopCheckBox):
                _keepOnTop = isChecked;
                Topmost = _keepOnTop;
                break;
            case nameof(HoverExpandCheckBox):
                _hoverExpandEnabled = isChecked;
                if (!_hoverExpandEnabled && _state == PanelState.Normal) SetState(PanelState.Compact);
                break;
            case nameof(TimerCompletionSoundCheckBox):
                _timerCompletionSound = isChecked;
                break;
            case nameof(BrowserAdaptationCheckBox):
                _browserAdaptationEnabled = isChecked;
                _browserWindowAdapter.SetEnabled(isChecked);
                break;
            case nameof(EnableMotionCheckBox):
                _enableMotion = isChecked;
                break;
            case nameof(EnableAudioReactiveCheckBox):
                _enableAudioReactive = isChecked;
                break;
            case nameof(ShowCapsuleCpuCheckBox):
                _showCapsuleCpu = isChecked;
                break;
            case nameof(ShowCapsuleGpuCheckBox):
                _showCapsuleGpu = isChecked;
                break;
            case nameof(ShowCapsuleRamCheckBox):
                _showCapsuleRam = isChecked;
                break;
            case nameof(ShowCapsuleClockCheckBox):
                _showCapsuleClock = isChecked;
                break;
            case nameof(ShowCapsuleWeatherCheckBox):
                _showCapsuleWeather = isChecked;
                break;
            case nameof(ShowCapsuleMediaCheckBox):
                _showCapsuleMedia = isChecked;
                break;
            case nameof(ShowCapsuleArtworkCheckBox):
                _showCapsuleArtwork = isChecked;
                break;
            case nameof(ShowCapsulePlaybackCheckBox):
                _showCapsulePlaybackStatus = isChecked;
                break;
            case nameof(ShowCapsuleNetworkCheckBox):
                _showCapsuleNetwork = isChecked;
                break;
            case nameof(ShowCapsuleVpnCheckBox):
                _showCapsuleVpn = isChecked;
                break;
            case nameof(ShowOverlayCpuCheckBox):
                _showOverlayCpu = isChecked;
                break;
            case nameof(ShowOverlayGpuCheckBox):
                _showOverlayGpu = isChecked;
                break;
            case nameof(ShowOverlayRamCheckBox):
                _showOverlayRam = isChecked;
                break;
            default:
                return;
        }

        UpdateSettingsOptionUi();
        UpdateCompactMetrics();
        UpdateCompactCapsuleContent();
        UpdateOverlayMetricVisibility();
        if (checkBox.Name is nameof(EnableMotionCheckBox) or nameof(EnableAudioReactiveCheckBox))
            UpdateAudioReactiveUi();
        SchedulePreferenceSave();
    }

    private void CapsuleLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CapsuleLayoutCombo.SelectedItem is not ComboBoxItem { Tag: string order }) return;
        _capsuleContentOrder = order;
        UpdateCompactCapsuleContent();
        SchedulePreferenceSave();
    }

    private void SettingsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || sender is not Slider slider) return;
        switch (slider.Name)
        {
            case nameof(HoverCollapseDelaySlider):
                _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds((int)e.NewValue);
                HoverCollapseDelayValue.Text = $"{(int)e.NewValue} мс";
                break;
            case nameof(WeatherRefreshSlider):
                _weatherRefreshTimer.Interval = TimeSpan.FromMinutes((int)e.NewValue);
                WeatherRefreshValue.Text = $"{(int)e.NewValue} мин";
                break;
            case nameof(SystemRefreshSlider):
                _systemStatsTimer.Interval = TimeSpan.FromSeconds((int)e.NewValue);
                SystemRefreshValue.Text = $"{(int)e.NewValue} с";
                break;
            case nameof(AudioReactiveIntensitySlider):
                _audioReactiveIntensity = e.NewValue;
                AudioReactiveIntensityValue.Text = $"{e.NewValue:0.0}×";
                UpdateAudioReactiveUi();
                break;
            case nameof(WidgetIconSizeSlider):
                _widgetIconSize = Math.Clamp(e.NewValue, 20, 50);
                _widgetRegistry.SetIconSize(_widgetIconSize);
                WidgetIconSizeValue.Text = $"{_widgetIconSize:0} DIP";
                break;
            default:
                return;
        }
        SchedulePreferenceSave();
    }

    private void UpdateSettingsOptionUi()
    {
        GamingOverlayButton.Content = _isGamingOverlay ? "Выключить игровой оверлей" : "Включить игровой оверлей";
        HoverCollapseDelayValue.Text = $"{_hoverCollapseTimer.Interval.TotalMilliseconds:0} мс";
        WeatherRefreshValue.Text = $"{_weatherRefreshTimer.Interval.TotalMinutes:0} мин";
        SystemRefreshValue.Text = $"{_systemStatsTimer.Interval.TotalSeconds:0} с";
        AudioReactiveIntensityValue.Text = $"{_audioReactiveIntensity:0.0}×";
        WidgetIconSizeValue.Text = $"{_widgetIconSize:0} DIP";
    }

    private void DevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

    private void TransferTransport_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingTransportPreference || TransferTransportCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value || !Enum.TryParse(value, out TransferTransportPreference preference) ||
            !Enum.IsDefined(typeof(TransferTransportPreference), preference))
            return;
        try
        {
            _transferService.Settings.SetTransportPreference(preference);
            SendWebRtcFilesButton.Content = preference == TransferTransportPreference.DirectVpn
                ? "Direct VPN / TCP-TLS"
                : "WebRTC / Internet";
            TransferStatusText.Text = preference == TransferTransportPreference.DirectVpn
                ? "Direct VPN: выберите доверенный контакт, введите его Radmin IPv4 и показанный на нём TCP-порт."
                : $"Маршрут передачи: {item.Content}. Для принудительных режимов выберите то же значение на устройстве-получателе.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, $"Не удалось сохранить маршрут передачи: {ex.Message}", "NOVA",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshDevices()
    {
        if (!_ready && !IsLoaded) return;
        var selectedId = (DevicesList.SelectedItem as DiscoveredDevice)?.DeviceId;
        var snapshot = _transferService.GetAvailableDevices();
        _devices.Clear();
        foreach (var device in snapshot) _devices.Add(device);
        DevicesList.SelectedItem = _devices.FirstOrDefault(x => x.DeviceId == selectedId);
        if (snapshot.Count == 0)
            TransferStatusText.Text = "Устройства появятся здесь, когда NOVA запущена на другом компьютере в этой сети.";
        else
            TransferStatusText.Text = $"Найдено устройств: {snapshot.Count}. Для передачи подтвердите их код безопасности.";
    }

    private void TrustDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is not DiscoveredDevice device)
        {
            System.Windows.MessageBox.Show(this, "Сначала выберите устройство из списка.", "NOVA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        if (device.IsTrusted)
        {
            System.Windows.MessageBox.Show(this, "Это устройство уже подтверждено.", "NOVA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var code = FormatFingerprint(device.Fingerprint);
        var result = System.Windows.MessageBox.Show(this,
            $"На обоих компьютерах откройте раздел «Передача» и сравните полный код устройства.\n\nУстройство: {device.DisplayName}\nКод: {code}\n\nПодтвердить доверие можно только при полном совпадении кодов.",
            "Проверка устройства", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;
        _transferService.TrustDevice(device);
        RefreshDevices();
        TransferStatusText.Text = $"Устройство «{device.DisplayName}» подтверждено. Подтвердите NOVA на обоих компьютерах.";
    }

    private static string CreatePairingCode(string deviceId, string displayName, string fingerprint)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new PairingCodePayload(deviceId, displayName, fingerprint));
        return "NOVA1:" + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void CopyPairingCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(LocalPairingCodeBox.Text);
            TransferStatusText.Text = "Код устройства скопирован. Передавайте его только по доверенному каналу.";
        }
        catch (Exception ex) { TransferStatusText.Text = $"Не удалось скопировать код: {ex.Message}"; }
    }

    private void TrustInternetCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            const string prefix = "NOVA1:";
            var code = PairingCodeBox.Text.Trim();
            if (!code.StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException("Код должен начинаться с NOVA1:.");
            var encoded = code[prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var payload = JsonSerializer.Deserialize<PairingCodePayload>(Convert.FromBase64String(encoded),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new FormatException("Пустой код устройства.");
            if (!Guid.TryParseExact(payload.DeviceId, "N", out var parsedId)) throw new FormatException("Неверный ID устройства.");
            var deviceId = parsedId.ToString("N");
            var displayName = payload.DisplayName?.Trim();
            var fingerprint = payload.Fingerprint?.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            if (deviceId == _transferService.Identity.Device.DeviceId || fingerprint == _transferService.Identity.Fingerprint)
                throw new InvalidOperationException("Нельзя добавить это же устройство.");
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80 || displayName.Any(char.IsControl) || !PeerTrustStore.IsValidFingerprint(fingerprint ?? ""))
                throw new FormatException("Код содержит некорректные данные.");
            _transferService.TrustDevice(new DiscoveredDevice(deviceId, displayName, System.Net.IPAddress.None, 0,
                fingerprint!, DateTimeOffset.UtcNow, false, DeviceDiscoverySource.Internet, false, false));
            PairingCodeBox.Clear();
            RefreshDevices();
            TransferStatusText.Text = $"Устройство «{displayName}» добавлено. Повторите то же действие на втором устройстве.";
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or System.IO.IOException)
        {
            TransferStatusText.Text = $"Не удалось добавить устройство: {ex.Message}";
        }
    }

    private async void ConnectInternet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _transferService.ConnectInternetAsync(InternetEndpointBox.Text.Trim());
            InternetStatusText.Text = "Подключаемся к NOVA Network…";
        }
        catch (Exception ex)
        {
            InternetStatusText.Text = $"Не удалось подключить NOVA Network: {ex.Message}";
        }
    }

    private void OnInternetStatusChanged(InternetConnectionStatus status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            InternetStatusText.Text = status.State switch
            {
                InternetConnectionState.Connected => "NOVA Network подключена",
                InternetConnectionState.Connecting => "Подключение к NOVA Network…",
                InternetConnectionState.Reconnecting => "Восстанавливаем соединение с NOVA Network…",
                InternetConnectionState.Failed => $"NOVA Network недоступна: {status.Detail}",
                _ => "Internet Mode отключён"
            };
            RefreshDevices();
        });
    }

    private void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Выберите файлы для передачи", Filter = "Все файлы|*.*", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
            if (!_outgoingFiles.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                _outgoingFiles.Add(new TransferFileRow(path));
    }

    private void ChooseDestination_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку для принятых файлов",
            UseDescriptionForTitle = true,
            SelectedPath = _transferService.Settings.DestinationDirectory,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            _transferService.SetDestinationDirectory(dialog.SelectedPath);
            DestinationPathText.Text = _transferService.Settings.DestinationDirectory;
            DestinationPathText.ToolTip = _transferService.Settings.DestinationDirectory;
            TransferStatusText.Text = "Папка получения сохранена.";
        }
        catch
        {
            System.Windows.MessageBox.Show(this, "Не удалось выбрать эту папку.", "NOVA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is not DiscoveredDevice peer)
        {
            System.Windows.MessageBox.Show(this, "Выберите устройство-получатель.", "NOVA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        if (!peer.IsTrusted || !_transferService.TrustedPeers.IsTrusted(peer.Fingerprint))
        {
            System.Windows.MessageBox.Show(this, "Перед отправкой подтвердите совпадение кодов безопасности на обоих компьютерах.", "NOVA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        var rows = _outgoingFiles.Where(x => x.Status == "Выбран").ToArray();
        if (rows.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "Сначала выберите один или несколько файлов.", "NOVA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        foreach (var row in rows) row.Status = "В очереди";
        TransferStatusText.Text = $"Подготовка передачи на устройство «{peer.DisplayName}»…";
        try
        {
            await _transferService.SendFilesAsync(peer, rows.Select(x => x.Path));
            TransferStatusText.Text = "Передача завершена.";
        }
        catch
        {
            var active = rows.FirstOrDefault(x => x.Status is "Ошибка" or "Ожидает подтверждения" or "Принят" or "Передаётся")
                ?? rows.FirstOrDefault(x => x.Status == "В очереди");
            foreach (var row in rows)
            {
                if (row == active) row.Status = "Ошибка";
                else if (row.Status == "В очереди") row.Status = "Выбран";
            }
            TransferStatusText.Text = "Не удалось передать файл. Проверьте соединение и попробуйте снова.";
            System.Windows.MessageBox.Show(this, "Передача не завершена. Проверьте, что устройство в сети и подтверждено на обоих компьютерах.", "NOVA", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnIncomingOffer(IncomingOfferRequest request)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var offer = request.Offer;
            _incomingOffers.Add(new IncomingOfferRow(request.Offer, request.Respond, false));
            var route = offer.TransportPreference == TransferTransportPreference.DirectVpn ? " · Direct VPN" : "";
            TransferStatusText.Text = $"Входящий файл от {offer.Sender.DisplayName}: {offer.FileName} ({FormatBytes(offer.SizeBytes)}){route}.";
        });
    }

    private void OnWebRtcIncomingOffer(WebRtcIncomingOfferRequest request)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var offer = request.Offer;
            _incomingOffers.Add(new IncomingOfferRow(offer, request.Respond, true));
            TransferStatusText.Text = $"Входящий файл от {offer.Sender.DisplayName}: {offer.FileName} ({FormatBytes(offer.SizeBytes)}) · {DescribeTransportPreference(offer.TransportPreference)}.";
        });
    }

    private static string DescribeTransportPreference(TransferTransportPreference preference) => preference switch
    {
        TransferTransportPreference.Automatic => "авто: P2P/TURN, при сбое TCP/TLS relay",
        TransferTransportPreference.DirectP2P => "только Direct P2P",
        TransferTransportPreference.TurnUdp => "только TURN UDP",
        TransferTransportPreference.TurnTcp => "только TURN TCP",
        TransferTransportPreference.TurnTls => "только TURN TLS",
        TransferTransportPreference.TcpTlsFileRelay => "TCP/TLS relay через сервер NOVA",
        TransferTransportPreference.DirectVpn => "Direct VPN · прямой TCP/TLS по IPv4",
        _ => "неизвестный маршрут"
    };

    private void AcceptIncoming_Click(object sender, RoutedEventArgs e)
    {
        if (IncomingOffersList.SelectedItem is not IncomingOfferRow row) return;
        row.Respond(true);
        _incomingOffers.Remove(row);
        TransferStatusText.Text = "Файл принят. Идёт проверка и сохранение.";
    }

    private void RejectIncoming_Click(object sender, RoutedEventArgs e)
    {
        if (IncomingOffersList.SelectedItem is not IncomingOfferRow row) return;
        row.Respond(false);
        _incomingOffers.Remove(row);
        TransferStatusText.Text = "Передача отклонена.";
    }

    private void OnTransferProgress(LocalTransferProgress progress)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_activityById.TryGetValue(progress.TransferId, out var activity))
            {
                activity = new TransferActivityRow(progress.TransferId, progress.FileName);
                _activityById[progress.TransferId] = activity;
                _activities.Insert(0, activity);
                if (_activities.Count > 30) _activities.RemoveAt(_activities.Count - 1);
            }
            activity.Update(progress);
            if (progress.SavedPath is not null)
                TransferStatusText.Text = $"Файл сохранён: {progress.SavedPath}";
            if (progress.State == TransferState.Offered)
            {
                var file = _outgoingFiles.FirstOrDefault(x => x.Status == "В очереди" && string.Equals(x.Name, progress.FileName, StringComparison.OrdinalIgnoreCase));
                if (file is not null) _fileByTransferId[progress.TransferId] = file;
            }
            if (_fileByTransferId.TryGetValue(progress.TransferId, out var row))
                row.Status = ToRussian(progress.State);
        });
    }

    private void OnWebRtcTransferProgress(WebRtcSessionProgress progress)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_activityById.TryGetValue(progress.TransferId, out var activity))
            {
                activity = new TransferActivityRow(progress.TransferId, progress.FileName);
                _activityById[progress.TransferId] = activity;
                _activities.Insert(0, activity);
                if (_activities.Count > 30) _activities.RemoveAt(_activities.Count - 1);
            }
            activity.Update(progress);
            if (progress.SavedPath is not null)
                TransferStatusText.Text = $"Файл сохранён: {progress.SavedPath}";
            if (!_webRtcFileByTransferId.TryGetValue(progress.TransferId, out var fileRow))
            {
                fileRow = _outgoingFiles.FirstOrDefault(x => x.Status == "WebRTC: ожидание" &&
                    string.Equals(x.Name, progress.FileName, StringComparison.OrdinalIgnoreCase));
                if (fileRow is not null) _webRtcFileByTransferId[progress.TransferId] = fileRow;
            }
            if (fileRow is not null)
            {
                fileRow.Status = progress.State switch
                {
                    WebRtcTransferSessionState.PendingApproval => "WebRTC: ожидание согласия",
                    WebRtcTransferSessionState.Connecting => "WebRTC: подключение",
                    WebRtcTransferSessionState.Transferring => "WebRTC: передача",
                    WebRtcTransferSessionState.Verifying => "WebRTC: проверка",
                    WebRtcTransferSessionState.Completed => "WebRTC: готово",
                    WebRtcTransferSessionState.Rejected => "WebRTC: отклонено",
                    WebRtcTransferSessionState.Cancelled => "WebRTC: отменено",
                    WebRtcTransferSessionState.Failed => "WebRTC: ошибка",
                    _ => "WebRTC"
                };
            }
            if (progress.Error is not null)
            {
                TransferStatusText.Text = $"WebRTC: {progress.Error}";
            }
            else if (progress.State is WebRtcTransferSessionState.Connecting or WebRtcTransferSessionState.Transferring)
            {
                var route = progress.ConnectionPath ?? "ожидается";
                TransferStatusText.Text =
                    $"WebRTC · ICE {progress.IceState} · канал {progress.DataChannelState} · маршрут {route} · TURN {(progress.TurnConfigured ? "настроен" : "не настроен")} · кандидаты {progress.LocalIceCandidates}/{progress.RemoteIceCandidates}";
            }
        });
    }

    private async void SendWebRtcFiles_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is not DiscoveredDevice peer || !peer.IsTrusted || !_transferService.TrustedPeers.IsTrusted(peer.Fingerprint))
        {
            System.Windows.MessageBox.Show(this, "Выберите и подтвердите доверенное устройство.", "NOVA", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var rows = _outgoingFiles.Where(x => x.Status == "Выбран").ToArray();
        if (rows.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "Сначала выберите файлы.", "NOVA", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_transferService.Settings.TransportPreference == TransferTransportPreference.DirectVpn)
        {
            var addressText = DirectVpnAddressBox.Text.Trim();
            if (!System.Net.IPAddress.TryParse(addressText, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                !string.Equals(address.ToString(), addressText, StringComparison.Ordinal))
            {
                System.Windows.MessageBox.Show(this, "Введите числовой IPv4-адрес устройства в сети Radmin VPN.", "NOVA",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(DirectVpnPortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1 or > 65535)
            {
                System.Windows.MessageBox.Show(this, "Введите TCP-порт NOVA, показанный на устройстве-получателе.", "NOVA",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var target = peer with { Address = address, Port = port, Source = DeviceDiscoverySource.None, IsOnline = true };
            foreach (var row in rows) row.Status = "В очереди";
            TransferStatusText.Text = $"Direct VPN: подключение к {address}:{port} на устройстве «{peer.DisplayName}».";
            try
            {
                await _transferService.SendDirectVpnFilesAsync(target, rows.Select(x => x.Path));
                TransferStatusText.Text = "Direct VPN: передача завершена.";
            }
            catch (OperationCanceledException)
            {
                foreach (var row in rows.Where(x => x.Status is "В очереди" or "Передаётся")) row.Status = "Отменено";
                TransferStatusText.Text = "Direct VPN: передача отменена.";
            }
            catch (Exception ex)
            {
                foreach (var row in rows.Where(x => x.Status is "В очереди" or "Ожидает подтверждения" or "Принят" or "Передаётся"))
                    row.Status = "Ошибка Direct VPN";
                TransferStatusText.Text = $"Direct VPN: {ex.Message}";
            }
            return;
        }
        foreach (var row in rows) row.Status = "WebRTC: ожидание";
        TransferStatusText.Text = $"WebRTC: запрос отправлен устройству «{peer.DisplayName}». Ожидается согласие.";
        try
        {
            await _transferService.SendWebRtcFilesAsync(peer, rows.Select(x => x.Path));
            TransferStatusText.Text = "WebRTC: передача завершена.";
        }
        catch (WebRtcTransferRejectedException ex)
        {
            foreach (var row in rows.Where(x => x.Status.StartsWith("WebRTC:", StringComparison.Ordinal))) row.Status = "WebRTC: отклонено";
            TransferStatusText.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            foreach (var row in rows.Where(x => x.Status.StartsWith("WebRTC:", StringComparison.Ordinal))) row.Status = "WebRTC: отменено";
            TransferStatusText.Text = "WebRTC: передача отменена.";
        }
        catch (Exception ex)
        {
            foreach (var row in rows.Where(x => x.Status.StartsWith("WebRTC:", StringComparison.Ordinal))) row.Status = "Ошибка WebRTC";
            TransferStatusText.Text = $"WebRTC: {ex.Message}";
        }
    }

    private void CancelWebRtcTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransferProgressList.SelectedItem is not TransferActivityRow row) return;
        if (_transferService.CancelDirectVpnTransfer(row.TransferId))
            TransferStatusText.Text = $"Direct VPN: отмена {row.FileName}.";
        else if (_transferService.CancelWebRtcTransfer(row.TransferId))
            TransferStatusText.Text = $"WebRTC: отмена {row.FileName}.";
    }

    private void OnHistoryChanged(TransferHistoryEntry entry)
    {
        Dispatcher.InvokeAsync(() => AddHistoryRow(entry));
    }

    private void RefreshHistory()
    {
        _historyRows.Clear();
        foreach (var entry in _transferService.History.GetRecent(100)) AddHistoryRow(entry);
    }

    private void AddHistoryRow(TransferHistoryEntry entry)
    {
        if (_historyRows.Any(x => x.TransferId == entry.TransferId && x.Direction == entry.Direction)) return;
        _historyRows.Insert(0, new TransferHistoryRow(entry));
        while (_historyRows.Count > 100) _historyRows.RemoveAt(_historyRows.Count - 1);
    }

    private static string ToRussian(TransferState state) => state switch
    {
        TransferState.Offered => "Ожидает подтверждения",
        TransferState.Accepted => "Принят",
        TransferState.Transferring => "Передаётся",
        TransferState.Paused => "Приостановлен",
        TransferState.Completed => "Готово",
        TransferState.Rejected => "Отклонён",
        TransferState.Cancelled => "Отменено",
        _ => "Ошибка"
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CapsuleWindowRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr handle, out CapsuleWindowRectangle rectangle);

    private static string FormatFingerprint(string fingerprint) =>
        string.Join("-", Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return; e.Cancel = true; Hide();
    }
    public async Task AllowCloseAsync()
    {
        _settingsSaveTimer.Stop();
        _displayRefreshTimer.Stop();
        _deviceRefreshTimer.Stop();
        _obsRefreshTimer.Stop();
        StopIslandServices();
        _browserWindowAdapter.Dispose();
        if (_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _displaySettingsSubscribed = false;
        }
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        await DisconnectObsAsync();
        SavePreferences();
        _allowClose = true;
    }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !IsInteractiveSource(e.OriginalSource as DependencyObject) &&
            (_isGamingOverlay ? _overlayEditMode : _state != PanelState.Fullscreen))
        {
            var startLeft = Left;
            var startTop = Top;
            _isDragging = true;
            StartDragTrail();
            try { DragMove(); }
            finally
            {
                _isDragging = false;
                StopDragTrail();
            }

            if (_isGamingOverlay)
            {
                _overlayScreenDeviceName = ScreenAtWindow().DeviceName;
                _overlayEditMode = false;
                SavePreferences();
                UpdateOverlayInputMode();
                return;
            }

            if (Math.Abs(Left - startLeft) < 2 && Math.Abs(Top - startTop) < 2)
                return;

            _dockEdge = null;
            UpdateAdaptiveLayout();
            UpdateDockUi();
            WorkspaceResizeGrip.Visibility = _state == PanelState.Expanded ? Visibility.Visible : Visibility.Collapsed;
            var screen = ScreenAtWindow();
            _screenDeviceName = screen.DeviceName;
            if (TryFindDockEdge(out var edge))
            {
                DockTo(edge);
                return;
            }

            if ((_state is PanelState.Normal or PanelState.Expanded) && _dockEdge is null)
            {
                _workspaceScreenDeviceName = screen.DeviceName;
                _workspaceLeft = Left;
                _workspaceTop = Top;
            }
            else
                _preferences = _preferences with { Left = Left, Top = Top, ScreenDevice = _screenDeviceName };

            UpdateAdaptiveLayout();
            ApplyDimensions();
            SchedulePreferenceSave();
        }
    }

    private bool TryFindDockEdge(out string edge)
    {
        edge = "";
        var screen = ScreenAtWindow();
        _screenDeviceName = screen.DeviceName;
        var scale = GetScreenScale(screen);
        var work = screen.WorkingArea;
        var workLeft = work.Left / scale.X;
        var workTop = work.Top / scale.Y;
        var workRight = (work.Left + work.Width) / scale.X;
        var workBottom = (work.Top + work.Height) / scale.Y;
        var nearest = PanelDockGeometry.FindNearestEdge(
            Left,
            Top,
            Width,
            Height,
            workLeft,
            workTop,
            workRight - workLeft,
            workBottom - workTop);
        if (nearest is null)
            return false;
        edge = nearest;
        return true;
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return; _sizeScale = e.NewValue; ApplyDimensions(); SchedulePreferenceSave();
    }
    private void DockTop_Click(object sender, RoutedEventArgs e) => DockTo("top");
    private void DockBottom_Click(object sender, RoutedEventArgs e) => DockTo("bottom");
    private void DockLeft_Click(object sender, RoutedEventArgs e) => DockTo("left");
    private void DockRight_Click(object sender, RoutedEventArgs e) => DockTo("right");

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isDimensionsAnimating)
        {
            UpdateCapsuleGeometry(_capsuleMotion);
            _browserWindowAdapter.ScheduleRefresh();
        }
        if (_isGamingOverlay) return;
        if (!_isDimensionsAnimating && _dockEdge is not null && _state != PanelState.Fullscreen)
            PositionAtEdge(_dockEdge);
        if (_state == PanelState.Expanded && _dockEdge is null && _ready && !_isDimensionsAnimating)
        {
            _workspaceWidth = Width;
            _workspaceHeight = Height;
            SchedulePreferenceSave();
        }
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (_isDimensionsAnimating) return;
        _browserWindowAdapter.ScheduleRefresh();
        if (!_ready || _isGamingOverlay || _state == PanelState.Fullscreen) return;
        if ((_state is PanelState.Normal or PanelState.Expanded) && _dockEdge is null)
        {
            _workspaceScreenDeviceName = ScreenAtWindow().DeviceName;
            _workspaceLeft = Left;
            _workspaceTop = Top;
        }
        else if (_state == PanelState.Compact)
        {
            _screenDeviceName = ScreenAtWindow().DeviceName;
            _preferences = _preferences with { Left = Left, Top = Top, ScreenDevice = _screenDeviceName };
        }
        SchedulePreferenceSave();
    }

    private sealed record PairingCodePayload(string DeviceId, string DisplayName, string Fingerprint);

    private void DockTo(string edge)
    {
        _dockEdge = edge;
        _screenDeviceName = ScreenAtWindow().DeviceName;
        _workspaceScreenDeviceName = _screenDeviceName;
        UpdateAdaptiveLayout();
        UpdateDockUi();
        WorkspaceResizeGrip.Visibility = Visibility.Collapsed;
        ApplyDimensions();
        SavePreferences();
    }

    private void DockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_dockEdge is null)
        {
            DockTo("top");
            return;
        }

        if (_state is PanelState.Normal or PanelState.Expanded)
        {
            _workspaceLeft = Left;
            _workspaceTop = Top;
            _workspaceScreenDeviceName = ScreenAtWindow().DeviceName;
        }
        else
            _preferences = _preferences with { Left = Left, Top = Top, ScreenDevice = ScreenAtWindow().DeviceName };
        _dockEdge = null;
        _screenDeviceName = ScreenAtWindow().DeviceName;
        UpdateAdaptiveLayout();
        UpdateDockUi();
        WorkspaceResizeGrip.Visibility = _state == PanelState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        ApplyDimensions();
        SavePreferences();
    }

    private void UpdateDockUi()
    {
        DockToggleButton.Content = _dockEdge is null ? "⌖" : "↗";
        DockToggleButton.ToolTip = _dockEdge is null
            ? "Прикрепить к верхнему краю"
            : $"Открепить от края «{DescribeDockEdge(_dockEdge)}»";
        DockStatusText.Text = _dockEdge switch
        {
            "top" => "Прикреплена сверху · горизонтальная компоновка.",
            "bottom" => "Прикреплена снизу · горизонтальная компоновка.",
            "left" => "Прикреплена слева · вертикальная компоновка.",
            "right" => "Прикреплена справа · вертикальная компоновка.",
            _ => "Откреплена. Перетащите панель к краю экрана, чтобы прикрепить."
        };
    }

    private static string DescribeDockEdge(string edge) => edge switch
    {
        "top" => "верх",
        "bottom" => "низ",
        "left" => "левый",
        "right" => "правый",
        _ => edge
    };

    private void StartDragTrail()
    {
        DragTrail.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 0.8, TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void StopDragTrail()
    {
        var currentOpacity = DragTrail.Opacity;
        DragTrail.BeginAnimation(OpacityProperty, null);
        DragTrail.BeginAnimation(OpacityProperty, new DoubleAnimation(currentOpacity, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void SchedulePreferenceSave() { _settingsSaveTimer.Stop(); _settingsSaveTimer.Start(); }

    private void SavePreferences()
    {
        if (!_ready) return;
        _preferences = _preferences with
        {
            DockEdge = _isGamingOverlay || _state == PanelState.Fullscreen ? _preferences.DockEdge : _dockEdge,
            Left = _isGamingOverlay || (_state is PanelState.Normal or PanelState.Expanded or PanelState.Fullscreen) ? _preferences.Left : Left,
            Top = _isGamingOverlay || (_state is PanelState.Normal or PanelState.Expanded or PanelState.Fullscreen) ? _preferences.Top : Top,
            SizeScale = _sizeScale,
            Opacity = 1,
            City = CityBox.Text.Trim(),
            ScreenDevice = _isGamingOverlay ||
                ((_state is PanelState.Normal or PanelState.Expanded or PanelState.Fullscreen) && _dockEdge is null)
                ? _preferences.ScreenDevice
                : _screenDeviceName,
            WeatherLocation = _selectedWeatherLocation,
            OverlayLeft = _isGamingOverlay ? Left : _preferences.OverlayLeft,
            OverlayTop = _isGamingOverlay ? Top : _preferences.OverlayTop,
            OverlayScale = _overlayScale,
            OverlayScreenDevice = _overlayScreenDeviceName,
            KeepOnTop = _keepOnTop,
            HoverExpandEnabled = _hoverExpandEnabled,
            HoverCollapseDelayMs = (int)_hoverCollapseTimer.Interval.TotalMilliseconds,
            WeatherRefreshMinutes = (int)_weatherRefreshTimer.Interval.TotalMinutes,
            SystemRefreshSeconds = (int)_systemStatsTimer.Interval.TotalSeconds,
            ShowCapsuleCpu = _showCapsuleCpu,
            ShowCapsuleGpu = _showCapsuleGpu,
            ShowCapsuleRam = _showCapsuleRam,
            ShowCapsuleClock = _showCapsuleClock,
            ShowCapsuleWeather = _showCapsuleWeather,
            ShowCapsuleMedia = _showCapsuleMedia,
            ShowCapsuleArtwork = _showCapsuleArtwork,
            ShowCapsulePlaybackStatus = _showCapsulePlaybackStatus,
            ShowCapsuleNetwork = _showCapsuleNetwork,
            ShowCapsuleVpn = _showCapsuleVpn,
            CapsuleContentOrder = _capsuleContentOrder,
            ShowOverlayCpu = _showOverlayCpu,
            ShowOverlayGpu = _showOverlayGpu,
            ShowOverlayRam = _showOverlayRam,
            TimerCompletionSound = _timerCompletionSound,
            EnableMotion = _enableMotion,
            EnableAudioReactive = _enableAudioReactive,
            AudioReactiveIntensity = _audioReactiveIntensity,
            WidgetIconSize = _widgetIconSize,
            SelectedWidget = _widgetHost.SelectedWidget?.Id ?? _preferences.SelectedWidget,
            WorkspaceLeft = (_state is PanelState.Normal or PanelState.Expanded) && _dockEdge is null ? Left : _workspaceLeft ?? _preferences.WorkspaceLeft,
            WorkspaceTop = (_state is PanelState.Normal or PanelState.Expanded) && _dockEdge is null ? Top : _workspaceTop ?? _preferences.WorkspaceTop,
            WorkspaceWidth = _state == PanelState.Expanded && _dockEdge is null ? Width : _workspaceWidth,
            WorkspaceHeight = _state == PanelState.Expanded && _dockEdge is null ? Height : _workspaceHeight,
            WorkspaceScreenDevice = (_state is PanelState.Normal or PanelState.Expanded) && _dockEdge is null
                ? ScreenAtWindow().DeviceName
                : _workspaceScreenDeviceName ?? _preferences.WorkspaceScreenDevice,
            BrowserAdaptationEnabled = _browserAdaptationEnabled
        };
        _preferences.Save();
    }

    private sealed class TransferFileRow : INotifyPropertyChanged
    {
        private string _status = "Выбран";
        public TransferFileRow(string path) { Path = path; Name = System.IO.Path.GetFileName(path); }
        public string Path { get; }
        public string Name { get; }
        public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText))); } }
        public string DisplayText => $"{Name}  ·  {Status}";
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class IncomingOfferRow
    {
        private readonly Func<bool, bool> _respond;
        public IncomingOfferRow(TransferOffer offer, Func<bool, bool> respond, bool webRtc)
        {
            _respond = respond;
            var directVpn = offer.TransportPreference == TransferTransportPreference.DirectVpn;
            var route = webRtc ? $"  ·  {DescribeTransportPreference(offer.TransportPreference)}" : "";
            var transport = webRtc ? "WebRTC" : directVpn ? "Direct VPN" : "LAN";
            DisplayText = $"{transport} · {offer.Sender.DisplayName}  ·  {offer.FileName}  ·  {FormatBytes(offer.SizeBytes)}{route}";
        }
        public bool Respond(bool accept) => _respond(accept);
        public string DisplayText { get; }
    }

    private sealed class TransferActivityRow : INotifyPropertyChanged
    {
        private string _displayText;
        public bool IsActive { get; private set; }
        public TransferActivityRow(string id, string fileName) { TransferId = id; FileName = fileName; _displayText = $"{fileName}  ·  Подключение"; }
        public string TransferId { get; }
        public string FileName { get; }
        public string DisplayText { get => _displayText; private set { _displayText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText))); } }
        public void Update(LocalTransferProgress progress)
        {
            IsActive = progress.State is TransferState.Offered or TransferState.Accepted or TransferState.Transferring or TransferState.Paused;
            var percent = progress.TotalBytes == 0 ? 100 : (int)Math.Clamp(progress.BytesTransferred * 100d / progress.TotalBytes, 0, 100);
            var bytes = progress.State == TransferState.Transferring ? $" · {FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes)} · {percent}%" : "";
            DisplayText = $"{FileName}  ·  {ToRussian(progress.State)}{bytes}";
        }
        public void Update(WebRtcSessionProgress progress)
        {
            IsActive = progress.State is WebRtcTransferSessionState.PendingApproval or WebRtcTransferSessionState.Connecting or
                WebRtcTransferSessionState.Transferring or WebRtcTransferSessionState.Verifying;
            var percent = progress.TotalBytes == 0 ? 100 : (int)Math.Clamp(progress.BytesTransferred * 100d / progress.TotalBytes, 0, 100);
            var suffix = progress.State == WebRtcTransferSessionState.Transferring ? $" · {FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes)} · {percent}%" : "";
            if (progress.State == WebRtcTransferSessionState.Transferring && progress.BytesPerSecond > 0)
                suffix += $" · {FormatBytes((long)progress.BytesPerSecond)}/с";
            var integrity = progress.IntegrityVerified ? " · SHA-256 подтверждён" :
                progress.State == WebRtcTransferSessionState.Verifying ? " · проверка SHA-256" : "";
            var state = progress.State switch
            {
                WebRtcTransferSessionState.PendingApproval => "Ожидает согласия",
                WebRtcTransferSessionState.Connecting => "Подключение",
                WebRtcTransferSessionState.Transferring => "Передаётся",
                WebRtcTransferSessionState.Verifying => "Проверка",
                WebRtcTransferSessionState.Completed => "Завершено",
                WebRtcTransferSessionState.Rejected => "Отклонено",
                WebRtcTransferSessionState.Cancelled => "Отменено",
                WebRtcTransferSessionState.Failed => $"Ошибка: {progress.Error}",
                _ => progress.State.ToString()
            };
            var connection = progress.ConnectionPath switch
            {
                "direct" => "Прямой P2P",
                "TURN UDP" => "TURN UDP",
                "TURN TCP" => "TURN TCP",
                "TURN TLS" => "TURN TLS",
                "TURN relay" => "TURN relay (транспорт не сообщается WebRTC)",
                "TCP/TLS File Relay" => "TCP/TLS File Relay",
                "unknown" => "маршрут не определён",
                _ => "маршрут ожидается"
            };
            var peer = progress.PeerConnectionState switch
            {
                "connected" => "соединён",
                "connecting" => "соединяется",
                "disconnected" => "отключён",
                "failed" => "ошибка",
                "closed" => "закрыт",
                _ => progress.PeerConnectionState
            };
            var ice = progress.IceState switch
            {
                "checking" => "проверка",
                "connected" => "соединён",
                "completed" => "завершён",
                "failed" => "ошибка",
                "disconnected" => "отключён",
                "closed" => "закрыт",
                _ => progress.IceState
            };
            var channel = progress.DataChannelState switch
            {
                "open" => "открыт",
                "closed" => "закрыт",
                _ => "ожидает открытия"
            };
            var transport = progress.ConnectionPath == "TCP/TLS File Relay"
                ? "Защищённый WebSocket/TLS · отдельный файловый relay"
                : $"ICE {ice} · peer {peer} · канал {channel} · TURN {(progress.TurnConfigured ? "настроен" : "не настроен")} · ICE-серверов {progress.IceServerCount} · кандидаты {progress.LocalIceCandidates}/{progress.RemoteIceCandidates}";
            DisplayText = $"{FileName}  ·  {state}{suffix} · {connection}{integrity}\n{transport}";
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class TransferHistoryRow
    {
        public TransferHistoryRow(TransferHistoryEntry entry)
        {
            TransferId = entry.TransferId;
            Direction = entry.Direction;
            var verb = entry.Direction == TransferDirection.Incoming ? "Получен" : "Отправлен";
            var result = entry.State switch
            {
                TransferState.Completed => "Успешно",
                TransferState.Rejected => "Отклонён",
                TransferState.Cancelled => "Отменено",
                _ => "Ошибка"
            };
            var date = entry.CompletedAt.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("ru-RU"));
            DisplayText = $"{verb}: {entry.FileName}  ·  {entry.DeviceName}  ·  {FormatBytes(entry.SizeBytes)}  ·  {date}  ·  {result}";
        }
        public string TransferId { get; }
        public TransferDirection Direction { get; }
        public string DisplayText { get; }
    }
}
