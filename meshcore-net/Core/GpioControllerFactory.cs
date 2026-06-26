using System.Device.Gpio;
using System.Device.Gpio.Drivers;

namespace MeshCoreNet;

internal static class GpioControllerFactory
{
    public static GpioController Create()
    {
        try
        {
            // Prefer libgpiod v2 (character device ABI) when available.
            return new GpioController(new LibGpiodV2Driver(0));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeLoadException)
        {
            Console.WriteLine($"GPIO libgpiod v2 driver unavailable ({ex.Message}). Trying libgpiod v1 driver.");
        }

        try
        {
            // Fallback to libgpiod v1 driver before giving up.
            return new GpioController(new LibGpiodDriver(0));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeLoadException)
        {
            throw new InvalidOperationException(
                $"No compatible libgpiod GPIO driver is available ({ex.Message}). Install a compatible libgpiod runtime and ensure /dev/gpiochip* is accessible to the service account.",
                ex);
        }
    }
}
