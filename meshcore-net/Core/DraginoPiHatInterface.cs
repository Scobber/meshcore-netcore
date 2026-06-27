namespace MeshCoreNet;

/// <summary>
/// Linux-only wrapper for a Raspberry Pi HAT-based LoRa node.
/// It keeps the existing SX126x transport implementation but selects region-aware defaults
/// that match the common 433/868/915 MHz Dragino-style carrier boards.
/// </summary>
public sealed class DraginoPiHatInterface : LinuxLoRaInterface
{
    public DraginoPiHatInterface(string name, string? region, LoRaOptions? options = null)
        : base(name, BuildOptions(region, options))
    {
        Region = NormalizeRegion(region);
    }

    public string Region { get; }

    private static LoRaOptions BuildOptions(string? region, LoRaOptions? options)
    {
        if (options is not null)
        {
            return options;
        }

        return LoRaHardwarePresets.DraginoLoRaHatV14(region);
    }

    private static string NormalizeRegion(string? region)
    {
        return string.IsNullOrWhiteSpace(region) ? "868" : region.Trim().ToLowerInvariant();
    }
}
