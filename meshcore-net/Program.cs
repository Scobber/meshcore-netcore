using MeshCoreNet;

namespace MeshCoreNet;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
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
