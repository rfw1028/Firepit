using System.Device.Gpio;

namespace FirepitController.Hardware;

/// <summary>
/// Real GPIO implementation of <see cref="IRelayArray"/> using System.Device.Gpio.
/// Verified working on the FirePit Pi (Pi 4B, Debian trixie, .NET 10, S.D.Gpio 3.2).
///
/// Every pin is opened as Output and immediately driven LOW so the relays are in the
/// safe (gas-off) state the instant this object exists. Note: on this hardware the
/// pin RETAINS its last state through a process crash (see project safety notes), so
/// crash fail-safe is handled at the systemd/watchdog/reboot layer, not here.
/// </summary>
public sealed class GpioRelayArray : IRelayArray
{
    private readonly GpioController _controller;
    private readonly int[] _pins;
    private readonly bool[] _commanded;
    private readonly ILogger<GpioRelayArray> _log;
    private readonly object _lock = new();
    private bool _disposed;

    public GpioRelayArray(int[] bcmPins, ILogger<GpioRelayArray> log)
    {
        if (bcmPins is null || bcmPins.Length == 0)
            throw new ArgumentException("At least one relay BCM pin must be configured.", nameof(bcmPins));

        _pins = bcmPins;
        _commanded = new bool[_pins.Length];
        _log = log;

        _controller = new GpioController();
        foreach (var pin in _pins)
        {
            _controller.OpenPin(pin, PinMode.Output);
            _controller.Write(pin, PinValue.Low); // force safe on open
        }
        _log.LogInformation("GPIO relay array initialized on BCM pins [{Pins}] — all driven LOW (gas OFF).",
            string.Join(", ", _pins));
    }

    public int ChannelCount => _pins.Length;
    public bool IsSimulated => false;
    public IReadOnlyList<bool> CommandedStates => (bool[])_commanded.Clone();

    public void ForceSafe()
    {
        lock (_lock)
        {
            if (_disposed) return;
            for (int i = 0; i < _pins.Length; i++)
            {
                _controller.Write(_pins[i], PinValue.Low);
                _commanded[i] = false;
            }
        }
    }

    public void SetChannel(int index, bool energized)
    {
        if (index < 0 || index >= _pins.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        lock (_lock)
        {
            if (_disposed) return;
            _controller.Write(_pins[index], energized ? PinValue.High : PinValue.Low);
            _commanded[index] = energized;
        }
    }

    public void SetAll(bool energized)
    {
        lock (_lock)
        {
            if (_disposed) return;
            for (int i = 0; i < _pins.Length; i++)
            {
                _controller.Write(_pins[i], energized ? PinValue.High : PinValue.Low);
                _commanded[i] = energized;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            // Drive safe, THEN release the pins.
            try
            {
                foreach (var pin in _pins)
                {
                    _controller.Write(pin, PinValue.Low);
                    _controller.ClosePin(pin);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error while force-safing relays during dispose.");
            }
            _controller.Dispose();
            _disposed = true;
            _log.LogInformation("GPIO relay array disposed — relays left LOW (gas OFF).");
        }
    }
}
