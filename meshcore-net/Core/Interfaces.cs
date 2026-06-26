using System.Text;
using System.Threading.Channels;

namespace MeshCoreNet;

/// <summary>
/// Raw frame delivered by a mesh transport. Device code parses Packet into MeshPacket/typed packets.
/// </summary>
public sealed record RadioFrame(byte[] Packet, double Rssi = 0, double Snr = 0, bool IsInternal = false);

/// <summary>
/// Radio parameters surfaced to the companion app. Non-radio transports return Empty.
/// </summary>
public sealed record RadioConfig(uint FrequencyKhz, uint BandwidthHz, byte SpreadingFactor, byte CodingRate, byte TxPower, byte MaxTxPower)
{
    public static RadioConfig Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public interface IMeshInterface
{
    string Name { get; }
    string Type { get; }
    ChannelReader<RadioFrame> ReceivedFrames { get; }
    Task StartAsync(CancellationToken cancellationToken);
    /// <returns>Transmit airtime in milliseconds, or zero when the transport cannot measure it.</returns>
    ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken);
    Task TransmitAsync(MeshPacket packet, CancellationToken cancellationToken);
    TimeSpan TransmitWait();
    RadioConfig GetRadioConfig();
}

public abstract class MeshInterfaceBase : IMeshInterface
{
    // Interfaces push inbound frames here; the dispatcher owns fan-out to devices.
    private readonly Channel<RadioFrame> _receivedFrames = System.Threading.Channels.Channel.CreateUnbounded<RadioFrame>();

    protected MeshInterfaceBase(string name, string type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; }
    public string Type { get; }
    public ChannelReader<RadioFrame> ReceivedFrames => _receivedFrames.Reader;
    protected ChannelWriter<RadioFrame> ReceivedFrameWriter => _receivedFrames.Writer;

    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Interface '{Name}' ({Type}) is ready.");
        return Task.CompletedTask;
    }

    public abstract ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken);

    public virtual async Task TransmitAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        await TransmitAsync(packet.ToWire(), cancellationToken).ConfigureAwait(false);
    }

    public virtual TimeSpan TransmitWait() => TimeSpan.Zero;

    public virtual RadioConfig GetRadioConfig() => RadioConfig.Empty;
}

public sealed class MockMeshInterface : MeshInterfaceBase
{
    private readonly string? _file;
    private readonly bool _repeat;

    public MockMeshInterface(string name, string? file = null, bool repeat = false) : base(name, "mock")
    {
        _file = file;
        _repeat = repeat;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Mock interface '{Name}' is absorbing traffic without hardware I/O.");
        if (_file is not null)
        {
            _ = Task.Run(() => ReadMockFileAsync(cancellationToken), cancellationToken);
        }

        return Task.CompletedTask;
    }

    public override ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Mock interface '{Name}' discarded {Convert.ToHexString(packetData).ToLowerInvariant()}.");
        return ValueTask.FromResult(0d);
    }

    private async Task ReadMockFileAsync(CancellationToken cancellationToken)
    {
        // Python waits before replaying mock input so the host has time to finish starting devices.
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        do
        {
            foreach (var rawLine in File.ReadLines(_file!))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                await ReceivedFrameWriter.WriteAsync(new RadioFrame(Convert.FromHexString(line)), cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        while (_repeat && !cancellationToken.IsCancellationRequested);
    }
}

public sealed class LoopbackMeshInterface : MeshInterfaceBase
{
    public LoopbackMeshInterface(string name, string type) : base(name, type)
    {
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Loopback interface '{Name}' is using the '{Type}' transport.");
        return Task.CompletedTask;
    }

    public override async ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Loopback interface '{Name}' accepted {Convert.ToHexString(packetData).ToLowerInvariant()}.");
        await ReceivedFrameWriter.WriteAsync(new RadioFrame(packetData), cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

public sealed class CompanionMeshInterface : MeshInterfaceBase
{
    public CompanionMeshInterface(string name) : base(name, "companion")
    {
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Companion interface '{Name}' is listening for client traffic.");
        return Task.CompletedTask;
    }

    public override ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Companion interface '{Name}' forwarded {Convert.ToHexString(packetData).ToLowerInvariant()} to the app bridge.");
        return ValueTask.FromResult(0d);
    }
}
