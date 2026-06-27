using System.Text;
using MeshCoreNet;
using System.Threading.Channels;

namespace meshcore_net.Tests;

public class CliRoomRepeaterTests
{
    [Fact]
    public void CliDeviceAcceptsAdminPasswordLogin()
    {
        var device = NewCliDevice(new DeviceAccessConfig { AdminPassword = "password", GuestOpen = false });
        var clientKey = new MeshEd25519PrivateKey(Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());

        var login = device.Login(clientKey.PublicKey, Encoding.UTF8.GetBytes("password"));

        Assert.NotNull(login);
        Assert.True(login.IsAdmin);
        Assert.NotNull(login.SharedSecret);
    }

    [Fact]
    public void RoomWriterPasswordGrantsWriteButNotAdmin()
    {
        var self = NewSelf(MeshAdvertType.Room);
        var room = new RoomMeshDevice(self, new IdentityStore(), new IdentityStore(), new HardwarePlatform(),
            new DeviceAccessConfig { WriterPassword = "writer", GuestOpen = false });
        var clientKey = new MeshEd25519PrivateKey(Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());

        var login = room.Login(clientKey.PublicKey, Encoding.UTF8.GetBytes("writer"));

        Assert.NotNull(login);
        Assert.False(login.IsAdmin);
        Assert.True(login.IsWriter);
    }

    [Fact]
    public async Task RoomReadOnlyIgnoresNonWriterText()
    {
        var (client, roomSelf, clientForRoom, roomForClient) = CreatePair();
        var room = new RoomMeshDevice(roomSelf, new IdentityStore(), new IdentityStore(), new HardwarePlatform(),
            new DeviceAccessConfig { ReadOnly = true });
        var store = room.Identities;
        store.AddIdentity(clientForRoom);
        await room.StartAsync(CancellationToken.None, new MeshDispatcher());

        var packet = new MeshTextOutgoing(client, roomForClient, Encoding.UTF8.GetBytes("hello"));
        await room.HandleFrameAsync(new RadioFrame(packet.ToWire()), CancellationToken.None);

        Assert.False(room.Stats.ContainsKey("room.posted"));
    }

    [Fact]
    public async Task CliAdvertCommandTransmitsFloodAdvert()
    {
        var interfaceStub = new TestInterface();
        var dispatcher = new MeshDispatcher();
        dispatcher.RegisterInterface(interfaceStub);
        var device = NewCliDevice(new DeviceAccessConfig());
        dispatcher.RegisterDevice(device);
        await dispatcher.StartAsync(CancellationToken.None);
        await device.StartAsync(CancellationToken.None, dispatcher);

        var response = await device.CliCommandAsync(Encoding.UTF8.GetBytes("advert"), CancellationToken.None);
        await Task.Delay(250);
        await dispatcher.StopAsync();

        Assert.Equal("OK - Advert sent", response);
        var sentAdvert = Assert.Single(interfaceStub.TransmittedPackets);
        Assert.Equal(MeshPacketType.Advert, sentAdvert.Type);
        Assert.True(sentAdvert.IsFlood);
    }

    [Fact]
    public async Task CliDeviceOnlyTracksZeroHopRepeaterAdvertsAsNeighbours()
    {
        var device = NewCliDevice(new DeviceAccessConfig());
        var dispatcher = new MeshDispatcher();
        await device.StartAsync(CancellationToken.None, dispatcher);

        var repeater = NewSelf(MeshAdvertType.Repeater, 32);
        var room = NewSelf(MeshAdvertType.Room, 64);
        var repeaterAdvert = new MeshAdvertOutgoing(repeater).ToWire();
        var roomAdvert = new MeshAdvertOutgoing(room).ToWire();
        var routedRepeater = MeshPacket.Parse(new MeshAdvertOutgoing(NewSelf(MeshAdvertType.Repeater, 96), flood: true).ToWire());
        var repeaterViaHop = new MeshPacket(routedRepeater.Header, [0x55], routedRepeater.Payload).ToWire();

        await device.HandleFrameAsync(new RadioFrame(repeaterAdvert), CancellationToken.None);
        await device.HandleFrameAsync(new RadioFrame(roomAdvert), CancellationToken.None);
        await device.HandleFrameAsync(new RadioFrame(repeaterViaHop), CancellationToken.None);

        var neighbours = device.NeighbourIdentities.GetAll();
        Assert.Single(neighbours);
        Assert.Equal(repeater.PublicKey, neighbours[0].PublicKey);
    }

    private static CliMeshDevice NewCliDevice(DeviceAccessConfig config)
    {
        return new CliMeshDevice("CLI", "cli", NewSelf(MeshAdvertType.Repeater), new IdentityStore(), new IdentityStore(), new HardwarePlatform(), config);
    }

    private static SelfIdentity NewSelf(MeshAdvertType type, int keyStart = 0)
    {
        return new SelfIdentity(new MeshEd25519PrivateKey(Enumerable.Range(keyStart, 32).Select(value => (byte)value).ToArray()), "Device", deviceType: type);
    }

    private static (SelfIdentity Client, SelfIdentity Room, MeshIdentity ClientForRoom, MeshIdentity RoomForClient) CreatePair()
    {
        var clientKey = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var roomKey = new MeshEd25519PrivateKey(Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());
        var client = new SelfIdentity(clientKey, "Client");
        var room = new SelfIdentity(roomKey, "Room", deviceType: MeshAdvertType.Room);
        var clientForRoom = new MeshIdentity(new AdvertData(client.Data), []);
        clientForRoom.CreateSharedSecret(roomKey);
        var roomForClient = new MeshIdentity(new AdvertData(room.Data), []);
        roomForClient.CreateSharedSecret(clientKey);
        return (client, room, clientForRoom, roomForClient);
    }

    private sealed class TestInterface : IMeshInterface
    {
        private readonly Channel<RadioFrame> _frames = System.Threading.Channels.Channel.CreateUnbounded<RadioFrame>();

        public string Name => "test";
        public string Type => "mock";
        public ChannelReader<RadioFrame> ReceivedFrames => _frames.Reader;
        public List<MeshPacket> TransmittedPackets { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
}
