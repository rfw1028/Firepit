namespace FirepitController.Hardware;

/// <summary>
/// Low-level control of the three series gas-shutoff relays.
///
/// SAFETY MODEL (verified on hardware 2026-07-25):
///  - Relays are ACTIVE-HIGH: GPIO HIGH = coil energized = NO contact closed.
///  - All three NO contacts are wired in SERIES, so ALL must be energized (HIGH)
///    for the 24 VDC gas valve to be powered. Any one de-energized = gas OFF.
///  - The safe state is therefore ALL channels LOW (de-energized).
/// </summary>
public interface IRelayArray : IDisposable
{
    /// <summary>Number of relay channels (3 for the FirePit).</summary>
    int ChannelCount { get; }

    /// <summary>True if this is a no-op simulation (dev machine, no GPIO).</summary>
    bool IsSimulated { get; }

    /// <summary>
    /// Drive EVERY channel to de-energized (LOW) — the safe, gas-off state.
    /// This is the method every failure/shutdown path funnels through.
    /// </summary>
    void ForceSafe();

    /// <summary>Energize (true = HIGH) or de-energize (false = LOW) one channel.</summary>
    void SetChannel(int index, bool energized);

    /// <summary>Energize/de-energize all channels together.</summary>
    void SetAll(bool energized);

    /// <summary>Last commanded state of each channel (what the pin was told to do).</summary>
    IReadOnlyList<bool> CommandedStates { get; }
}
