namespace FirepitController.Hardware;

/// <summary>Configuration for the external-watchdog heartbeat, bound from "Heartbeat".</summary>
public sealed class HeartbeatOptions
{
    /// <summary>File the watchdog polls. On /run (tmpfs) so it clears on reboot.</summary>
    public string Path { get; set; } = "/run/firepit/heartbeat";

    /// <summary>How often to refresh the heartbeat. Must be well under the watchdog's stale threshold.</summary>
    public int IntervalMs { get; set; } = 250;

    /// <summary>Disable on dev machines (no /run, no watchdog).</summary>
    public bool Enabled { get; set; } = true;
}
