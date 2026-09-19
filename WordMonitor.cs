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
                    Logger.Log("Word not running");
                }

                _previous.Clear();
                SensitiveVisible = false;
                break;

            case WordStatus.Busy:
                // Keep the previous state; report only when we enter this state.
                if (_lastStatus != WordStatus.Busy)
                {
                    Logger.Log($"Word busy or unreadable ({snapshot.Error}); keeping previous state (sensitive={SensitiveVisible})");
                }

                break;

            case WordStatus.Ok:
                if (_lastStatus == WordStatus.NotRunning)
                {
                    Logger.Log("Word detected");
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
                Logger.Log("OPENED  " + Describe(window));
            }
            else if (old != window)
            {
                Logger.Log("CHANGED " + Describe(window));
            }
        }

        foreach (WordWindowState old in previous.Values)
        {
            if (!current.ContainsKey(old.Hwnd))
            {
                Logger.Log("CLOSED  " + Describe(old));
            }
        }

        return current;
    }

    private static string Describe(WordWindowState w)
    {
        string label = w.LabelUnavailable ? "(label API unavailable)" : w.LabelId ?? "(no label)";
        return $"\"{w.DocumentName}\"  label={label}  blocked={YesNo(w.Blocked)}  " +
               $"visible={YesNo(w.Visible)}  minimized={YesNo(w.Minimized)}  hwnd=0x{w.Hwnd:X}";
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
