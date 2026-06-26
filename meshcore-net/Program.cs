using MeshCoreNet;
using System.Security.Cryptography;
using System.Text;

namespace MeshCoreNet;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        GpioNativeCompat.Initialize();

        if (TryHandleUtilityCommand(args))
        {
            return 0;
        }

        var options = ParseHostOptions(args);

        FullStackLogger.Initialize(logFileName: options.ServiceMode switch
        {
            HostServiceMode.Web => "meshcore-web.log",
            HostServiceMode.Repeater => "meshcore-repeater.log",
            HostServiceMode.Companion => "meshcore-companion.log",
            _ => "meshcore-netcore.log"
        });
        try
        {
            var configPath = ResolveConfigPath(options.ConfigPathArg);
            Console.WriteLine($"Using configuration file: {configPath}");
            Console.WriteLine($"Starting host in service mode: {options.ServiceMode.ToString().ToLowerInvariant()}");

            var config = SimpleTomlParser.ParseFile(configPath);
            var host = new MeshHost(config, configPath);
            await host.RunAsync(CancellationToken.None, options.ServiceMode);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MeshCore .NET host failed: {ex.Message}");
            return 1;
        }
        finally
        {
            FullStackLogger.Shutdown();
        }
    }

    private static bool TryHandleUtilityCommand(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        if (!args[0].Equals("--generate-admin-keys", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Directory.GetCurrentDirectory(), ".");
        GenerateAdminKeys(directory);
        return true;
    }

    private static HostOptions ParseHostOptions(string[] args)
    {
        string? configPathArg = null;
        var serviceMode = HostServiceMode.All;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--service=", StringComparison.OrdinalIgnoreCase))
            {
                serviceMode = ParseServiceMode(arg[(arg.IndexOf('=') + 1)..]);
                continue;
            }

            if (arg.Equals("--service", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for --service. Expected one of: web, repeater, companion, all.");
                }

                serviceMode = ParseServiceMode(args[++i]);
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            configPathArg ??= arg;
        }

        return new HostOptions(configPathArg, serviceMode);
    }

    private static HostServiceMode ParseServiceMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "web" => HostServiceMode.Web,
            "repeater" => HostServiceMode.Repeater,
            "companion" => HostServiceMode.Companion,
            "all" => HostServiceMode.All,
            _ => throw new ArgumentException($"Unsupported --service value '{value}'. Expected one of: web, repeater, companion, all.")
        };
    }

    private static void GenerateAdminKeys(string directory)
    {
        Directory.CreateDirectory(directory);

        var passwordPath = Path.Combine(directory, "password");
        var privatePath = Path.Combine(directory, "private");
        var publicPath = Path.Combine(directory, "public");

        if (!File.Exists(passwordPath) || string.IsNullOrWhiteSpace(File.ReadAllText(passwordPath)))
        {
            var passwordBytes = RandomNumberGenerator.GetBytes(24);
            var password = Convert.ToBase64String(passwordBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            File.WriteAllText(passwordPath, password + Environment.NewLine, Encoding.UTF8);
            Console.WriteLine($"Generated admin password file: {passwordPath}");
        }

        if (!File.Exists(privatePath) || string.IsNullOrWhiteSpace(File.ReadAllText(privatePath)) ||
            !File.Exists(publicPath) || string.IsNullOrWhiteSpace(File.ReadAllText(publicPath)))
        {
            var key = new MeshEd25519PrivateKey();
            File.WriteAllText(privatePath, Convert.ToHexString(key.PrivateKey).ToLowerInvariant() + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(publicPath, Convert.ToHexString(key.PublicKey).ToLowerInvariant() + Environment.NewLine, Encoding.UTF8);
            Console.WriteLine($"Generated admin key files: {publicPath}, {privatePath}");
        }
    }

    private static string ResolveConfigPath(string? explicitConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            return Path.GetFullPath(explicitConfigPath);
        }

        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."))
        };

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(root, "config.toml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var exampleCandidate = Path.Combine(root, "example-config.toml");
            if (File.Exists(exampleCandidate))
            {
                return exampleCandidate;
            }
        }

        throw new FileNotFoundException("No configuration file was found. Pass a path explicitly or place config.toml in the working directory.");
    }

    private sealed record HostOptions(string? ConfigPathArg, HostServiceMode ServiceMode);
}
