using System.Text;
using MeshCoreNet;

namespace meshcore_net.Tests;

public class Ed25519Tests
{
    private static readonly byte[] Seed = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] Message = Encoding.ASCII.GetBytes("meshcore");

    [Fact]
    public void SeedKeyMatchesPythonPublicKeyAndSignature()
    {
        var key = new MeshEd25519PrivateKey(Seed);

        Assert.Equal(
            "03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8",
            Convert.ToHexString(key.PublicKey).ToLowerInvariant());
        Assert.Equal(
            "3d94eea49c580aef816935762be049559d6d1440dede12e6a125f1841fff8e6fa9d71862a3e5746b571be3d187b0041046f52ebd850c7cbd5fde8ee38473b649",
            Convert.ToHexString(key.MeshCorePrivateKey).ToLowerInvariant());
        Assert.Equal(
            "be7393421a4140c21bdb8a1b604cea39eb596c6b8731b27cbda10483affc549fcc3ab2599c24b64575740befdebaeb8eaa18830a992a4ad9e9688b818b22cc02",
            Convert.ToHexString(key.Sign(Message)).ToLowerInvariant());
    }

    [Fact]
    public void MeshCorePrivateKeyImportsWithoutSeed()
    {
        var seedKey = new MeshEd25519PrivateKey(Seed);
        var meshKey = new MeshEd25519PrivateKey(seedKey.MeshCorePrivateKey);

        Assert.Equal(seedKey.PublicKey, meshKey.PublicKey);
        Assert.Equal(seedKey.Sign(Message), meshKey.Sign(Message));
    }

    [Fact]
    public void VerifiesSignaturesAndSharedSecretMatchesPython()
    {
        var key = new MeshEd25519PrivateKey(Seed);
        var signature = key.Sign(Message);

        Assert.True(MeshEd25519PrivateKey.Verify(key.PublicKey, Message, signature));
        Assert.Equal(
            "fb229314c2da59093b0b5bed01962399a7d91f4d38b59b5817a12b714c208d16",
            Convert.ToHexString(key.SharedSecret(key.PublicKey)).ToLowerInvariant());
    }
}
