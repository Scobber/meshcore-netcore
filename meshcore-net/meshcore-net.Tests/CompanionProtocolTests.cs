using System.Text;
using MeshCoreNet;

namespace meshcore_net.Tests;

public class CompanionProtocolTests
{
    [Fact]
    public void SelfInfoMatchesCompanionProtocolLayout()
    {
        var key = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var self = new SelfIdentity(key, "Companion", (1.25, 2.5), MeshAdvertType.Chat);
        var frame = CompanionRadioProtocol.SelfInfo(self, new RadioConfig(869618, 62500, 8, 8, 22, 27));

        Assert.Equal(CompanionRadioProtocol.RespSelfInfo, frame[0]);
        Assert.Equal((byte)MeshAdvertType.Chat, frame[1]);
        Assert.Equal(22, frame[2]);
        Assert.Equal(27, frame[3]);
        Assert.Equal(key.PublicKey, frame[4..36]);
        Assert.Equal(1_250_000, BitConverter.ToInt32(frame, 36));
        Assert.Equal(2_500_000, BitConverter.ToInt32(frame, 40));
        Assert.Equal(869618u, BitConverter.ToUInt32(frame, 48));
        Assert.Equal(62500u, BitConverter.ToUInt32(frame, 52));
        Assert.Equal(8, frame[56]);
        Assert.Equal(8, frame[57]);
        Assert.Equal("Companion", Encoding.UTF8.GetString(frame[58..]));
    }

    [Fact]
    public void DeviceInfoReportsPythonCompanionMetadata()
    {
        var frame = CompanionRadioProtocol.DeviceInfo(32);

        Assert.Equal(80, frame.Length);
        Assert.Equal(CompanionRadioProtocol.RespDeviceInfo, frame[0]);
        Assert.Equal(CompanionRadioProtocol.FirmwareVersionCode, frame[1]);
        Assert.Equal(255, frame[2]);
        Assert.Equal(32, frame[3]);
        Assert.Equal(123456u, BitConverter.ToUInt32(frame, 4));
    }

    [Fact]
    public void ContactFrameUsesPythonContactRecordShape()
    {
        var key = new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var self = new SelfIdentity(key, "Alice", (1, -1), MeshAdvertType.Chat);
        var contact = new MeshIdentity(new AdvertData(self.Data), [0x01, 0x02]);

        var frame = CompanionRadioProtocol.ContactFrame(CompanionRadioProtocol.RespContact, contact);

        Assert.Equal(148, frame.Length);
        Assert.Equal(CompanionRadioProtocol.RespContact, frame[0]);
        Assert.Equal(contact.PublicKey, frame[1..33]);
        Assert.Equal((byte)MeshAdvertType.Chat, frame[33]);
        Assert.Equal((byte)contact.Advert.Flags, frame[34]);
        Assert.Equal(2, frame[35]);
        Assert.Equal([0x01, 0x02], frame[36..38]);
        Assert.Equal("Alice", Encoding.UTF8.GetString(frame[100..105]));
    }
}
