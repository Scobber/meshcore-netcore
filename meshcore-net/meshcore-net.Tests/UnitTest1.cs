using MeshCoreNet;
using System.Threading.Channels;

namespace meshcore_net.Tests;

public class PacketTests
{
    [Fact]
    public void CanRoundTripPacketPayload()
    {
        var packet = new MeshPacket(MeshPacketType.TextMessage, MeshPacketRoute.Flood, new byte[] { 1, 2, 3, 4 }, new byte[] { 0x10, 0x20 });

        var bytes = packet.ToWire();
        var parsed = MeshPacket.Parse(bytes);

        Assert.Equal(packet.Type, parsed.Type);
        Assert.Equal(packet.Route, parsed.Route);
        Assert.Equal(packet.PathLength, parsed.PathLength);
        Assert.Equal(packet.Payload, parsed.Payload);
    }

    [Fact]
    public async Task DispatcherSuppressesDuplicates()
    {
        var dispatcher = new MeshDispatcher();
        var interfaceStub = new TestInterface();
        dispatcher.RegisterInterface(interfaceStub);

        await dispatcher.StartAsync(CancellationToken.None);

        var packet = MeshPacket.CreateAdvert("hello");
        dispatcher.QueuePacket(packet);
        dispatcher.QueuePacket(packet);

        await Task.Delay(250);
        await dispatcher.StopAsync();

        Assert.Single(interfaceStub.TransmittedPackets);
    }

    [Fact]
    public async Task DispatcherDeliversInboundInterfaceFramesToDevices()
    {
        var dispatcher = new MeshDispatcher();
        var interfaceStub = new TestInterface();
        var device = new TestDevice();
        dispatcher.RegisterInterface(interfaceStub);
        dispatcher.RegisterDevice(device);

        await dispatcher.StartAsync(CancellationToken.None);

        var packet = MeshPacket.CreateAdvert("hello");
        await interfaceStub.InjectAsync(new RadioFrame(packet.ToWire()));

        await Task.Delay(250);
        await dispatcher.StopAsync();

        Assert.Single(device.Frames);
        Assert.Equal(packet.ToWire(), device.Frames[0].Packet);
    }

    [Fact]
    public void GpsParserRecognisesFixesFromTheMt3339Module()
    {
        var sentence = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";

        var parsed = GpsNmeaParser.TryParse(sentence, out var fix);

        Assert.True(parsed);
        Assert.True(fix.IsValid);
        Assert.Equal(48.1173, fix.Latitude, 4);
        Assert.Equal(11.5167, fix.Longitude, 4);
    }

    private sealed class TestInterface : IMeshInterface
    {
        private readonly Channel<RadioFrame> _frames = System.Threading.Channels.Channel.CreateUnbounded<RadioFrame>();

        public string Name => "test";
        public string Type => "mock";
        public ChannelReader<RadioFrame> ReceivedFrames => _frames.Reader;
        public List<MeshPacket> TransmittedPackets { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask InjectAsync(RadioFrame frame) => _frames.Writer.WriteAsync(frame);

        public ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
        {
            TransmittedPackets.Add(MeshPacket.Parse(packetData));
            return ValueTask.FromResult(0d);
        }

        public Task TransmitAsync(MeshPacket packet, CancellationToken cancellationToken)
        {
            TransmittedPackets.Add(packet);
            return Task.CompletedTask;
        }

        public TimeSpan TransmitWait() => TimeSpan.Zero;

        public RadioConfig GetRadioConfig() => RadioConfig.Empty;
    }

    private sealed class TestDevice : IMeshDevice
    {
        public string Name => "device";
        public string Type => "test";
        public List<RadioFrame> Frames { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher) => Task.CompletedTask;

        public Task HandleFrameAsync(RadioFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add(frame);
            return Task.CompletedTask;
        }

        public Task HandlePacketAsync(MeshPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
