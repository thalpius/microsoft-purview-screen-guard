using Microsoft.Win32;

namespace MicrosoftPurviewScreenGuard;

/// <summary>One pre-created overlay per monitor. Rebuilds them when the monitor layout changes.</summary>
internal sealed class OverlayManager : IDisposable
{
    private readonly Bitmap? _picture;
    private List<OverlayForm> _overlays;
    private string _signature;
    private volatile bool _layoutDirty;

    public OverlayManager()
    {
        // Load the picture once. If it is missing or unusable the overlays fall back to plain white with one line of
        // text: blocking never depends on the picture.
        string path = Path.Combine(AppContext.BaseDirectory, OverlayRenderer.PictureFileName);
        if (OverlayRenderer.TryLoadPicture(path, out Bitmap? picture, out string reason))
        {
            _picture = picture;
            Logger.Info($"overlay picture loaded: {picture!.Width}x{picture.Height} ({OverlayRenderer.PictureFileName})");
        }
        else
        {
            Logger.Warn(
                $"overlay picture NOT loaded: {reason}. Overlays will be plain white with the text " +
                $"\"{OverlayRenderer.FallbackText}\" (fail-closed)");
        }

        _signature = LayoutSignature();
        _overlays = CreateOverlays();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Logger.Info($"{_overlays.Count} overlay(s) created, hidden until needed");
        foreach (OverlayForm overlay in _overlays)
        {
            Logger.Info($"  {overlay.Description}: rendered once in {overlay.RenderMs:F0} ms ({overlay.RenderInfo})");
        }
    }

    public int VisibleCount => _overlays.Count(o => o.IsVisibleNow);

    /// <summary>
    /// Makes the overlays match the wanted state. While blocked, every call re-asserts topmost
    /// (and re-shows any overlay that is no longer visible).
    /// </summary>
    /// <param name="decidedAt">Stopwatch timestamp of the moment blocking was decided (for the paint timing log).</param>
    public void Apply(bool blocked, long decidedAt)
    {
        RebuildIfLayoutChanged(blocked, decidedAt);

        foreach (OverlayForm overlay in _overlays)
        {
            if (blocked)
            {
                if (overlay.IsVisibleNow)
                {
                    overlay.ReassertTopmost();
                }
                else
                {
                    Show(overlay, decidedAt);
                }
            }
            else if (overlay.IsVisibleNow)
            {
                overlay.HideOverlay();
            }
        }
    }

    private void RebuildIfLayoutChanged(bool blocked, long decidedAt)
    {
        string signature = LayoutSignature();
        if (!_layoutDirty && signature == _signature)
        {
            return;
        }

        _layoutDirty = false;
        _signature = signature;

        List<OverlayForm> fresh = CreateOverlays();
        Logger.Write(LogKind.Screen, $"monitor layout changed: {fresh.Count} monitor(s), overlays rebuilt");
        foreach (OverlayForm overlay in fresh)
        {
            Logger.Info($"  {overlay.Description}: rendered once in {overlay.RenderMs:F0} ms ({overlay.RenderInfo})");
        }

        // Cover the new layout first, then remove the old overlays, so there is no gap while blocked.
        if (blocked)
        {
            foreach (OverlayForm overlay in fresh)
            {
                Show(overlay, decidedAt);
            }
        }

        List<OverlayForm> old = _overlays;
        _overlays = fresh;
        foreach (OverlayForm overlay in old)
        {
            overlay.HideOverlay();
            overlay.Dispose();
        }
    }

    private static void Show(OverlayForm overlay, long decidedAt)
    {
        double setWindowPosMs = overlay.ShowTopmost(decidedAt);

        // Logging comes after the paint so console output never inflates the measurement.
        string painted = double.IsNaN(overlay.PaintedAfterMs)
            ? "NOT painted yet (waiting for the first paint)"
            : $"painted {overlay.PaintedAfterMs:F1} ms after the block decision";
        Logger.Write(
            LogKind.Screen,
            new Seg("overlay up  ", ConsoleColor.White),
            new Seg(
                $"{overlay.Description}  |  visible: {(overlay.IsVisibleNow ? "yes" : "NO")}  |  " +
                $"SetWindowPos {setWindowPosMs:F1} ms  |  {painted}  |  {overlay.RenderInfo}",
                ConsoleColor.Gray));
    }

    private List<OverlayForm> CreateOverlays()
    {
        var list = new List<OverlayForm>();
        foreach (Screen screen in Screen.AllScreens)
        {
            var overlay = new OverlayForm(screen, _picture);
            overlay.LatePaint += OnLatePaint;
            list.Add(overlay);
        }

        return list;
    }

    private static void OnLatePaint(OverlayForm overlay, double ms) =>
        Logger.Warn($"overlay {overlay.Description}: first paint came late, {ms:F1} ms after the block decision");

    private static string LayoutSignature() =>
        string.Join(";", Screen.AllScreens.Select(s => $"{s.DeviceName}:{s.Bounds}"));

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _layoutDirty = true;

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (OverlayForm overlay in _overlays)
        {
            overlay.HideOverlay();
            overlay.Dispose();
        }

        _overlays.Clear();
        _picture?.Dispose();
    }
}
