namespace MicrosoftPurviewScreenGuard;

/// <summary>
/// WinForms message loop owner. A 250 ms timer polls Word and the console key; camera health changes trigger
/// the policy immediately on the UI thread.
/// </summary>
internal sealed class GuardContext : ApplicationContext
{
    private const int PollIntervalMs = 250;
    private const int StatsIntervalMs = 1000;
    private const int SlowTickMs = 500;
    private const int CameraStartDelayMs = 300;

    private readonly WordMonitor _word;
    private readonly OverlayManager _overlays;
    private readonly CameraMonitor _camera;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = PollIntervalMs };

    // One-shot: lets the overlay painted by the first evaluation reach the screen before the camera thread starts.
    private readonly System.Windows.Forms.Timer _cameraStartTimer = new() { Interval = CameraStartDelayMs };
    private readonly Control _marshal = new();

    private bool _phoneOverride;
    private bool _keysEnabled = true;
    private int _cameraChangePending;
    private long _lastStatsTick;
    private bool _disposed;
    private bool _prevDetectorPhone;
    private bool _prevBlocked;
    private (bool Sensitive, bool Override, bool Detected, bool CameraHealthy, bool Blocked, int Visible)? _lastLogged;
    private string? _lastError;

    public GuardContext(BlockedLabels blocked)
    {
        _word = new WordMonitor(new WordLabelReader(blocked));
        _overlays = new OverlayManager();
        _camera = new CameraMonitor();

        // A handle on this thread lets other threads (Ctrl+C, camera reader) reach the UI thread safely.
        _ = _marshal.Handle;

        Logger.Log("Press P in this console to toggle the manual 'phone seen' override.");

        _camera.Changed += OnCameraChanged;

        _timer.Tick += (_, _) => Guarded(OnTick);
        _timer.Start();

        // The camera starts after the first Word poll, not before. The first dynamic COM call to Word is slow
        // (~2 s), and a cold camera open in parallel stretched it to ~8 s, leaving a sensitive document uncovered.
        _cameraStartTimer.Tick += (_, _) =>
        {
            _cameraStartTimer.Stop();
            _camera.Start();
        };

        // Run the first evaluation as soon as the message loop starts instead of waiting for the first timer tick.
        _marshal.BeginInvoke(new Action(() =>
        {
            Guarded(OnTick);
            _cameraStartTimer.Start();
        }));
    }

    /// <summary>Safe to call from any thread (used by the Ctrl+C handler).</summary>
    public void RequestExit() => _marshal.BeginInvoke(new Action(ExitThread));

    /// <summary>Timer tick: refresh the cached Word state, read the key, evaluate the policy.</summary>
    private void OnTick()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        PollKeys();
        _word.Poll();
        _camera.SetDetectionActive(_word.SensitiveVisible);
        long wordMs = watch.ElapsedMilliseconds;
        Evaluate();
        LogCameraStats();

        // A slow tick means the UI thread was stalled: overlays cannot react until it returns.
        if (watch.ElapsedMilliseconds > SlowTickMs)
        {
            Logger.Log($"SLOW poll tick: {watch.ElapsedMilliseconds} ms total ({wordMs} ms in Word poll)");
        }
    }

    /// <summary>Runs on the camera reader thread. Coalesces bursts into one pending UI-thread evaluation.</summary>
    private void OnCameraChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _cameraChangePending, 1) != 0)
        {
            return;
        }

        try
        {
            _marshal.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _cameraChangePending, 0);
                Guarded(Evaluate);
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutting down: the UI thread is gone.
            Interlocked.Exchange(ref _cameraChangePending, 0);
        }
    }

    /// <summary>
    /// Phone seen = manual P override OR detector. Block = sensitive visible AND (phone seen OR camera not healthy).
    /// Uses the cached Word state; only the timer refreshes it.
    /// </summary>
    private void Evaluate()
    {
        CameraSnapshot camera = _camera.GetSnapshot();
        bool phoneSeen = _phoneOverride || camera.PhoneSeen;
        bool blocked = BlockPolicy.ShouldBlock(_word.SensitiveVisible, phoneSeen, camera.Healthy);

        // Timestamp of the moment blocking is decided; the overlay logs how long it takes until it has painted.
        long decidedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _overlays.Apply(blocked, decidedAt);

        LogReaction(camera, blocked);
        LogIfChanged(camera, blocked);
    }

    /// <summary>
    /// When the overlay appears because the detector just saw a phone, logs how long it took since the
    /// frame that confirmed the phone was captured.
    /// </summary>
    private void LogReaction(CameraSnapshot camera, bool blocked)
    {
        bool phoneStarted = camera.PhoneSeen && !_prevDetectorPhone;
        bool overlayAppeared = blocked && !_prevBlocked && _overlays.VisibleCount > 0;
        if (overlayAppeared && phoneStarted)
        {
            long ms = Environment.TickCount64 - _camera.PhoneEpisodeCaptureTick;
            Logger.Log($"[reaction] {ms} ms");
        }

        _prevDetectorPhone = camera.PhoneSeen;
        _prevBlocked = blocked;
    }

    private void Guarded(Action action)
    {
        try
        {
            action();
            _lastError = null;
        }
        catch (Exception ex)
        {
            // Keep whatever state the overlays are in; report each distinct error once.
            string message = $"{ex.GetType().Name}: {ex.Message}";
            if (message != _lastError)
            {
                Logger.Log("ERROR in guard loop, keeping previous state: " + message);
                _lastError = message;
            }
        }
    }

    private void PollKeys()
    {
        if (!_keysEnabled)
        {
            return;
        }

        try
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.P)
                {
                    _phoneOverride = !_phoneOverride;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Input is redirected: there is no console to read keys from.
            _keysEnabled = false;
            Logger.Log("WARNING: console input is not available; the P key override is disabled.");
        }
    }

    private void LogCameraStats()
    {
        long now = Environment.TickCount64;
        if (now - _lastStatsTick < StatsIntervalMs)
        {
            return;
        }

        _lastStatsTick = now;
        CameraSnapshot snapshot = _camera.GetSnapshot();
        string reason = snapshot.Healthy ? string.Empty : $" reason=\"{snapshot.Reason}\"";
        string mode = _camera.DetectionActive ? "active" : "idle";
        Logger.Log($"CAMERA healthy={Lower(snapshot.Healthy)} phoneSeen={Lower(snapshot.PhoneSeen)} detector={mode} {snapshot.Status}{reason}");
    }

    private void LogIfChanged(CameraSnapshot camera, bool blocked)
    {
        (bool Sensitive, bool Override, bool Detected, bool CameraHealthy, bool Blocked, int Visible) state =
            (_word.SensitiveVisible, _phoneOverride, camera.PhoneSeen, camera.Healthy, blocked, _overlays.VisibleCount);
        if (_lastLogged == state)
        {
            return;
        }

        _lastLogged = state;
        Logger.Log(
            $"STATE sensitive={Lower(state.Sensitive)} phoneOverride={Lower(state.Override)} phoneDetected={Lower(state.Detected)} " +
            $"cameraHealthy={Lower(state.CameraHealthy)} blocked={Lower(state.Blocked)} overlaysVisible={state.Visible}");
    }

    private static string Lower(bool value) => value ? "true" : "false";

    protected override void Dispose(bool disposing)
    {
        // Application.Run disposes the context and so does the using in Main: only the first call counts.
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Dispose();
            _cameraStartTimer.Dispose();
            _camera.Changed -= OnCameraChanged;
            _camera.Dispose();
            _overlays.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
