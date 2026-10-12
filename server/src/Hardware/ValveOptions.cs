namespace FirepitController.Hardware;

/// <summary>Configuration for the Tuya valve shim, bound from "Valve".</summary>
public sealed class ValveOptions
{
    /// <summary>Base URL of the local Python valve shim.</summary>
    public string ShimUrl { get; set; } = "http://127.0.0.1:5006";

    /// <summary>HTTP timeout for shim calls (the WiFi valve can be slow).</summary>
    public double TimeoutSeconds { get; set; } = 10;

    /// <summary>Disable valve calls on dev machines without the shim.</summary>
    public bool Enabled { get; set; } = true;
}
