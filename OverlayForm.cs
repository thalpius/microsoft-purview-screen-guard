using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// Borderless white form covering one monitor. Its picture is rendered ONCE into a bitmap when the form is created;
/// painting only copies that bitmap. Created hidden up front and shown/hidden with native calls so it never takes focus.
/// Never shows document or label names.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_DPICHANGED = 0x02E0;

    private readonly Rectangle _target;
    private readonly Bitmap? _rendered;

    private long _decidedAt;
    private bool _awaitingPaint;
    private bool _inShow;
    private double _paintedAfterMs = double.NaN;

    public OverlayForm(Screen screen, Bitmap? picture)
    {
        _target = screen.Bounds;
        Description = $"{screen.DeviceName} {_target.Width}x{_target.Height} at ({_target.X},{_target.Y})";

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        Text = "Screen Guard overlay";
        Bounds = _target;

        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);

        // Render once. If even that fails the overlay stays plain white (BackColor / OnPaint fill), it is never skipped.
        var watch = Stopwatch.StartNew();
        try
        {
            _rendered = OverlayRenderer.Render(_target.Size, picture, out string how);
            RenderInfo = how;
        }
        catch (Exception ex)
        {
            RenderInfo = $"plain white (render failed: {ex.GetType().Name})";
        }

        RenderMs = watch.Elapsed.TotalMilliseconds;

        // Create the native window now (still hidden) so showing it later is a single SetWindowPos call.
        _ = Handle;
    }

    public string Description { get; }

    /// <summary>What the overlay shows: "picture", "plain white + text", ...</summary>
    public string RenderInfo { get; } = string.Empty;

    public double RenderMs { get; }

    /// <summary>Milliseconds from the block decision to the first paint after the last show; NaN if not painted yet.</summary>
    public double PaintedAfterMs => _paintedAfterMs;

    public bool IsVisibleNow => IsHandleCreated && Native.IsWindowVisible(Handle);

    /// <summary>Raised when the first paint after a show happened later than the show call itself (normally never).</summary>
    public event Action<OverlayForm, double>? LatePaint;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>
    /// Shows the overlay topmost without activating it, then paints it immediately (Invalidate + Update) so it does not
    /// sit there unpainted. The monitor rectangle is applied here (physical pixels) so a monitor with a different DPI than
    /// the primary cannot leave the overlay mis-sized. Returns the time SetWindowPos took in ms.
    /// </summary>
    /// <param name="decidedAt">Stopwatch timestamp of the moment blocking was decided.</param>
    public double ShowTopmost(long decidedAt)
    {
        _decidedAt = decidedAt;
        _paintedAfterMs = double.NaN;
        _awaitingPaint = true;
        _inShow = true;
        try
        {
            long start = Stopwatch.GetTimestamp();
            Native.SetWindowPos(
                Handle,
                Native.HWND_TOPMOST,
                _target.X,
                _target.Y,
                _target.Width,
                _target.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            double setWindowPosMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Invalidate();
            Update();
            return setWindowPosMs;
        }
        finally
        {
            _inShow = false;
        }
    }

    /// <summary>Re-asserts topmost Z-order only: no move, no resize, no activation, no repaint.</summary>
    public void ReassertTopmost() =>
        Native.SetWindowPos(
            Handle,
            Native.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    public void HideOverlay()
    {
        if (IsHandleCreated)
        {
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is drawn in OnPaint; an erase pass would only flash the background first.
    }

    /// <summary>Copies the pre-rendered bitmap, pixel for pixel. No scaling, no blending, no text, no per-paint work.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;

        if (_rendered is null)
        {
            g.Clear(Color.White);
        }
        else
        {
            if (ClientSize != _rendered.Size)
            {
                g.Clear(Color.White);
            }

            // Explicit source and destination rectangles: DrawImageUnscaled would scale by the bitmap/screen DPI ratio.
            var whole = new Rectangle(0, 0, _rendered.Width, _rendered.Height);
            g.DrawImage(_rendered, whole, whole, GraphicsUnit.Pixel);
        }

        if (_awaitingPaint)
        {
            _awaitingPaint = false;
            _paintedAfterMs = Stopwatch.GetElapsedTime(_decidedAt).TotalMilliseconds;

            // The synchronous paint from Update() inside ShowTopmost is the normal case and is reported by the caller.
            if (!_inShow)
            {
                LatePaint?.Invoke(this, _paintedAfterMs);
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        // Never let WinForms rescale the overlay when it lands on a monitor with another DPI.
        if (m.Msg == WM_DPICHANGED)
        {
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rendered?.Dispose();
        }

        base.Dispose(disposing);
    }
}
