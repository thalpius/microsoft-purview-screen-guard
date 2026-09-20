using System.Diagnostics;
using OpenCvSharp;

namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// Two background threads. The reader opens camera 0 (DSHOW), reads frames and does the blind check at camera speed.
/// The detector (BelowNormal) runs YOLO on the LATEST frame only: the reader hands over a copy of the newest frame and
/// never waits for the detector, which drops older frames when it falls behind.
/// If the camera cannot be opened, is unplugged or throws, or the detector is missing, failed or stuck, it is unhealthy.
/// </summary>
internal sealed class CameraMonitor : IDisposable
{
    private const int CameraIndex = 0;
    private const int RetryDelayMs = 1000;

    /// <summary>Stream that yields no frame for this long is closed and reopened (warm-up restarts).</summary>
    private const int ReadFailReopenMs = 1000;

    /// <summary>The detector wakes up this often even without a new frame, to notice a hold that ran out.</summary>
    private const int DetectorIdleWaitMs = 250;

    /// <summary>
    /// Forcing MJPG / resolution / fps can cost several seconds at startup on some cameras,
    /// so it is off unless a camera really needs it.
    /// </summary>
    private const bool UseFixedMode = false;
    private const int FixedWidth = 1280;
    private const int FixedHeight = 720;
    private const int FixedFps = 30;

    private readonly object _gate = new();
    private readonly CameraHealth _health = new();
    private readonly PhoneTracker _phone = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _reader;
    private readonly Thread _detector;

    // Hand-over of the newest frame from the reader to the detector.
    private readonly object _frameGate = new();
    private readonly AutoResetEvent _frameReady = new(false);

    // Created lazily on the reader thread: allocating a Mat loads the OpenCV native library, and that must not
    // happen on the UI thread before the first Word poll.
    private Mat? _pending;
    private Mat? _working;
    private bool _hasPending;
    private long _pendingCaptureTick;
    private DateTime _pendingCapturedAt;
    private volatile bool _detectorAccepting;

    // Full-speed detection only while a sensitive document is visible; starts true so start-up is fail-closed.
    private readonly DetectionPacer _pacer = new();
    private volatile bool _detectionActive = true;

    // One phone sighting (from the first confirmation until the hold runs out), for the summary line. Guarded by _gate.
    private bool _episodeActive;
    private long _episodeStartTick;
    private long _episodeLastConfirmTick;
    private double _episodePeak;
    private int _episodeCandidateFrames;

    private bool _lastRaisedHealthy;
    private bool _lastRaisedPhone;
    private string? _lastProblem;
    private int _disposed;

    public CameraMonitor()
    {
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "CameraReader" };
        _detector = new Thread(DetectorLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "PhoneDetector",
        };
    }

    /// <summary>Raised on the reader OR detector thread when Healthy or PhoneSeen changes. Marshal to the UI thread.</summary>
    public event EventHandler? Changed;

    public bool Healthy => GetSnapshot().Healthy;

    public bool PhoneSeen => GetSnapshot().PhoneSeen;

    /// <summary>Capture tick of the frame that started the current "phone seen" episode.</summary>
    public long PhoneEpisodeCaptureTick
    {
        get
        {
            lock (_gate)
            {
                return _phone.EpisodeCaptureTick;
            }
        }
    }

    /// <summary>Short status: brightness, std deviation, frame interval, last phone score, inference time, model.</summary>
    public string Status => GetSnapshot().Status;

    public CameraSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            return _health.Snapshot(now) with { PhoneSeen = _phone.IsSeen(now) };
        }
    }

    /// <summary>True while the detector gets every frame (a sensitive document is visible); false = about 1 frame per second.</summary>
    public bool DetectionActive => _detectionActive;

    /// <summary>Called from the UI thread after every Word poll. Only a change is logged.</summary>
    public void SetDetectionActive(bool active)
    {
        if (_detectionActive == active)
        {
            return;
        }

        _detectionActive = active;
        Logger.Write(
            LogKind.Phone,
            active
                ? "detector ACTIVE: sensitive document visible, every frame is analyzed"
                : $"detector IDLE: no sensitive document visible, one frame per {PhoneSettings.IdleIntervalMs / 1000.0:0.#} s (saves CPU)");
    }

    public void Start()
    {
        _reader.Start();
        _detector.Start();
    }

    // ---------------------------------------------------------------- reader thread

    private void ReaderLoop()
    {
        while (!_stop.IsSet)
        {
            try
            {
                using VideoCapture? cap = TryOpen(out Stopwatch openWatch);
                if (cap is not null)
                {
                    ReadFrames(cap, openWatch);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ReportProblem($"Camera error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _health.OnClosed();
                    _phone.ResetRecent();
                }

                RaiseIfChanged();
            }

            _stop.Wait(RetryDelayMs);
        }
    }

    private VideoCapture? TryOpen(out Stopwatch watch)
    {
        watch = Stopwatch.StartNew();
        var cap = new VideoCapture(CameraIndex, VideoCaptureAPIs.DSHOW);
        if (!cap.IsOpened())
        {
            cap.Dispose();
            ReportProblem($"Camera {CameraIndex} could not be opened (retrying every {RetryDelayMs / 1000} s)");
            return null;
        }

        ApplyFixedMode(cap);

        lock (_gate)
        {
            _health.OnOpened();
        }

        _lastProblem = null;
        Logger.Write(LogKind.Camera, $"opened after {watch.ElapsedMilliseconds} ms (app uptime {Logger.Uptime.Elapsed.TotalSeconds:0.0} s)");
        return cap;
    }

    // UseFixedMode is a compile-time constant; the branch is intentionally dead by default.
#pragma warning disable CS0162
    private static void ApplyFixedMode(VideoCapture cap)
    {
        if (UseFixedMode)
        {
            cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
            cap.Set(VideoCaptureProperties.FrameWidth, FixedWidth);
            cap.Set(VideoCaptureProperties.FrameHeight, FixedHeight);
            cap.Set(VideoCaptureProperties.Fps, FixedFps);
        }
    }
#pragma warning restore CS0162

    private void ReadFrames(VideoCapture cap, Stopwatch openWatch)
    {
        using var frame = new Mat();
        using var converted = new Mat();
        long firstFailTick = 0;
        bool firstFrameLogged = false;

        while (!_stop.IsSet)
        {
            bool ok = cap.Read(frame) && !frame.Empty();
            long now = Environment.TickCount64;

            if (!ok)
            {
                if (firstFailTick == 0)
                {
                    firstFailTick = now;
                }

                if (now - firstFailTick >= ReadFailReopenMs)
                {
                    Logger.Warn("camera stream stopped; closing and reopening the camera");
                    return;
                }

                RaiseIfChanged();
                Thread.Sleep(20);
                continue;
            }

            firstFailTick = 0;

            // Grayscale std deviation: a covered or dead lens is flat, whatever auto-exposure does to brightness.
            Mat gray = frame;
            if (frame.Channels() != 1)
            {
                Cv2.CvtColor(frame, converted, ColorConversionCodes.BGR2GRAY);
                gray = converted;
            }

            Cv2.MeanStdDev(gray, out Scalar mean, out Scalar stdDev);

            if (!firstFrameLogged)
            {
                firstFrameLogged = true;
                Logger.Write(LogKind.Camera, $"first frame after {openWatch.ElapsedMilliseconds} ms (the first frames are usually black)");
            }

            bool justWarmedUp;
            lock (_gate)
            {
                justWarmedUp = _health.OnFrame(now, mean.Val0, stdDev.Val0);
            }

            if (justWarmedUp)
            {
                Logger.Write(
                    LogKind.Camera,
                    $"warm-up complete after {openWatch.ElapsedMilliseconds} ms ({CameraHealth.ReadyFrames} good frames in a row)");
            }

            // Blind frames are not worth an inference (hand over the lens); the blind check above already handles them.
            if (stdDev.Val0 >= CameraHealth.BlindStdDevThreshold && _detectorAccepting && _pacer.ShouldSubmit(_detectionActive, now))
            {
                SubmitFrame(frame, now);
            }

            RaiseIfChanged();
        }
    }

    /// <summary>Stores a copy of the newest frame for the detector. Only ever waits for the detector's tiny swap.</summary>
    private void SubmitFrame(Mat frame, long captureTick)
    {
        lock (_frameGate)
        {
            _pending ??= new Mat();
            _working ??= new Mat();
            frame.CopyTo(_pending);
            _pendingCaptureTick = captureTick;
            _pendingCapturedAt = DateTime.Now;
            _hasPending = true;
        }

        _frameReady.Set();
    }

    // ---------------------------------------------------------------- detector thread

    private void DetectorLoop()
    {
        PhoneDetector? detector = null;
        try
        {
            detector = LoadDetector();
            if (detector is null)
            {
                return;
            }

            _detectorAccepting = true;
            while (!_stop.IsSet)
            {
                if (!_frameReady.WaitOne(DetectorIdleWaitMs))
                {
                    RaiseIfChanged();
                    continue;
                }

                long captureTick;
                DateTime capturedAt;
                lock (_frameGate)
                {
                    if (!_hasPending)
                    {
                        continue;
                    }

                    // The reader keeps writing into the other buffer; older frames are simply overwritten.
                    (_pending, _working) = (_working, _pending);
                    captureTick = _pendingCaptureTick;
                    capturedAt = _pendingCapturedAt;
                    _hasPending = false;
                }

                var watch = Stopwatch.StartNew();
                double score = detector.Detect(_working!);
                long inferenceMs = watch.ElapsedMilliseconds;
                long done = Environment.TickCount64;

                bool confirmed;
                lock (_gate)
                {
                    _health.OnDetection(done, score, inferenceMs);
                    confirmed = _phone.OnFrame(done, captureTick, score);
                    TrackEpisode(done, score, confirmed);
                }

                if (score >= PhoneSettings.HitLogMinScore)
                {
                    (string verdict, ConsoleColor color) = DescribeHit(score, confirmed);
                    Logger.Hit(capturedAt, score, verdict, color);
                }

                RaiseIfChanged();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Anything unexpected: fail closed. The camera counts as unhealthy from now on.
            string reason = $"{ex.GetType().Name}: {ex.Message}";
            lock (_gate)
            {
                _health.OnDetectorFailed(reason);
            }

            Logger.Error($"phone detector FAILED: {reason}. The camera counts as unhealthy and screens stay covered (fail-closed)");
            RaiseIfChanged();
        }
        finally
        {
            _detectorAccepting = false;
            detector?.Dispose();
        }
    }

    private PhoneDetector? LoadDetector()
    {
        string path = Path.Combine(AppContext.BaseDirectory, PhoneSettings.ModelFileName);
        PhoneDetector? detector = null;
        try
        {
            detector = PhoneDetector.Load(path);
            long warmUpMs = detector.WarmUp();

            lock (_gate)
            {
                _health.OnModelLoaded();
            }

            Logger.Write(
                LogKind.Phone,
                $"detector ready: {PhoneSettings.ModelFileName} (model load {detector.LoadMs} ms, warm-up inference {warmUpMs} ms)");
            RaiseIfChanged();
            return detector;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            detector?.Dispose();
            string reason = ex is FileNotFoundException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";

            lock (_gate)
            {
                _health.OnModelNotLoaded(reason);
            }

            Logger.Error($"model NOT loaded: {reason}. The camera counts as unhealthy and screens stay covered (fail-closed)");
            RaiseIfChanged();
            return null;
        }
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// Raises <see cref="Changed"/> only when Healthy or PhoneSeen actually flipped, so the UI is not flooded.
    /// Called from both threads; logs the phone state change once.
    /// </summary>
    private void RaiseIfChanged()
    {
        bool phoneChanged;
        bool phoneSeen;
        double score;
        string? summary = null;
        lock (_gate)
        {
            long now = Environment.TickCount64;
            CameraSnapshot snapshot = _health.Snapshot(now);
            phoneSeen = _phone.IsSeen(now);

            if (snapshot.Healthy == _lastRaisedHealthy && phoneSeen == _lastRaisedPhone)
            {
                return;
            }

            phoneChanged = phoneSeen != _lastRaisedPhone;
            score = snapshot.LastPhoneScore;
            _lastRaisedHealthy = snapshot.Healthy;
            _lastRaisedPhone = phoneSeen;

            if (phoneChanged && !phoneSeen && _episodeActive)
            {
                double inViewSeconds = (_episodeLastConfirmTick - _episodeStartTick) / 1000.0;
                summary =
                    $"in view {inViewSeconds:0.0} s, peak score {_episodePeak:0.00}, " +
                    $"{_episodeCandidateFrames} frame(s) at or above {PhoneSettings.MinScore:0.00}";
                _episodeActive = false;
            }
        }

        if (phoneChanged)
        {
            if (phoneSeen)
            {
                Logger.Write(
                    LogKind.Phone,
                    new Seg("SEEN", ConsoleColor.Red),
                    new Seg($"  confirmed at score {score:0.00}; stays seen for {PhoneSettings.HoldMs / 1000.0:0.#} s after the last confirmation", ConsoleColor.Gray));
            }
            else
            {
                Logger.Write(
                    LogKind.Phone,
                    new Seg("cleared", ConsoleColor.Green),
                    new Seg($"  {summary ?? "no confirmation for " + PhoneSettings.HoldMs / 1000.0 + " s"}", ConsoleColor.Gray));
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Statistics of one phone sighting, printed as a single summary line when it ends. Call under <c>_gate</c>.</summary>
    private void TrackEpisode(long now, double score, bool confirmed)
    {
        if (confirmed && !_episodeActive)
        {
            _episodeActive = true;
            _episodeStartTick = now;
            _episodePeak = 0;
            _episodeCandidateFrames = 0;
        }

        if (!_episodeActive)
        {
            return;
        }

        _episodePeak = Math.Max(_episodePeak, score);
        if (score >= PhoneSettings.MinScore)
        {
            _episodeCandidateFrames++;
        }

        if (confirmed)
        {
            _episodeLastConfirmTick = now;
        }
    }

    /// <summary>What a frame's score means, in words, and the color to show it in.</summary>
    private static (string Verdict, ConsoleColor Color) DescribeHit(double score, bool confirmed)
    {
        if (confirmed)
        {
            if (score >= PhoneSettings.FastScore)
            {
                return ("STRONG: confirms the phone on its own", ConsoleColor.Red);
            }

            // A weak frame can still count as confirmed: 2 of the last 3 frames were above MinScore.
            return score >= PhoneSettings.MinScore
                ? ("candidate: confirms the phone (2 of the last 3 frames)", ConsoleColor.Red)
                : ("this frame is weak, the phone stays confirmed by the 2 frames before it", ConsoleColor.DarkRed);
        }

        return score >= PhoneSettings.MinScore
            ? ("candidate: needs a 2nd frame to confirm", ConsoleColor.Yellow)
            : ("weak", ConsoleColor.DarkGray);
    }

    private void ReportProblem(string message)
    {
        if (message != _lastProblem)
        {
            _lastProblem = message;
            Logger.Warn(message);
        }
    }

    public void Dispose()
    {
        // Application.Run disposes the ApplicationContext and so does the using in Main: only the first call counts.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Set();
        _frameReady.Set();

        bool readerDone = !_reader.IsAlive || _reader.Join(3000);
        bool detectorDone = !_detector.IsAlive || _detector.Join(3000);

        // Only free the buffers when no thread can still be using them.
        if (readerDone && detectorDone)
        {
            _pending?.Dispose();
            _working?.Dispose();
            _frameReady.Dispose();
        }
    }
}
