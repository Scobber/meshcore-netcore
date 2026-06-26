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

        var frequency = NormalizeRegion(region) switch
        {
            "433" => 433_000_000u,
            "915" => 915_000_000u,
            "868" => 868_000_000u,
            _ => 869_618_000u
        };

        return new LoRaOptions(
            SpiBus: 0,
            ChipSelect: 0,
            ResetPin: 25,
            BusyPin: 24,
            IrqPin: 23,
            TxEnablePin: 4,
            RxEnablePin: -1,
            WakePin: -1,
            Frequency: frequency,
            SpreadingFactor: 8,
            Bandwidth: 62_500,
            CodingRate: 8,
            TxPower: 22,
            AirtimeDutyCycle: 10,
            Dio2RfSwitch: false,
            Dio3Voltage: null,
            Dio3TcxoDelay: null);
    }

    private static string NormalizeRegion(string? region)
    {
        return string.IsNullOrWhiteSpace(region) ? "868" : region.Trim().ToLowerInvariant();
    }
}
