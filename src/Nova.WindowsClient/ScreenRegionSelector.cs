using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Nova.WindowsClient;

internal sealed class ScreenRegionSelector : Form
{
    private readonly Rectangle _virtualBounds;
    private Point _selectionStart;
    private Point _selectionEnd;
    private bool _selecting;

    public ScreenRegionSelector()
    {
        _virtualBounds = SystemInformation.VirtualScreen;
        if (_virtualBounds.Width <= 0 || _virtualBounds.Height <= 0)
            throw new InvalidOperationException("Windows не вернула корректные границы виртуального экрана.");

        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        Bounds = _virtualBounds;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        Opacity = 0.44;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Выделите область экрана";
    }

    public Rectangle? SelectedRegion { get; private set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(255, 255, 188, 94), 2);
        using var fill = new SolidBrush(Color.FromArgb(56, 255, 188, 94));
        using var font = new Font(new FontFamily("Segoe UI"), 13, FontStyle.Bold);
        var screenRegion = CurrentScreenRegion();
        if (_selecting && screenRegion.Width > 0 && screenRegion.Height > 0)
        {
            var topLeft = PointToClient(screenRegion.Location);
            var bottomRight = PointToClient(new Point(screenRegion.Right, screenRegion.Bottom));
            var region = Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            e.Graphics.FillRectangle(fill, region);
            e.Graphics.DrawRectangle(pen, region);
        }

        e.Graphics.DrawString(
            "Перетащите рамку для распознавания · Esc — отмена",
            font,
            Brushes.White,
            new PointF(18, 18));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        _selecting = true;
        _selectionStart = GetCursorPosition();
        _selectionEnd = _selectionStart;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting) return;
        _selectionEnd = GetCursorPosition();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_selecting || e.Button != MouseButtons.Left) return;

        _selectionEnd = GetCursorPosition();
        _selecting = false;
        Capture = false;
        var region = CurrentScreenRegion();
        if (region.Width < 4 || region.Height < 4)
        {
            SelectedRegion = null;
            DialogResult = DialogResult.Cancel;
        }
        else
        {
            SelectedRegion = region;
            DialogResult = DialogResult.OK;
        }
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape) return;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private Rectangle CurrentScreenRegion() => Rectangle.FromLTRB(
        Math.Min(_selectionStart.X, _selectionEnd.X),
        Math.Min(_selectionStart.Y, _selectionEnd.Y),
        Math.Max(_selectionStart.X, _selectionEnd.X),
        Math.Max(_selectionStart.Y, _selectionEnd.Y));

    private static Point GetCursorPosition()
    {
        if (!GetCursorPos(out var position))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить координаты указателя мыши.");
        return new Point(position.X, position.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);
}
