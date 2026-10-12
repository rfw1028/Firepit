using System.Device.Gpio;
using System.Device.Gpio.Drivers;

// Safe GPIO feasibility probe for the FirePit Pi.
// Drives ONE relay channel (default CH1 = BCM26) HIGH, holds long enough to be killed
// mid-hold, then LOW, then releases. Gas is NOT wired yet, so a click/LED is harmless.
//
// Args: [pin] [driver] [holdSeconds]
//   driver = "libgpiod" forces the libgpiod character-device driver (kernel owns the line);
//            anything else uses the default auto-selected driver.

int pin = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 26;
bool useLibgpiod = args.Length > 1 && args[1].Equals("libgpiod", StringComparison.OrdinalIgnoreCase);
int holdSec = args.Length > 2 && int.TryParse(args[2], out var h) ? h : 3;

Console.WriteLine($"System.Device.Gpio probe starting. Pin BCM{pin}, driver={(useLibgpiod ? "libgpiod" : "auto")}, hold={holdSec}s");

try
{
    using var controller = useLibgpiod
        ? new GpioController(PinNumberingScheme.Logical, new LibGpiodDriver(0))
        : new GpioController();
    Console.WriteLine($"GpioController created OK. Driver: {controller.GetType().Name} (numbering: {controller.NumberingScheme})");

    controller.OpenPin(pin, PinMode.Output);
    Console.WriteLine($"Pin {pin} opened as Output.");

    controller.Write(pin, PinValue.High);
    Console.WriteLine($"Pin {pin} -> HIGH (relay should energize). Holding {holdSec}s...");
    Thread.Sleep(holdSec * 1000);

    Console.WriteLine($"Read-back of pin {pin}: {controller.Read(pin)}");

    controller.Write(pin, PinValue.Low);
    Console.WriteLine($"Pin {pin} -> LOW (relay relaxed). Holding 1s...");
    Thread.Sleep(1000);

    controller.ClosePin(pin);
    Console.WriteLine("Pin closed / released. SUCCESS: System.Device.Gpio controls this Pi.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAILURE: " + ex.GetType().FullName + ": " + ex.Message);
    Console.WriteLine(ex.ToString());
    return 1;
}
