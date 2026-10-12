namespace FirepitController.Hardware;

/// <summary>
/// No-op relay array for development on machines without GPIO (e.g. Windows).
/// Tracks commanded state and logs, so the web app and control logic can be
/// exercised off the Pi. Never touches hardware.
/// </summary>
public sealed class SimulatedRelayArray : IRelayArray
{
    private readonly bool[] _commanded;
    private readonly ILogger<SimulatedRelayArray> _log;
    private readonly object _lock = new();

    public SimulatedRelayArray(int channelCount, ILogger<SimulatedRelayArray> log)
    {
        _commanded = new bool[channelCount];
        _log = log;
        _log.LogWarning("SIMULATED relay array ({Count} channels) — no real GPIO. Dev mode only.", channelCount);
    }

    public int ChannelCount => _commanded.Length;
    public bool IsSimulated => true;
    public IReadOnlyList<bool> CommandedStates => (bool[])_commanded.Clone();

    public void ForceSafe()
    {
        lock (_lock)
        {
            Array.Clear(_commanded);
            _log.LogInformation("[SIM] ForceSafe → all channels LOW (gas OFF).");
        }
    }

    public void SetChannel(int index, bool energized)
    {
        lock (_lock)
        {
            _commanded[index] = energized;
            _log.LogInformation("[SIM] Channel {Index} → {State}.", index, energized ? "HIGH" : "LOW");
        }
    }

    public void SetAll(bool energized)
    {
        lock (_lock)
        {
            for (int i = 0; i < _commanded.Length; i++) _commanded[i] = energized;
            _log.LogInformation("[SIM] All channels → {State}.", energized ? "HIGH" : "LOW");
        }
    }

    public void Dispose() => ForceSafe();
}
