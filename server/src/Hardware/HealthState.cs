namespace FirepitController.Hardware;

/// <summary>
/// Shared, thread-safe health flag for the control application.
///
/// The <see cref="HeartbeatService"/> only writes the heartbeat file while
/// <see cref="Healthy"/> is true. Setting it false — whether by a detected fault or a
/// deliberate self-abort — stops the heartbeat, which trips the independent external
/// watchdog (it cuts the CH3 relay → gas off, then reboots if it stays stale).
/// </summary>
public sealed class HealthState
{
    private volatile bool _healthy = true;

    public bool Healthy => _healthy;
    public string? Reason { get; private set; }

    /// <summary>Stop heartbeating → hand control to the external watchdog.</summary>
    public void MarkUnhealthy(string reason)
    {
        Reason = reason;
        _healthy = false;
    }

    public void MarkHealthy()
    {
        Reason = null;
        _healthy = true;
    }
}
