namespace FirepitController.Hardware;

/// <summary>Configuration for the igniter dead-man heartbeat, bound from "IgniterHeartbeat".</summary>
public sealed class IgniterHeartbeatOptions
{
    /// <summary>File the independent igniter watchdog polls. On /run (tmpfs).</summary>
    public string Path { get; set; } = "/run/firepit/igniter-heartbeat";

    /// <summary>
    /// How long since the last hold ping before the igniter is force-released.
    /// Must be comfortably above the browser's ping interval (500 ms) to absorb jitter.
    /// </summary>
    public int HoldTimeoutMs { get; set; } = 1500;
}
