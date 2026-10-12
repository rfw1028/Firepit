namespace FirepitController.Hardware;

/// <summary>Configuration for the app-owned igniter relay (Relay A), bound from "Igniter".</summary>
public sealed class IgniterOptions
{
    /// <summary>
    /// BCM GPIO pin for Relay A (app-owned). Relay B (BCM 6) belongs exclusively to the
    /// independent firepit-igniter-watchdog service and is intentionally not listed here —
    /// same convention as CH3 being excluded from Relays.BcmPins.
    /// </summary>
    public int[] BcmPins { get; set; } = [];

    /// <summary>Force simulation even on Linux (for testing).</summary>
    public bool ForceSimulation { get; set; }
}
