using MeshCoreNet;
using System.Security.Cryptography;
using System.Text;

namespace MeshCoreNet;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (TryHandleUtilityCommand(args))
        {
            return 0;
        }

        FullStackLogger.Initialize();
        try
        {
            var configPath = ResolveConfigPath(args);
            Console.WriteLine($"Using configuration file: {configPath}");

            var config = SimpleTomlParser.ParseFile(configPath);
            var host = new MeshHost(config, configPath);
            await host.RunAsync(CancellationToken.None);
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

    private static string ResolveConfigPath(string[] args)
    {
        if (args.Length > 0)
        {
            return Path.GetFullPath(args[0]);
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
}
