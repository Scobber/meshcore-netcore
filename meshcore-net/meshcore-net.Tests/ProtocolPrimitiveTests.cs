using System.Text;
using MeshCoreNet;

namespace meshcore_net.Tests;

public class ProtocolPrimitiveTests
{
    [Fact]
    public void EncryptAndMacMatchesMeshCoreLayout()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var encrypted = MeshCrypto.EncryptAndMac(key, Encoding.ASCII.GetBytes("hello meshcore"));

        Assert.Equal("35c8b9386051d976b886bf806450a632616a", Convert.ToHexString(encrypted).ToLowerInvariant());

        var decrypted = MeshCrypto.MacAndDecrypt(key, encrypted.AsSpan(0, 2), encrypted.AsSpan(2));
        Assert.Equal("hello meshcore\0\0", Encoding.ASCII.GetString(decrypted));
    }

    [Fact]
    public void AckHashUsesFirstFourSha256Bytes()
    {
        Assert.Equal("ba7816bf", Convert.ToHexString(MeshCrypto.AckHash(Encoding.ASCII.GetBytes("abc"))).ToLowerInvariant());
    }

    [Fact]
    public void GroupTextMessageUsesTimestampTypeMessageAndZeroPadding()
    {
        var message = GroupTextMessage.Create(Encoding.ASCII.GetBytes("Alice: hello"), 123456789);

        Assert.Equal(
            "15cd5b0700416c6963653a2068656c6c6f000000000000000000000000000000",
            Convert.ToHexString(message.MessageData).ToLowerInvariant());
    }

    [Fact]
    public void ChannelEncryptsAndDecryptsGroupMessages()
    {
        var channel = new Channel(ChannelStore.PublicChannelKey, "Public");
        var groupMessage = GroupTextMessage.Create(Encoding.ASCII.GetBytes("Alice: hello"), 123456789);

        var encrypted = channel.Encrypt(groupMessage.MessageData);
        var decrypted = channel.Decrypt(encrypted);

        Assert.NotNull(decrypted);
        Assert.Equal(groupMessage.MessageData, decrypted);
    }
}
