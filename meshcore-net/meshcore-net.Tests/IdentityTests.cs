using MeshCoreNet;

namespace meshcore_net.Tests;

public class IdentityTests
{
    [Fact]
    public void SelfIdentityCreatesSignedAdvertData()
    {
        var privateKey = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var self = new SelfIdentity(privateKey, "Test Device", (1.5, -2.25), MeshAdvertType.Room);

        var advert = new AdvertData(self.Data);

        Assert.Equal(privateKey.PublicKey, advert.PublicKey);
        Assert.Equal(MeshAdvertType.Room, advert.Type);
        Assert.True(advert.Flags.HasFlag(MeshAdvertFlags.LatLon));
        Assert.True(advert.Flags.HasFlag(MeshAdvertFlags.Name));
        Assert.Equal((1.5, -2.25), advert.LatLon);
        Assert.Equal("Test Device", advert.Name);
        Assert.True(advert.Validate());
    }

    [Fact]
    public void IdentityStoreFindsByHashNameAndPartialKey()
    {
        var privateKey = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var self = new SelfIdentity(privateKey, "Test Device", deviceType: MeshAdvertType.Chat);
        var identity = new MeshIdentity(new AdvertData(self.Data), [0x10, 0x20]);
        identity.CreateSharedSecret(privateKey);

        var store = new IdentityStore();
        Assert.True(store.AddIdentity(identity));

        Assert.Contains(identity, store.FindByHash(identity.Hash));
        Assert.Same(identity, store.FindByName("Device"));
        Assert.Same(identity, store.FindByPublicKey(identity.PublicKey[..6]));
    }

    [Fact]
    public void FileIdentityStorePersistsPythonContactsFormat()
    {
        var privateKey = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var self = new SelfIdentity(privateKey, "Test Device", deviceType: MeshAdvertType.Chat);
        var identity = new MeshIdentity(new AdvertData(self.Data), [0x10, 0x20], [0x30])
        {
            LastMessageTime = 123,
            Snr = -1.5
        };
        identity.CreateSharedSecret(privateKey);

        var file = Path.Combine(Path.GetTempPath(), $"contacts-{Guid.NewGuid():N}.mesh");
        try
        {
            var store = new FileIdentityStore(file, privateKey);
            Assert.True(store.AddIdentity(identity));

            var loaded = new FileIdentityStore(file, privateKey);
            var roundTrip = Assert.IsType<MeshIdentity>(loaded.FindByPublicKey(identity.PublicKey)!);
            Assert.Equal(identity.PublicKey, roundTrip.PublicKey);
            Assert.Equal(identity.Path, roundTrip.Path);
            Assert.Equal(identity.AdvertPath, roundTrip.AdvertPath);
            Assert.Equal((uint)123, roundTrip.LastMessageTime);
            Assert.Equal(-1.5, roundTrip.Snr);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
