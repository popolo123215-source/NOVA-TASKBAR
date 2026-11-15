using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Nova.Core.Contracts;
using Nova.TransferEngine;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Forms = System.Windows.Forms;

namespace Nova.WindowsClient;

public partial class ShellWindow : Window
{
    private enum PanelState { Compact, Main, Expanded }
    private PanelState _state = PanelState.Main;
    private bool _allowClose;
    private bool _ready;
    private double _sizeScale = 1;
    private string? _dockEdge;
    private PanelPreferences _preferences = PanelPreferences.Defaults;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly LocalTransferService _transferService;
    private readonly ObservableCollection<DiscoveredDevice> _devices = [];
    private readonly ObservableCollection<TransferFileRow> _outgoingFiles = [];
    private readonly ObservableCollection<IncomingOfferRow> _incomingOffers = [];
    private readonly ObservableCollection<TransferActivityRow> _activities = [];
    private readonly ObservableCollection<TransferHistoryRow> _historyRows = [];
    private readonly Dictionary<string, TransferActivityRow> _activityById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferFileRow> _fileByTransferId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferFileRow> _webRtcFileByTransferId = new(StringComparer.Ordinal);

    public ShellWindow(LocalTransferService transferService)
    {
        InitializeComponent();
        _transferService = transferService;
        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer.Stop(); SavePreferences(); };
        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceRefreshTimer.Tick += (_, _) => RefreshDevices();

        DevicesList.ItemsSource = _devices;
        OutgoingFilesList.ItemsSource = _outgoingFiles;
        IncomingOffersList.ItemsSource = _incomingOffers;
        TransferProgressList.ItemsSource = _activities;
        HistoryList.ItemsSource = _historyRows;

        _transferService.IncomingOffer += OnIncomingOffer;
        _transferService.WebRtcIncomingOffer += OnWebRtcIncomingOffer;
        _transferService.ProgressChanged += OnTransferProgress;
        _transferService.WebRtcProgressChanged += OnWebRtcTransferProgress;
        _transferService.HistoryChanged += OnHistoryChanged;
        _transferService.InternetStatusChanged += OnInternetStatusChanged;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _preferences = PanelPreferences.Load();
        _sizeScale = Math.Clamp(_preferences.SizeScale, 0.8, 1.2);
        SizeSlider.Value = _sizeScale;
        OpacitySlider.Value = Math.Clamp(_preferences.Opacity, 0.72, 1);
        PanelSurface.Opacity = OpacitySlider.Value;
        LocalFingerprintText.Text = FormatFingerprint(_transferService.Identity.Fingerprint);
        LocalPairingCodeBox.Text = CreatePairingCode(_transferService.Identity.Device.DeviceId,
            _transferService.Identity.Device.DisplayName, _transferService.Identity.Fingerprint);
        DestinationPathText.Text = _transferService.Settings.DestinationDirectory;
        InternetEndpointBox.Text = _transferService.InternetSettings.Endpoint ?? "";
        OnInternetStatusChanged(_transferService.Internet.Status);
        RefreshDevices();
        RefreshHistory();
        _deviceRefreshTimer.Start();
        _ready = true;
        SetState(PanelState.Main);
        if (_preferences.DockEdge is "top" or "bottom" or "left" or "right")
            DockTo(_preferences.DockEdge);
        else
            RestoreFreePosition();
    }

    private void SetState(PanelState state)
    {
        _state = state;
        var next = state switch { PanelState.Compact => CompactView, PanelState.Main => MainView, _ => ExpandedView };
        var views = new[] { CompactView, MainView, ExpandedView };
        foreach (var view in views)
        {
            if (view == next) continue;
            view.BeginAnimation(OpacityProperty, null);
            view.Visibility = Visibility.Collapsed;
        }
        next.Visibility = Visibility.Visible;
        next.Opacity = 0;
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        ApplyDimensions();
    }

    private void ApplyDimensions()
    {
        var size = _state switch { PanelState.Compact => (W: 314d, H: 88d), PanelState.Main => (W: 386d, H: 316d), _ => (W: 430d, H: 430d) };
        var duration = TimeSpan.FromMilliseconds(_ready ? 190 : 0);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(WidthProperty, new DoubleAnimation(Width, size.W * _sizeScale, duration) { EasingFunction = easing });
        BeginAnimation(HeightProperty, new DoubleAnimation(Height, size.H * _sizeScale, duration) { EasingFunction = easing });
    }

    private void SetExpandedSection(string section)
    {
        SetState(PanelState.Expanded);
        OverviewContent.Visibility = section == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        TransferContent.Visibility = section == "Transfer" ? Visibility.Visible : Visibility.Collapsed;
        HistoryContent.Visibility = section == "History" ? Visibility.Visible : Visibility.Collapsed;
        ModuleContent.Visibility = section == "Remote" ? Visibility.Visible : Visibility.Collapsed;

        if (section == "Transfer")
        {
            TransferStatusText.Text = _transferService.IsStarted
                ? "Поиск устройств NOVA в локальной сети…"
                : "Сетевой обмен пока недоступен. Перезапустите приложение и попробуйте снова.";
            RefreshDevices();
        }
        else if (section == "History") RefreshHistory();
        else if (section == "Remote")
        {
            ModuleTitle.Text = "Удалённое управление";
            ModuleDescription.Text = "Защищённое управление рабочим столом с вашего разрешения появится в следующих версиях.";
            ModuleStatus.Text = "Удалённый доступ пока недоступен";
        }
    }

    private void Compact_Click(object sender, RoutedEventArgs e) => SetState(PanelState.Compact);
    private void CompactOpen_Click(object sender, RoutedEventArgs e) => SetState(PanelState.Main);
    private void Main_Click(object sender, RoutedEventArgs e) => SetState(PanelState.Main);
    private void Expand_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Overview");
    private void Overview_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Overview");
    private void Transfers_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Transfer");
    private void History_Click(object sender, RoutedEventArgs e) => SetExpandedSection("History");
    private void Remote_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Remote");
    private void Settings_Click(object sender, RoutedEventArgs e) => SetExpandedSection("Settings");
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void DevicesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }

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
            TransferStatusText.Text = $"Входящий файл от {offer.Sender.DisplayName}: {offer.FileName} ({FormatBytes(offer.SizeBytes)}).";
        });
    }

    private void OnWebRtcIncomingOffer(WebRtcIncomingOfferRequest request)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var offer = request.Offer;
            _incomingOffers.Add(new IncomingOfferRow(offer, request.Respond, true));
            TransferStatusText.Text = $"Входящий WebRTC файл от {offer.Sender.DisplayName}: {offer.FileName} ({FormatBytes(offer.SizeBytes)}).";
        });
    }

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
            if (progress.Error is not null) TransferStatusText.Text = $"WebRTC: {progress.Error}";
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
        if (_transferService.CancelWebRtcTransfer(row.TransferId))
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

    private static string FormatFingerprint(string fingerprint) =>
        string.Join("-", Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return; e.Cancel = true; Hide();
    }
    public void AllowClose() { _settingsSaveTimer.Stop(); SavePreferences(); _allowClose = true; }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            _dockEdge = null;
            DragMove();
            SavePreferences();
        }
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return; _sizeScale = e.NewValue; ApplyDimensions(); SchedulePreferenceSave();
    }
    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready) { PanelSurface.Opacity = e.NewValue; SchedulePreferenceSave(); }
    }
    private void DockTop_Click(object sender, RoutedEventArgs e) => DockTo("top");
    private void DockBottom_Click(object sender, RoutedEventArgs e) => DockTo("bottom");
    private void DockLeft_Click(object sender, RoutedEventArgs e) => DockTo("left");
    private void DockRight_Click(object sender, RoutedEventArgs e) => DockTo("right");

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_dockEdge is not null) PositionAtEdge(_dockEdge);
    }

    private sealed record PairingCodePayload(string DeviceId, string DisplayName, string Fingerprint);

    private void DockTo(string edge)
    {
        _dockEdge = edge;
        PositionAtEdge(edge);
        SavePreferences();
    }

    private void PositionAtEdge(string edge)
    {
        var work = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = edge switch { "left" => work.Left + 10, "right" => work.Right - width - 10, _ => work.Left + (work.Width - width) / 2 };
        Top = edge switch { "top" => work.Top + 10, "bottom" => work.Bottom - height - 10, _ => work.Top + 28 };
    }

    private void RestoreFreePosition()
    {
        var work = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = _preferences.Left < 0 ? work.Right - width - 10 : Math.Clamp(_preferences.Left, work.Left, work.Right - width);
        Top = _preferences.Top < 0 ? work.Top + 28 : Math.Clamp(_preferences.Top, work.Top, work.Bottom - height);
    }

    private void SchedulePreferenceSave() { _settingsSaveTimer.Stop(); _settingsSaveTimer.Start(); }

    private void SavePreferences()
    {
        if (!_ready) return;
        _preferences = new PanelPreferences(_dockEdge, Left, Top, _sizeScale, PanelSurface.Opacity);
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
            DisplayText = $"{(webRtc ? "WebRTC · " : "LAN · ")}{offer.Sender.DisplayName}  ·  {offer.FileName}  ·  {FormatBytes(offer.SizeBytes)}";
        }
        public bool Respond(bool accept) => _respond(accept);
        public string DisplayText { get; }
    }

    private sealed class TransferActivityRow : INotifyPropertyChanged
    {
        private string _displayText;
        public TransferActivityRow(string id, string fileName) { TransferId = id; FileName = fileName; _displayText = $"{fileName}  ·  Подключение"; }
        public string TransferId { get; }
        public string FileName { get; }
        public string DisplayText { get => _displayText; private set { _displayText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText))); } }
        public void Update(LocalTransferProgress progress)
        {
            var percent = progress.TotalBytes == 0 ? 100 : (int)Math.Clamp(progress.BytesTransferred * 100d / progress.TotalBytes, 0, 100);
            var bytes = progress.State == TransferState.Transferring ? $" · {FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes)} · {percent}%" : "";
            DisplayText = $"{FileName}  ·  {ToRussian(progress.State)}{bytes}";
        }
        public void Update(WebRtcSessionProgress progress)
        {
            var percent = progress.TotalBytes == 0 ? 100 : (int)Math.Clamp(progress.BytesTransferred * 100d / progress.TotalBytes, 0, 100);
            var suffix = progress.State == WebRtcTransferSessionState.Transferring ? $" · {FormatBytes(progress.BytesTransferred)} / {FormatBytes(progress.TotalBytes)} · {percent}%" : "";
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
            var connection = progress.ConnectionPath switch { "direct" => " · Прямое соединение", "TURN relay" => " · Через relay", _ => "" };
            DisplayText = $"{FileName}  ·  WebRTC {state}{suffix}{connection}";
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
            var result = entry.State switch { TransferState.Completed => "Успешно", TransferState.Rejected => "Отклонён", _ => "Ошибка" };
            var date = entry.CompletedAt.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("ru-RU"));
            DisplayText = $"{verb}: {entry.FileName}  ·  {entry.DeviceName}  ·  {FormatBytes(entry.SizeBytes)}  ·  {date}  ·  {result}";
        }
        public string TransferId { get; }
        public TransferDirection Direction { get; }
        public string DisplayText { get; }
    }
}
