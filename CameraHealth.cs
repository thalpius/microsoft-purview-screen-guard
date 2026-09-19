namespace MicrosoftPurviewScreenGuard;

internal readonly record struct CameraSnapshot(
    bool Healthy,
    string Reason,
    double Brightness,
    double StdDev,
    long IntervalMs,
    double LastPhoneScore,
    long InferenceMs,
    string ModelStatus,
    bool PhoneSeen = false)
{
    public string Status =>
        $"brightness={Brightness:F0} std={StdDev:F1} interval={IntervalMs}ms " +
        $"lastScore={LastPhoneScore:F2} inference={InferenceMs}ms model={ModelStatus}";
}

/// <summary>
/// Pure camera + detector health rules, no OpenCV and no clock of its own (time is passed in), so it can be tested.
/// Not thread-safe: the caller locks.
/// </summary>
internal sealed class CameraHealth
{
    /// <summary>Grayscale std deviation below this = blind. Mean brightness is useless (auto-exposure).</summary>
    public const double BlindStdDevThreshold = 12.0;

    /// <summary>Consecutive non-blind frames needed before the camera counts as healthy (warm-up).</summary>
    public const int ReadyFrames = 15;

    /// <summary>Blindness shorter than this is tolerated.</summary>
    public const long BlindGraceMs = 300;

    /// <summary>No frame for this long = the stream is dead.</summary>
    public const long StaleFrameMs = 2000;

    /// <summary>The detector must have finished a frame this recently, otherwise it counts as stuck.</summary>
    public const long DetectorStaleMs = 3000;

    private enum ModelState
    {
        Loading,
        Loaded,
        NotLoaded,
    }

    private bool _opened;
    private bool _warmedUp;
    private int _consecutiveGood;
    private long _blindSince;      // tick count when blindness started, 0 when the image is OK
    private long _lastFrameTick;   // 0 = no frame yet
    private long _intervalMs;
    private double _brightness;
    private double _stdDev;

    private ModelState _model = ModelState.Loading;
    private string _modelReason = string.Empty;
    private bool _detectorFailed;
    private string _failReason = string.Empty;
    private long _lastDetectionTick; // 0 = no detection yet
    private double _lastScore;
    private long _inferenceMs;

    public bool WarmedUp => _warmedUp;

    public void OnOpened()
    {
        _opened = true;
        ResetWarmUp();
        _blindSince = 0;
        _lastFrameTick = 0;
        _intervalMs = 0;
    }

    public void OnClosed()
    {
        _opened = false;
        ResetWarmUp();
        _blindSince = 0;
    }

    /// <summary>Feeds one frame. Returns true when this frame completed the warm-up.</summary>
    public bool OnFrame(long now, double brightness, double stdDev)
    {
        // The stream had stopped for a while: start the warm-up over.
        if (_lastFrameTick != 0 && now - _lastFrameTick > StaleFrameMs)
        {
            ResetWarmUp();
        }

        _intervalMs = _lastFrameTick == 0 ? 0 : now - _lastFrameTick;
        _lastFrameTick = now;
        _brightness = brightness;
        _stdDev = stdDev;

        bool justWarmedUp = false;
        if (stdDev < BlindStdDevThreshold)
        {
            if (_blindSince == 0)
            {
                _blindSince = now;
            }

            _consecutiveGood = 0;
        }
        else
        {
            _blindSince = 0;
            if (_consecutiveGood < int.MaxValue)
            {
                _consecutiveGood++;
            }

            if (!_warmedUp && _consecutiveGood >= ReadyFrames)
            {
                _warmedUp = true;
                justWarmedUp = true;
            }
        }

        return justWarmedUp;
    }

    public void OnModelLoaded()
    {
        _model = ModelState.Loaded;
        _modelReason = string.Empty;
    }

    public void OnModelNotLoaded(string reason)
    {
        _model = ModelState.NotLoaded;
        _modelReason = reason;
    }

    /// <summary>Anything unexpected in the detector. Latched: fail-closed until the app restarts.</summary>
    public void OnDetectorFailed(string reason)
    {
        _detectorFailed = true;
        _failReason = reason;
    }

    /// <summary>The detector finished one frame.</summary>
    public void OnDetection(long now, double score, long inferenceMs)
    {
        _lastDetectionTick = now;
        _lastScore = score;
        _inferenceMs = inferenceMs;
    }

    /// <summary>Short status for the per-second line; the detail is in <see cref="CameraSnapshot.Reason"/>.</summary>
    public string ModelStatus =>
        _detectorFailed ? "FAILED"
        : _model == ModelState.Loaded ? "loaded"
        : _model == ModelState.NotLoaded ? "NOT loaded"
        : "loading";

    /// <summary>
    /// Healthy = camera opened AND warmed up AND last frame recent AND NOT blind for longer than the grace
    /// AND model loaded AND detector not failed AND the detector finished a frame recently.
    /// </summary>
    public CameraSnapshot Snapshot(long now)
    {
        bool hasFrame = _lastFrameTick != 0;
        bool recent = hasFrame && now - _lastFrameTick < StaleFrameMs;
        bool blindTooLong = _blindSince != 0 && now - _blindSince > BlindGraceMs;
        bool cameraOk = _opened && _warmedUp && recent && !blindTooLong;

        bool hasDetection = _lastDetectionTick != 0;
        bool detectorRecent = hasDetection && now - _lastDetectionTick < DetectorStaleMs;
        bool detectorOk = _model == ModelState.Loaded && !_detectorFailed && detectorRecent;

        bool healthy = cameraOk && detectorOk;

        string reason = healthy ? string.Empty
            : !_opened ? "camera not open"
            : !_warmedUp ? $"warming up {_consecutiveGood}/{ReadyFrames}"
            : !hasFrame ? "no frames yet"
            : !recent ? $"no frames for {now - _lastFrameTick} ms"
            : blindTooLong ? $"blind for {now - _blindSince} ms"
            : _detectorFailed ? $"detector failed: {_failReason}"
            : _model == ModelState.NotLoaded ? $"model NOT loaded: {_modelReason}"
            : _model == ModelState.Loading ? "model loading"
            : !hasDetection ? "no detection yet"
            : $"detector stuck: no frame for {now - _lastDetectionTick} ms";

        return new CameraSnapshot(healthy, reason, _brightness, _stdDev, _intervalMs, _lastScore, _inferenceMs, ModelStatus);
    }

    private void ResetWarmUp()
    {
        _warmedUp = false;
        _consecutiveGood = 0;
    }
}
