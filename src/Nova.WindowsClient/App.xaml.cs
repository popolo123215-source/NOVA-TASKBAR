using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "NOVA.TaskBar.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }
        _ownsMutex = true;
        base.OnStartup(e);

        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NOVA", "TaskBar");
        _transferService = new LocalTransferService(stateDirectory);
        var schedule = LocalTrainScheduleService.LoadDefault();
        var reminderScheduler = new TrainReminderScheduler(
            new TrainReminderStore(stateDirectory),
            schedule.TimeZone ?? LocalTrainScheduleService.GetMoscowTimeZone());
        reminderScheduler.Initialize();
        var reminderNotifier = new WindowsToastNotificationService();
        _window = new ShellWindow(_transferService, reminderScheduler);
        InitializeTray();
        ToastNotificationManagerCompat.OnActivated += _ => Dispatcher.BeginInvoke(new Action(ShowPanel));
        _trainReminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _trainReminderTimer.Tick += (_, _) =>
            reminderScheduler.ProcessDue(DateTimeOffset.UtcNow, reminderNotifier);
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

    private void OnIncomingOffer(IncomingOfferRequest request)
    {
        var offer = request.Offer;
        Dispatcher.BeginInvoke(new Action(() =>
            _trayIcon?.ShowBalloonTip(5000, "Входящий файл",
                $"{offer.Sender.DisplayName} отправляет «{offer.FileName}» ({offer.SizeBytes:N0} байт). Откройте NOVA, чтобы принять или отклонить.",
                ToolTipIcon.Info)));
    }

    private void OnWebRtcIncomingOffer(WebRtcIncomingOfferRequest request)
    {
        var offer = request.Offer;
        Dispatcher.BeginInvoke(new Action(() =>
            _trayIcon?.ShowBalloonTip(5000, "Входящий WebRTC файл",
                $"{offer.Sender.DisplayName} предлагает {offer.FileName} ({offer.SizeBytes:N0} байт). Откройте NOVA, чтобы принять или отклонить запрос.",
                ToolTipIcon.Info)));
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
