using System.Device.Gpio;
using System.Device.Gpio.Drivers;

namespace MeshCoreNet;

internal static class GpioControllerFactory
{
    public static GpioController Create()
    {
        try
        {
            return new GpioController();
        }
        catch (EntryPointNotFoundException ex)
        {
            Console.WriteLine($"GPIO libgpiod ABI mismatch detected ({ex.Message}). Falling back to SysFs GPIO driver.");
            return new GpioController(new SysFsDriver());
        }
        catch (DllNotFoundException ex) when (ex.Message.Contains("libgpiod", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"GPIO libgpiod runtime not found ({ex.Message}). Falling back to SysFs GPIO driver.");
            return new GpioController(new SysFsDriver());
        }
    }
}
