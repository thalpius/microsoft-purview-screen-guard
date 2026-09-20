namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// Polls Word and tracks whether a blocked-label document is visible.
/// When Word cannot be read (busy, dialog open), the previous state is kept.
/// </summary>
internal sealed class WordMonitor
{
    private readonly WordLabelReader _reader;
    private Dictionary<long, WordWindowState> _previous = new();
    private WordStatus? _lastStatus;

    public WordMonitor(WordLabelReader reader) => _reader = reader;

    public bool SensitiveVisible { get; private set; }

    public void Poll()
    {
        WordSnapshot snapshot = _reader.Read();

        switch (snapshot.Status)
        {
            case WordStatus.NotRunning:
                if (_lastStatus != WordStatus.NotRunning)
                {
                    Logger.Write(LogKind.Word, "Word is not running");
                }

                _previous.Clear();
                SensitiveVisible = false;
                break;

            case WordStatus.Busy:
                // Keep the previous state; report only when we enter this state.
                if (_lastStatus != WordStatus.Busy)
                {
                    Logger.Warn(
                        $"Word is busy or unreadable ({snapshot.Error}); keeping the previous state " +
                        $"(sensitive document visible: {YesNo(SensitiveVisible)})");
                }

                break;

            case WordStatus.Ok:
                if (_lastStatus == WordStatus.NotRunning)
                {
                    Logger.Write(LogKind.Word, "Word detected");
                }

                _previous = ReportChanges(_previous, snapshot.Windows);
                SensitiveVisible = BlockPolicy.IsSensitiveVisible(snapshot.Windows);
                break;
        }

        _lastStatus = snapshot.Status;
    }

    private static Dictionary<long, WordWindowState> ReportChanges(
        Dictionary<long, WordWindowState> previous,
        IReadOnlyList<WordWindowState> windows)
    {
        var current = windows.ToDictionary(w => w.Hwnd);

        foreach (WordWindowState window in windows)
        {
            if (!previous.TryGetValue(window.Hwnd, out WordWindowState? old))
            {
                Logger.Write(LogKind.Word, Describe("opened", window));
            }
            else if (old != window)
            {
                Logger.Write(LogKind.Word, Describe("changed", window));
            }
        }

        foreach (WordWindowState old in previous.Values)
        {
            if (!current.ContainsKey(old.Hwnd))
            {
                Logger.Write(LogKind.Word, Describe("closed", old));
            }
        }

        return current;
    }

    /// <summary>
    /// One readable line per Word window: what happened, the document, its label GUID (the value to put in
    /// blocked-labels.txt), whether that label is in the block list, and whether the window counts as visible.
    /// </summary>
    private static Seg[] Describe(string what, WordWindowState w)
    {
        string label = w.LabelUnavailable ? "label API unavailable" : w.LabelId is null ? "no label" : $"label {w.LabelId}";
        Seg protection = w.Blocked
            ? new Seg("IN BLOCK LIST", ConsoleColor.Red)
            : new Seg("not in block list", ConsoleColor.DarkGray);
        Seg visibility = !w.Visible ? new Seg("hidden", ConsoleColor.DarkGray)
            : w.Minimized ? new Seg("minimized", ConsoleColor.DarkGray)
            : new Seg("visible", ConsoleColor.Green);

        return new[]
        {
            new Seg(what.PadRight(8), ConsoleColor.Gray),
            new Seg($"\"{w.DocumentName}\"", ConsoleColor.White),
            new Seg("   " + label + "   ", ConsoleColor.Gray),
            protection,
            new Seg("   ", null),
            visibility,
        };
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
