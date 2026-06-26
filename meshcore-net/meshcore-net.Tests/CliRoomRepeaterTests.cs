using System.Text;
using MeshCoreNet;

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

    private static CliMeshDevice NewCliDevice(DeviceAccessConfig config)
    {
        return new CliMeshDevice("CLI", "cli", NewSelf(MeshAdvertType.Repeater), new IdentityStore(), new IdentityStore(), new HardwarePlatform(), config);
    }

    private static SelfIdentity NewSelf(MeshAdvertType type)
    {
        return new SelfIdentity(new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()), "Device", deviceType: type);
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
}
