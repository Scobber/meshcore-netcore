namespace MeshCoreNet;

public interface ISxRadioHal : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken);
    IAsyncEnumerable<RadioFrame> ReceiveAsync(CancellationToken cancellationToken);
}

public interface ISx126xRadio : ISxRadioHal
{
}

public static class SxRadioHalFactory
{
    public static ISxRadioHal Create(LoRaOptions options)
    {
        var chipKind = NormalizeChipKind(options.ChipKind);
        return chipKind switch
        {
            "sx126x" => new Sx126xLinuxHal(options),
            "sx127x" => new Sx127xLinuxHal(options),
            _ => throw new NotSupportedException($"Unsupported SX radio HAL '{options.ChipKind}'. Supported values are 'sx126x' and 'sx127x'.")
        };
    }

    private static string NormalizeChipKind(string? chipKind)
    {
        if (string.IsNullOrWhiteSpace(chipKind))
        {
            return "sx126x";
        }

        return chipKind.Trim().ToLowerInvariant();
    }
}
