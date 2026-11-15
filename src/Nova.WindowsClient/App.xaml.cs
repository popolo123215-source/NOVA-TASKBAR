using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Application = System.Windows.Application;
using Nova.TransferEngine;

namespace Nova.WindowsClient;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private NotifyIcon? _trayIcon;
    private bool _ownsMutex;
    private Icon? _novaIcon;
    private ShellWindow? _window;
    private LocalTransferService? _transferService;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "NOVA.TaskBar.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }
        _ownsMutex = true;
        base.OnStartup(e);

        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NOVA", "TaskBar");
        _transferService = new LocalTransferService(stateDirectory);
        _window = new ShellWindow(_transferService);
        InitializeTray();
        _transferService.IncomingOffer += OnIncomingOffer;
        _transferService.WebRtcIncomingOffer += OnWebRtcIncomingOffer;
        MainWindow = _window;
        try { _transferService.Start(); }
        catch { /* The panel remains available and reports that network discovery is unavailable. */ }
        _window.Show();
    }

    private void InitializeTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть NOVA", null, (_, _) => ShowPanel());
        menu.Items.Add("Скрыть панель", null, (_, _) => _window?.Hide());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Завершить NOVA", null, (_, _) => ExitApplication());
        _novaIcon = CreateNovaTrayIcon();
        _trayIcon = new NotifyIcon { Text = "Панель NOVA", Icon = _novaIcon, ContextMenuStrip = menu, Visible = true };
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
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApplication()
    {
        _trayIcon?.Dispose(); _trayIcon = null;
        _novaIcon?.Dispose(); _novaIcon = null;
        _window?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _novaIcon?.Dispose();
        if (_transferService is not null)
        {
            try { _transferService.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { }
        }
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
