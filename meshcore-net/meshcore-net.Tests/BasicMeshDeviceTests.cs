using MeshCoreNet;

namespace meshcore_net.Tests;

public class BasicMeshDeviceTests
{
    [Fact]
    public async Task BasicMeshAddsValidAdvertToIdentityStore()
    {
        var receiverKey = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var senderKey = new MeshEd25519PrivateKey(Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());
        var receiver = new SelfIdentity(receiverKey, "Receiver");
        var sender = new SelfIdentity(senderKey, "Sender");
        var store = new IdentityStore();
        var device = new BasicMeshDevice("receiver", "test", receiver, store);
        await device.StartAsync(CancellationToken.None, new MeshDispatcher());

        var advert = new MeshAdvertOutgoing(sender, flood: true);
        await device.HandleFrameAsync(new RadioFrame(advert.ToWire()), CancellationToken.None);

        var found = Assert.IsType<MeshIdentity>(store.FindByPublicKey(sender.PublicKey));
        Assert.Equal("Sender", found.Name);
        Assert.NotNull(found.SharedSecret);
    }

    [Fact]
    public async Task BasicMeshRecordsReceiveStats()
    {
        var receiver = new SelfIdentity(new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()), "Receiver");
        var sender = new SelfIdentity(new MeshEd25519PrivateKey(Enumerable.Range(32, 32).Select(value => (byte)value).ToArray()), "Sender");
        var device = new BasicMeshDevice("receiver", "test", receiver, new IdentityStore());
        await device.StartAsync(CancellationToken.None, new MeshDispatcher());

        await device.HandleFrameAsync(new RadioFrame(new MeshAdvertOutgoing(sender, flood: true).ToWire()), CancellationToken.None);

        Assert.Equal(1, device.Stats["received"]);
        Assert.Equal(1, device.Stats["received.Flood"]);
        Assert.Equal(1, device.Stats["type.Advert"]);
    }
}
