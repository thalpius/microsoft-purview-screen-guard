namespace MicrosoftPurviewScreenGuard;

internal static class BlockPolicy
{
    /// <summary>
    /// True when ANY Word window (not just the foreground one) is visible, not minimized,
    /// and shows a document whose label GUID is in the blocked list.
    /// </summary>
    public static bool IsSensitiveVisible(IEnumerable<WordWindowState> windows) =>
        windows.Any(w => w.Blocked && w.Visible && !w.Minimized);

    /// <summary>
    /// Block = sensitive content visible AND (a phone is seen, currently the manual override, OR the camera is not healthy).
    /// </summary>
    public static bool ShouldBlock(bool sensitiveVisible, bool phoneSeen, bool cameraHealthy) =>
        sensitiveVisible && (phoneSeen || !cameraHealthy);
}
