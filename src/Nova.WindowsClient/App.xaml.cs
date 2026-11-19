using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Application = System.Windows.Application;
using CommunityToolkit.WinUI.Notifications;
using Nova.TransferEngine;
using Nova.WindowsClient.Services;

namespace Nova.WindowsClient;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _trayMenu;
    private ToolStripMenuItem? _overlayMoveItem;
    private ToolStripMenuItem? _overlayExitItem;
    private bool _ownsMutex;
    private bool _shutdownInProgress;
    private Icon? _novaIcon;
    private ShellWindow? _window;
    private LocalTransferService? _transferService;
    private DispatcherTimer? _trainReminderTimer;
    private NotificationCenterService? _notificationCenter;
    private ProductivityService? _productivityService;
    private WindowsToastNotificationService? _toastNotificationService;
    private string? _toastRegistrationError;
    private ProfileService? _profileService;
    private const string ActivationPipeName = "NOVA.TaskBar.Activation.6";
    private CancellationTokenSource? _activationListenerCancellation;
    private Task? _activationListenerTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "NOVA.TaskBar.SingleInstance", out var created);
        if (!created)
        {
            TryActivateRunningInstance();
            Shutdown();
            return;
        }
        _ownsMutex = true;
        base.OnStartup(e);

        try
        {
            WindowsToastRegistration.Initialize();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _toastRegistrationError = ex.Message;
            System.Diagnostics.Trace.TraceError("Could not register Windows Toast notifications: {0}", ex);
        }

        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NOVA", "TaskBar");
        _transferService = new LocalTransferService(stateDirectory);
        var schedule = LocalTrainScheduleService.LoadDefault();
        var reminderScheduler = new TrainReminderScheduler(
            new TrainReminderStore(stateDirectory),
            schedule.TimeZone ?? LocalTrainScheduleService.GetMoscowTimeZone());
        reminderScheduler.Initialize();
        _notificationCenter = new NotificationCenterService(new NotificationCenterStore(stateDirectory));
        _notificationCenter.Initialize();
        _productivityService = new ProductivityService(new ProductivityStore(stateDirectory));
        _productivityService.Initialize();
        _profileService = new ProfileService(new ProfileStore(stateDirectory));
        _profileService.Initialize();
        if (_notificationCenter.IsAvailable)
        {
            try
            {
                _notificationCenter.SyncTrainReminders(reminderScheduler.Reminders, reminderScheduler);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceError("Could not synchronize train reminders with the notification center: {0}", ex);
            }
        }
        reminderScheduler.Changed += (_, _) =>
        {
            if (_notificationCenter?.IsAvailable == true)
            {
                try
                {
                    _notificationCenter.SyncTrainReminders(reminderScheduler.Reminders, reminderScheduler);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _trayIcon?.ShowBalloonTip(5000, "Центр уведомлений", ex.Message, ToolTipIcon.Warning);
                }
            }
        };
        _toastNotificationService = new WindowsToastNotificationService();
        _window = new ShellWindow(_transferService, reminderScheduler, _notificationCenter, _productivityService, _profileService);
        InitializeTray();
        StartActivationListener();
        ToastNotificationManagerCompat.OnActivated += _ => Dispatcher.BeginInvoke(new Action(ShowPanel));
        if (_toastRegistrationError is not null)
            _trayIcon?.ShowBalloonTip(5000, "Windows Toast", _toastRegistrationError, ToolTipIcon.Warning);
        if (!_productivityService.IsAvailable)
            _trayIcon?.ShowBalloonTip(5000, "Productivity", _productivityService.LastError ?? "Локальное хранилище недоступно.", ToolTipIcon.Warning);
        _trainReminderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _trainReminderTimer.Tick += (_, _) =>
        {
            var center = _notificationCenter!;
            var profileNotificationsEnabled = _profileService?.ActiveProfile.Snapshot.NotificationsEnabled ?? true;
            var suppressProfileToasts = _profileService?.ActiveProfile.Snapshot.SuppressNotificationToasts ?? false;
            if (!profileNotificationsEnabled || suppressProfileToasts)
            {
                return;
            }
            var previousTrainError = reminderScheduler.LastError;
            reminderScheduler.ProcessDue(DateTimeOffset.UtcNow, _toastNotificationService!);
            if (reminderScheduler.LastError is { Length: > 0 } trainError &&
                !string.Equals(trainError, previousTrainError, StringComparison.Ordinal))
                _trayIcon?.ShowBalloonTip(5000, "Напоминание о поезде", trainError, ToolTipIcon.Warning);

            var previousCenterError = center.LastError;
            center.ProcessDue(DateTimeOffset.UtcNow, _toastNotificationService!.ShowNotification);
            if (center.LastError is { Length: > 0 } centerError &&
                !string.Equals(centerError, previousCenterError, StringComparison.Ordinal))
                _trayIcon?.ShowBalloonTip(5000, "Центр уведомлений", centerError, ToolTipIcon.Warning);

            var productivity = _productivityService;
            if (productivity is null)
                return;
            var previousProductivityError = productivity.LastError;
            productivity.ProcessDue(DateTimeOffset.UtcNow, DeliverProductivityNotification);
            if (productivity.LastError is { Length: > 0 } productivityError &&
                !string.Equals(productivityError, previousProductivityError, StringComparison.Ordinal))
                _trayIcon?.ShowBalloonTip(5000, "Productivity", productivityError, ToolTipIcon.Warning);
        };
        _trainReminderTimer.Start();
        _transferService.IncomingOffer += OnIncomingOffer;
        _transferService.WebRtcIncomingOffer += OnWebRtcIncomingOffer;
        MainWindow = _window;
        try { _transferService.Start(); }
        catch { /* The panel remains available and reports that network discovery is unavailable. */ }
        _window.Show();
    }

    private void InitializeTray()
    {
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Открыть NOVA", null, (_, _) => ShowPanel());
        _trayMenu.Items.Add("Скрыть панель", null, (_, _) => _window?.Hide());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _overlayMoveItem = new ToolStripMenuItem("Переместить игровой оверлей", null, (_, _) => _window?.BeginOverlayMove());
        _overlayExitItem = new ToolStripMenuItem("Выключить игровой оверлей", null, (_, _) => _window?.ExitGamingOverlay());
        _trayMenu.Items.Add(_overlayMoveItem);
        _trayMenu.Items.Add(_overlayExitItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Завершить NOVA", null, async (_, _) => await ExitApplicationAsync());
        _trayMenu.Opening += (_, _) =>
        {
            var overlayActive = _window?.IsGamingOverlayActive == true;
            _overlayMoveItem.Enabled = overlayActive;
            _overlayExitItem.Enabled = overlayActive;
        };
        _novaIcon = CreateNovaTrayIcon();
        _trayIcon = new NotifyIcon { Text = "Панель NOVA", Icon = _novaIcon, ContextMenuStrip = _trayMenu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => ShowPanel();
    }

    private void StartActivationListener()
    {
        _activationListenerCancellation?.Dispose();
        _activationListenerCancellation = new CancellationTokenSource();
        var cancellation = _activationListenerCancellation.Token;
        _activationListenerTask = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                try
                {
                    await server.WaitForConnectionAsync(cancellation);
                    var buffer = new byte[32];
                    var read = await server.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation);
                    var command = Encoding.UTF8.GetString(buffer, 0, read);
                    if (string.Equals(command, "SHOW", StringComparison.Ordinal))
                        _ = Dispatcher.BeginInvoke(new Action(ShowPanel));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation);
                }
            }
        }, cancellation);
    }

    private static void TryActivateRunningInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect(1000);
            var command = Encoding.UTF8.GetBytes("SHOW");
            client.Write(command, 0, command.Length);
            client.Flush();
        }
        catch (Exception ex) when (
            ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not activate the running NOVA instance: {0}", ex);
        }
    }

    private void OnIncomingOffer(IncomingOfferRequest request)
    {
        var offer = request.Offer;
        Dispatcher.BeginInvoke(new Action(() =>
            RecordAndNotify("Входящий файл",
                $"{offer.Sender.DisplayName} отправляет «{offer.FileName}» ({offer.SizeBytes:N0} байт). Откройте NOVA, чтобы принять или отклонить.")));
    }

    private void OnWebRtcIncomingOffer(WebRtcIncomingOfferRequest request)
    {
        var offer = request.Offer;
        Dispatcher.BeginInvoke(new Action(() =>
            RecordAndNotify("Входящий WebRTC файл",
                $"{offer.Sender.DisplayName} предлагает {offer.FileName} ({offer.SizeBytes:N0} байт). Откройте NOVA, чтобы принять или отклонить запрос.")));
    }

    private void RecordAndNotify(string title, string message, string? notificationTag = null)
    {
        try
        {
            _notificationCenter?.AddSystemEvent(title, message, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _trayIcon?.ShowBalloonTip(5000, title, $"Не удалось сохранить событие: {ex.Message}", ToolTipIcon.Warning);
        }

        try
        {
            _toastNotificationService?.ShowNotification(
                title,
                message,
                notificationTag ?? Guid.NewGuid().ToString("N"));
        }
        catch (ToastNotificationDeliveryException ex)
        {
            _trayIcon?.ShowBalloonTip(5000, title, $"Windows Toast недоступен: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void DeliverProductivityNotification(string title, string message, string notificationTag)
    {
        (_toastNotificationService ?? throw new InvalidOperationException("Windows Toast сервис не инициализирован."))
            .ShowNotification(title, message, notificationTag);
        try
        {
            _notificationCenter?.AddSystemEvent(title, message, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _trayIcon?.ShowBalloonTip(5000, title, $"Уведомление показано, но история не сохранена: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void ShowPanel()
    {
        if (_shutdownInProgress || _window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;
        _trainReminderTimer?.Stop();
        if (_transferService is not null)
        {
            await _transferService.DisposeAsync();
            _transferService = null;
        }
        if (_window is not null)
            await _window.AllowCloseAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trainReminderTimer?.Stop();
        _activationListenerCancellation?.Cancel();
        _activationListenerCancellation?.Dispose();
        _activationListenerCancellation = null;
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _novaIcon?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static Icon CreateNovaTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var shape = new GraphicsPath();
        const int inset = 2;
        const int diameter = 9;
        shape.AddArc(inset, inset, diameter, diameter, 180, 90);
        shape.AddArc(32 - inset - diameter, inset, diameter, diameter, 270, 90);
        shape.AddArc(32 - inset - diameter, 32 - inset - diameter, diameter, diameter, 0, 90);
        shape.AddArc(inset, 32 - inset - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        using var fill = new LinearGradientBrush(new Rectangle(2, 2, 28, 28), Color.FromArgb(255, 180, 155, 255), Color.FromArgb(255, 91, 197, 255), 45f);
        graphics.FillPath(fill, shape);
        using var fontFamily = new System.Drawing.FontFamily("Segoe UI");
        using var font = new System.Drawing.Font(fontFamily, 18f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("N", font, Brushes.MidnightBlue, new RectangleF(2, 2, 28, 28), format);
        var handle = bitmap.GetHicon();
        try
        {
            using var source = Icon.FromHandle(handle);
            return (Icon)source.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
