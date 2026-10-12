using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FirepitController.Hardware;

/// <summary>
/// Manual dead-man control for the Eaton continuous-ignition module's 12V enable input.
///
/// Owns Relay A only (BCM 5). Relay B (BCM 6) is in series with it and is owned
/// exclusively by the independent firepit-igniter-watchdog process — same split as
/// CH1/CH2 (app) + CH3 (watchdog) for the gas relays. Both relays must be energized
/// for the igniter module to be enabled; either dropping cuts it.
///
/// There is no explicit "off" signal this depends on: the operator holds a button in
/// the browser, which pings <see cref="Hold"/> roughly every 500 ms. Relay A is driven
/// LOW the moment pings go stale (checked immediately on late ping, and proactively by
/// <see cref="IgniterHoldTimeoutService"/>), not just on an explicit release call.
/// </summary>
public sealed class IgniterController : IDisposable
{
    private readonly IRelayArray _relays;
    private readonly IgniterHeartbeatOptions _hbOpts;
    private readonly ILogger<IgniterController> _log;
    private readonly object _lock = new();
    private readonly string? _heartbeatDir;
    private bool _active;
    private DateTimeOffset _lastHoldUtc = DateTimeOffset.MinValue;

    public IgniterController(
        [FromKeyedServices("igniter")] IRelayArray relays,
        IOptions<IgniterHeartbeatOptions> hbOpts,
        ILogger<IgniterController> log)
    {
        _relays = relays;
        _hbOpts = hbOpts.Value;
        _log = log;
        _relays.ForceSafe();

        try
        {
            _heartbeatDir = System.IO.Path.GetDirectoryName(_hbOpts.Path);
            if (!string.IsNullOrEmpty(_heartbeatDir)) Directory.CreateDirectory(_heartbeatDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not create igniter heartbeat directory for {Path}.", _hbOpts.Path);
        }
    }

    public bool IsSimulated => _relays.IsSimulated;

    public TimeSpan HoldTimeout => TimeSpan.FromMilliseconds(_hbOpts.HoldTimeoutMs);

    /// <summary>Called on each browser hold ping. Energizes Relay A and refreshes the heartbeat.</summary>
    public async Task HoldAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_active)
            {
                _relays.SetChannel(0, true);
                _active = true;
                _log.LogInformation("Igniter Relay A energized (hold).");
            }
            _lastHoldUtc = DateTimeOffset.UtcNow;
        }

        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(_heartbeatDir)) return;
        try
        {
            await File.WriteAllTextAsync(
                _hbOpts.Path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to write igniter heartbeat to {Path}.", _hbOpts.Path);
        }
    }

    /// <summary>De-energize Relay A. Used by explicit release, the timeout poller, and shutdown.</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (!_active) return;
            _relays.SetChannel(0, false);
            _active = false;
            _log.LogInformation("Igniter Relay A de-energized (release).");
        }
    }

    public void ForceSafe()
    {
        lock (_lock)
        {
            _relays.ForceSafe();
            _active = false;
        }
    }

    public IgniterStatus GetStatus()
    {
        lock (_lock)
        {
            var ageMs = _lastHoldUtc == DateTimeOffset.MinValue
                ? (double?)null
                : (DateTimeOffset.UtcNow - _lastHoldUtc).TotalMilliseconds;
            return new IgniterStatus(
                Active: _active,
                Simulated: _relays.IsSimulated,
                ChannelStates: _relays.CommandedStates,
                HoldAgeMs: ageMs);
        }
    }

    /// <summary>Called by <see cref="IgniterHoldTimeoutService"/>. True if pings have gone stale.</summary>
    internal bool IsStale(TimeSpan timeout)
    {
        lock (_lock)
        {
            return _active && DateTimeOffset.UtcNow - _lastHoldUtc > timeout;
        }
    }

    public void Dispose() => ForceSafe();
}

/// <summary>Immutable snapshot of igniter state for the API and UI.</summary>
public sealed record IgniterStatus(bool Active, bool Simulated, IReadOnlyList<bool> ChannelStates, double? HoldAgeMs);
