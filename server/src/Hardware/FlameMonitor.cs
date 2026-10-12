using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FirepitController.Hardware;

/// <summary>
/// Read-only view of the flame detector plus the auto-ignition state, for /api/flame and the UI.
/// <c>Present</c> is fail-safe: true only if the detector says flame AND its camera is OK AND the
/// reading is fresh. A dead/stale/unreachable detector is "no flame", never "flame".
/// </summary>
public sealed record FlameStatus(
    bool Present,
    bool Available,
    bool? CameraOk,
    double? Confidence,
    double? AgeS,
    string? Error,
    string AutoIgnite,          // disabled | idle | burning | sparking | no-flame | lockout
    double? TrialRemainingS,
    bool FlameFailure,
    bool AutoIgniteEnabled);

/// <summary>
/// Polls the camera flame detector (advisory) and runs the automatic re-ignition logic:
///
///   gas ON  + flame seen            → igniter off ("burning")
///   gas ON  + no flame / detector down → spark (re-holds the igniter every tick, through the
///                                       normal IgniterController path, so the heartbeat file,
///                                       Relay B watchdog and hold timeout all still apply)
///   no flame after TrialSeconds     → release igniter, CLOSE GAS, lock out until Open gas again
///   gas OFF                         → igniter off, nothing automatic
///
/// The detector is advisory; the thermocouple pilot interlock and watchdogs remain the real safety.
/// </summary>
public sealed class FlameMonitor : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    private readonly IHttpClientFactory _http;
    private readonly GasValveController _gas;
    private readonly IgniterController _igniter;
    private readonly FlameOptions _opts;
    private readonly ILogger<FlameMonitor> _log;
    private readonly object _lock = new();

    // latest detector sample
    private bool _flag;               // detector's "flame" value
    private bool? _cameraOk;
    private double? _confidence;
    private bool _available;
    private string? _error;
    private DateTimeOffset _polledAt = DateTimeOffset.MinValue;   // when the sample was fetched
    private double? _sampleAgeS;                                  // detector ts age at fetch

    // auto-ignition state
    private bool _sparking;
    private bool _prevGas;
    private DateTimeOffset? _trialStart;
    private DateTimeOffset? _valveWaitStart;
    private bool _failure;
    private volatile bool _autoIgnite;   // runtime on/off switch (persisted; initial value from config)
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "firepit", "settings.json");
    private string _state = "idle";
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

    public FlameMonitor(IHttpClientFactory http, GasValveController gas, IgniterController igniter,
        IOptions<FlameOptions> opts, ILogger<FlameMonitor> log)
    {
        _http = http;
        _gas = gas;
        _igniter = igniter;
        _opts = opts.Value;
        _log = log;
        _autoIgnite = LoadAutoIgnite() ?? _opts.AutoIgnite;
        _state = _opts.Enabled ? (_autoIgnite ? "idle" : "no-flame") : "disabled";
    }

    /// <summary>Whether the controller sparks automatically (the user's on/off switch).</summary>
    public bool AutoIgniteEnabled => _autoIgnite;

    /// <summary>Turn automatic ignition on/off at runtime; persisted so it survives restarts and updates.</summary>
    public void SetAutoIgnite(bool enabled)
    {
        _autoIgnite = enabled;
        _log.LogWarning("Auto-ignite turned {State} by user.", enabled ? "ON" : "OFF");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { autoIgnite = enabled }));
        }
        catch (Exception ex) { _log.LogError(ex, "Could not save auto-ignite setting to {Path}.", _settingsPath); }
    }

    private bool? LoadAutoIgnite()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            return doc.RootElement.TryGetProperty("autoIgnite", out var v) ? v.GetBoolean() : null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not read {Path}; using config value.", _settingsPath); return null; }
    }

    private bool PresentLocked(DateTimeOffset now) =>
        _flag && _cameraOk == true && _available
        && _sampleAgeS is { } a
        && a + (now - _polledAt).TotalSeconds < _opts.MaxAgeSeconds;   // detector age at fetch + time since

    public FlameStatus GetStatus()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            double? remaining = _trialStart is { } t
                ? Math.Max(0, _opts.TrialSeconds - (now - t).TotalSeconds)
                : null;
            return new FlameStatus(
                Present: PresentLocked(now),
                Available: _available && (now - _polledAt).TotalSeconds < _opts.MaxAgeSeconds,
                CameraOk: _cameraOk,
                Confidence: _confidence,
                AgeS: _sampleAgeS is { } a ? Math.Round(a + (now - _polledAt).TotalSeconds, 1) : null,
                Error: _error,
                AutoIgnite: _state,
                TrialRemainingS: remaining is { } r ? Math.Round(r, 1) : null,
                FlameFailure: _failure,
                AutoIgniteEnabled: _autoIgnite);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _log.LogWarning("Flame monitor disabled (Flame:Enabled=false) — no auto-ignition.");
            return;
        }
        _log.LogInformation(
            "Flame monitor on: poll {Poll}s, auto-ignite {Auto}, trial {Trial}s, url {Url}.",
            _opts.PollSeconds, _autoIgnite, _opts.TrialSeconds, _opts.Url);

        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Never let the supervisor die. On any fault: no flame, stop sparking this tick
                    // (the igniter hold simply goes stale and drops by itself).
                    _log.LogError(ex, "Flame monitor tick failed.");
                    lock (_lock) { _flag = false; _sparking = false; }
                }
            }
        }
        finally
        {
            if (_sparking) _igniter.Release();
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var pollEvery = TimeSpan.FromSeconds(_sparking ? Math.Min(1, _opts.PollSeconds) : _opts.PollSeconds);
        if (now - _lastPoll >= pollEvery)
        {
            _lastPoll = now;
            await PollAsync(ct);
        }

        bool gasOn = _gas.GasRequested;
        bool present;
        lock (_lock) present = PresentLocked(now);

        if (!gasOn)
        {
            if (_sparking) { _igniter.Release(); _sparking = false; }
            lock (_lock)
            {
                _trialStart = null; _valveWaitStart = null;
                _prevGas = false;
                _state = _failure ? "lockout" : (_autoIgnite ? "idle" : "no-flame");
            }
            return;
        }

        if (!_prevGas)   // gas was just opened: clear any previous lockout, start fresh
        {
            lock (_lock) { _failure = false; _trialStart = null; _prevGas = true; }
        }

        if (present)
        {
            if (_sparking)
            {
                _igniter.Release();
                _sparking = false;
                _log.LogInformation("Flame detected — auto-ignition spark OFF.");
            }
            lock (_lock) { _trialStart = null; _state = "burning"; }
            return;
        }

        // Gas on, no (trustworthy) flame.
        if (!_autoIgnite)
        {
            if (_sparking) { _igniter.Release(); _sparking = false; }   // switched off while sparking
            lock (_lock) { _trialStart = null; _valveWaitStart = null; _state = "no-flame"; }
            return;
        }

        // Ball valve must be confirmed fully open before any spark; the trial clock starts after that.
        if (!_gas.ValveFullyOpen)
        {
            if (_sparking) { _igniter.Release(); _sparking = false; }
            DateTimeOffset since;
            lock (_lock) { _valveWaitStart ??= now; since = _valveWaitStart.Value; _state = "valve-opening"; }
            if ((now - since).TotalSeconds >= _opts.ValveWaitSeconds)
            {
                _log.LogError("FLAME FAILURE - valve did not reach 100% in {S}s. Closing gas.", _opts.ValveWaitSeconds);
                lock (_lock) { _failure = true; _valveWaitStart = null; _prevGas = false; _state = "lockout"; }
                _gas.CloseGas();
            }
            return;
        }
        lock (_lock) _valveWaitStart = null;

        DateTimeOffset start;
        lock (_lock) { _trialStart ??= now; start = _trialStart.Value; }

        if ((now - start).TotalSeconds >= _opts.TrialSeconds)
        {
            _igniter.Release();
            _sparking = false;
            _log.LogError("FLAME FAILURE — no flame after {Trial}s of sparking. Closing gas; lockout until Open gas.",
                _opts.TrialSeconds);
            lock (_lock) { _failure = true; _trialStart = null; _prevGas = false; _state = "lockout"; }
            _gas.CloseGas();
            return;
        }

        await _igniter.HoldAsync(ct);   // same dead-man path as the button: heartbeat + Relay B watchdog
        if (!_sparking)
        {
            _sparking = true;
            _log.LogWarning("Gas on, no flame — auto-ignition spark ON (trial {Trial}s).", _opts.TrialSeconds);
        }
        lock (_lock) _state = "sparking";
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));
            var client = _http.CreateClient("flame");
            using var resp = await client.GetAsync(_opts.Url, cts.Token);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            bool flame = root.GetProperty("flame").GetBoolean();
            bool camOk = root.GetProperty("camera_ok").GetBoolean();
            double ts = root.GetProperty("ts").GetDouble();
            double? conf = root.TryGetProperty("confidence", out var c) ? c.GetDouble() : null;
            var age = now.ToUnixTimeMilliseconds() / 1000.0 - ts;
            lock (_lock)
            {
                _flag = flame; _cameraOk = camOk; _confidence = conf;
                _available = true; _error = null; _polledAt = now; _sampleAgeS = Math.Max(0, age);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                _flag = false; _cameraOk = null; _confidence = null;
                _available = false; _error = ex is OperationCanceledException ? "detector timeout" : ex.Message;
                _polledAt = now; _sampleAgeS = null;
            }
        }
    }
}
