namespace FirepitController.Hardware;

/// <summary>
/// High-level gas control. Owns the series relay array and is the single place
/// the rest of the app calls to enable or shut off gas. Singleton.
///
/// "Gas on" means ALL series relays energized (HIGH). Any shutoff path drives
/// everything LOW via <see cref="ForceSafe"/>.
/// </summary>
public sealed class GasValveController : IDisposable
{
    private readonly IRelayArray _relays;
    private readonly ValveController _valve;
    private readonly ILogger<GasValveController> _log;
    private readonly object _lock = new();
    private bool _gasRequested;
    private bool _pending;          // valve moving to 100%, relays not yet energized
    private string? _openError;

    public GasValveController(IRelayArray relays, ValveController valve, ILogger<GasValveController> log)
    {
        _relays = relays;
        _valve = valve;
        _log = log;
        // Guarantee a known safe relay state at construction (valve is handled at
        // startup in Program, once the shim is reachable — not from the ctor).
        _relays.ForceSafe();
    }

    /// <summary>True if gas is currently commanded on (all relays energized).</summary>
    public bool GasRequested { get { lock (_lock) return _gasRequested; } }

    /// <summary>True once the ball valve has confirmed fully open (100%) after the last Open gas.</summary>
    public bool ValveFullyOpen => _valve.FullyOpen;

    public bool IsSimulated => _relays.IsSimulated;

    /// <summary>
    /// Open gas, valve first: the ball valve is driven to 100% (skipped if already there) and only once it
    /// CONFIRMS 100% are the series relays energized. If it never confirms, the relays stay off.
    /// </summary>
    public void OpenGas()
    {
        lock (_lock)
        {
            if (_gasRequested || _pending) return;
            _pending = true;
            _openError = null;
            _log.LogWarning("OPEN GAS requested - valve to 100% first, relays after it confirms.");
        }
        _valve.RequestFullOpen("gas open requested", EnergizeRelays, reason =>
        {
            lock (_lock) { _pending = false; _openError = "Valve did not reach 100%, gas NOT opened: " + reason; }
            _log.LogError("Gas NOT opened - valve did not confirm 100%: {Reason}", reason);
        });
    }

    private void EnergizeRelays()
    {
        lock (_lock)
        {
            if (!_pending) return;   // closed meanwhile
            _pending = false;
            _relays.SetAll(true);
            _gasRequested = true;
            _log.LogWarning("GAS OPENED - all {Count} series relays energized (HIGH), valve at 100%.", _relays.ChannelCount);
        }
    }

    /// <summary>De-energize all series relays → gas off.</summary>
    public void CloseGas()
    {
        lock (_lock)
        {
            _relays.SetAll(false);
            _gasRequested = false;
            _pending = false;
            _log.LogWarning("GAS CLOSED — all series relays de-energized (LOW).");
        }
        _valve.RequestClose("gas closed");
    }

    /// <summary>Unconditional shutoff. Used on startup, shutdown, and any fault.</summary>
    public void ForceSafe()
    {
        lock (_lock)
        {
            _relays.ForceSafe();
            _gasRequested = false;
            _pending = false;
            _log.LogInformation("ForceSafe → gas OFF.");
        }
        _valve.RequestClose("force-safe");
    }

    public GasStatus GetStatus()
    {
        lock (_lock)
        {
            return new GasStatus(
                GasRequested: _gasRequested,
                Pending: _pending,
                OpenError: _openError,
                Simulated: _relays.IsSimulated,
                ChannelStates: _relays.CommandedStates);
        }
    }

    public void Dispose() => ForceSafe();
}

/// <summary>Immutable snapshot of gas/relay state for the API and UI.</summary>
public sealed record GasStatus(bool GasRequested, bool Pending, string? OpenError, bool Simulated, IReadOnlyList<bool> ChannelStates);
