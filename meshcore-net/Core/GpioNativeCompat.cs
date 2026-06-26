using System.Device.Gpio;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MeshCoreNet;

internal static class GpioNativeCompat
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(GpioController).Assembly, ResolveLibgpiod);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GPIO native compatibility resolver could not be registered: {ex.Message}");
        }
    }

    private static IntPtr ResolveLibgpiod(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("libgpiod.so.2", StringComparison.Ordinal) &&
            !libraryName.Equals("libgpiod", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        // Support distros that ship libgpiod under different SONAME versions.
        foreach (var candidate in new[] { "libgpiod.so.3", "libgpiod.so.2", "libgpiod.so.1", "libgpiod" })
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}
