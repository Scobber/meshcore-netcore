using System.Text;
using MeshCoreNet;

namespace meshcore_net.Tests;

public class TypedPacketTests
{
    [Fact]
    public void TextOutgoingDecryptsForDestination()
    {
        var (alice, bob, aliceForBob, bobForAlice) = CreatePair();
        var text = new MeshTextOutgoing(alice, bobForAlice, Encoding.UTF8.GetBytes("hello"), timestamp: 1234);

        var store = new IdentityStore();
        store.AddIdentity(aliceForBob);

        var incoming = Assert.IsType<MeshTextPacket>(MeshPacketConverter.ConvertIncoming(text.ToWire(), bob, store));

        Assert.True(incoming.Decrypted);
        Assert.Equal("hello", Encoding.UTF8.GetString(incoming.Text));
        Assert.Equal((uint)1234, incoming.Timestamp);
        Assert.Equal(text.MessageAckHash(), incoming.MessageAckHash());
    }

    [Fact]
    public void AckPacketParsesExpectedHash()
    {
        var packet = new MeshPacket(MeshPacketType.Ack, MeshPacketRoute.Direct, Convert.FromHexString("01020304"), []);

        var ack = Assert.IsType<MeshAckPacket>(MeshPacketConverter.ConvertIncoming(packet.ToWire(), DummySelf(), new IdentityStore()));

        Assert.Equal(Convert.FromHexString("01020304"), ack.AckHash);
    }

    [Fact]
    public void GroupTextOutgoingConvertsThroughKnownChannel()
    {
        var channel = new Channel(ChannelStore.PublicChannelKey, "Public");
        var outgoing = new MeshGroupTextOutgoing(channel, Encoding.UTF8.GetBytes("Alice: hello"), 123456789);

        var incoming = Assert.IsType<MeshGroupTextPacket>(
            MeshPacketConverter.ConvertIncoming(outgoing.ToWire(), DummySelf(), new IdentityStore(), [channel]));

        Assert.True(incoming.Decrypted);
        Assert.Equal("Alice: hello", Encoding.UTF8.GetString(incoming.Message!.Message));
    }

    [Fact]
    public void AnonymousRequestDecryptsWithDestinationIdentity()
    {
        var (alice, room, _, roomForAlice) = CreatePair(MeshAdvertType.Chat, MeshAdvertType.Room);
        var request = new MeshAnonReqOutgoing(alice, roomForAlice, Encoding.UTF8.GetBytes("password"), since: 100);

        var incoming = Assert.IsType<MeshAnonReqPacket>(MeshPacketConverter.ConvertIncoming(request.ToWire(), room, new IdentityStore()));

        Assert.True(incoming.Decrypted);
        Assert.Equal(alice.PublicKey, incoming.SenderPublicKey);
        Assert.Equal((uint)100, incoming.SyncTime);
        Assert.Equal("password", Encoding.UTF8.GetString(incoming.Password!));
    }

    [Fact]
    public void TracePacketRoundTripsPathMetadata()
    {
        var outgoing = new MeshTraceOutgoing([0x10, 0x20], [1, 2, 3, 4], [5, 6, 7, 8], flags: 9);

        var incoming = Assert.IsType<MeshTracePacket>(MeshPacketConverter.ConvertIncoming(outgoing.ToWire(), DummySelf(), new IdentityStore()));

        Assert.Equal([1, 2, 3, 4], incoming.Tag);
        Assert.Equal([5, 6, 7, 8], incoming.Auth);
        Assert.Equal(9, incoming.Flags);
        Assert.Equal([0x10, 0x20], incoming.TracePath);
    }

    private static (SelfIdentity Alice, SelfIdentity Bob, MeshIdentity AliceForBob, MeshIdentity BobForAlice) CreatePair(
        MeshAdvertType aliceType = MeshAdvertType.Chat,
        MeshAdvertType bobType = MeshAdvertType.Chat)
    {
        var aliceKey = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var bobKey = new MeshEd25519PrivateKey(Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());
        var alice = new SelfIdentity(aliceKey, "Alice", deviceType: aliceType);
        var bob = new SelfIdentity(bobKey, "Bob", deviceType: bobType);

        var aliceForBob = new MeshIdentity(new AdvertData(alice.Data), []);
        aliceForBob.CreateSharedSecret(bobKey);
        var bobForAlice = new MeshIdentity(new AdvertData(bob.Data), []);
        bobForAlice.CreateSharedSecret(aliceKey);
        return (alice, bob, aliceForBob, bobForAlice);
    }

    private static SelfIdentity DummySelf()
    {
        return new SelfIdentity(new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()), "Dummy");
    }
}
