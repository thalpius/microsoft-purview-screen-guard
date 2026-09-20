namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// Decides which camera frames are handed to the phone detector. While a sensitive document is visible every frame is
/// offered (the detector then runs as fast as it can). Otherwise only about one frame per <see cref="PhoneSettings.IdleIntervalMs"/>
/// is, which keeps the detector's "finished a frame recently" health check satisfied at a fraction of the CPU cost.
/// Time is passed in; used by the camera reader thread only, so it is not thread-safe.
/// </summary>
internal sealed class DetectionPacer
{
    private long _lastSubmitTick; // 0 = nothing submitted yet

    public bool ShouldSubmit(bool detectionActive, long now)
    {
        if (detectionActive || _lastSubmitTick == 0 || now - _lastSubmitTick >= PhoneSettings.IdleIntervalMs)
        {
            _lastSubmitTick = now;
            return true;
        }

        return false;
    }
}
