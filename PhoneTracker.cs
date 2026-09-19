namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// Turns per-frame phone scores into a stable "phone seen" state. Pure logic, time is passed in, not thread-safe (caller locks).
/// Confirmed when one frame is at or above FastScore, or at least 2 of the last 3 frames are at or above MinScore.
/// After a confirmation the phone stays seen for HoldMs, which also smooths flickering scores.
/// </summary>
internal sealed class PhoneTracker
{
    private const int Window = 3;
    private const int RequiredInWindow = 2;

    private readonly double[] _recent = new double[Window];
    private int _count;
    private int _next;
    private long _lastConfirmTick;

    /// <summary>Capture tick of the frame that started the current "seen" episode (for the reaction-time log).</summary>
    public long EpisodeCaptureTick { get; private set; }

    /// <summary>Feeds one processed frame. Returns true if this frame confirmed the phone.</summary>
    /// <param name="now">Tick when the detection finished.</param>
    /// <param name="captureTick">Tick when the frame was captured.</param>
    public bool OnFrame(long now, long captureTick, double score)
    {
        _recent[_next] = score;
        _next = (_next + 1) % Window;
        if (_count < Window)
        {
            _count++;
        }

        bool confirmed = score >= PhoneSettings.FastScore || CountAtLeastMin() >= RequiredInWindow;
        if (!confirmed)
        {
            return false;
        }

        if (!IsSeen(now))
        {
            EpisodeCaptureTick = captureTick;
        }

        _lastConfirmTick = now;
        return true;
    }

    public bool IsSeen(long now) => _lastConfirmTick != 0 && now - _lastConfirmTick < PhoneSettings.HoldMs;

    /// <summary>Forget the recent frames (the camera stream restarted). The hold time is kept.</summary>
    public void ResetRecent()
    {
        _count = 0;
        _next = 0;
    }

    private int CountAtLeastMin()
    {
        int hits = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_recent[i] >= PhoneSettings.MinScore)
            {
                hits++;
            }
        }

        return hits;
    }
}
