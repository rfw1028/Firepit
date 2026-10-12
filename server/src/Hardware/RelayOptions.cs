namespace FirepitController.Hardware;

/// <summary>Configuration for the gas-relay hardware, bound from the "Relays" config section.</summary>
public sealed class RelayOptions
{
    /// <summary>
    /// BCM GPIO pins for the series relays. Verified: CH1=26, CH2=20, CH3=21.
    /// Default is EMPTY on purpose: a non-empty default causes the config binder to
    /// APPEND bound values to the default (duplicating pins). The canonical default
    /// [26,20,21] is applied in the DI factory when this is empty.
    /// </summary>
    public int[] BcmPins { get; set; } = [];

    /// <summary>
    /// Force simulation even on Linux (for testing). When null/false on Linux, real GPIO is used;
    /// on non-Linux platforms simulation is always used regardless.
    /// </summary>
    public bool ForceSimulation { get; set; }
}
