using System.Text;
using MeshCoreNet;

namespace meshcore_net.Tests;

public class CompanionRadioDeviceTests
{
    [Fact]
    public async Task CompanionDeviceRespondsToAppStartAndDeviceQuery()
    {
        var device = NewDevice(out _);
        await device.StartAsync(CancellationToken.None, new MeshDispatcher());

        var selfInfo = await device.ProcessCommandAsync([CompanionRadioProtocol.CmdAppStart], CancellationToken.None);
        var deviceInfo = await device.ProcessCommandAsync([CompanionRadioProtocol.CmdDeviceQuery, 3], CancellationToken.None);

        Assert.NotNull(selfInfo);
        Assert.Equal(CompanionRadioProtocol.RespSelfInfo, selfInfo![0]);
        Assert.NotNull(deviceInfo);
        Assert.Equal(CompanionRadioProtocol.RespDeviceInfo, deviceInfo![0]);
    }

    [Fact]
    public async Task CompanionDeviceGetsAndSetsChannels()
    {
        var device = NewDevice(out _);
        await device.StartAsync(CancellationToken.None, new MeshDispatcher());

        var setFrame = new byte[50];
        setFrame[0] = CompanionRadioProtocol.CmdSetChannel;
        setFrame[1] = 1;
        MeshUtilities.Pad("Test", 32).CopyTo(setFrame.AsSpan(2));
        Enumerable.Range(0, 16).Select(value => (byte)value).ToArray().CopyTo(setFrame.AsSpan(34));

        var setResponse = await device.ProcessCommandAsync(setFrame, CancellationToken.None);
        var getResponse = await device.ProcessCommandAsync([CompanionRadioProtocol.CmdGetChannel, 1], CancellationToken.None);

        Assert.NotNull(setResponse);
        Assert.Equal([CompanionRadioProtocol.RespOk], setResponse!);
        Assert.NotNull(getResponse);
        Assert.Equal(CompanionRadioProtocol.RespChannelInfo, getResponse![0]);
        Assert.Equal(1, getResponse[1]);
        Assert.Equal("Test", Encoding.UTF8.GetString(getResponse[2..34]).TrimEnd('\0'));
    }

    [Fact]
    public async Task CompanionDeviceQueuesIncomingTextForSync()
    {
        var device = NewDevice(out var link);
        var dispatcher = new MeshDispatcher();
        await device.StartAsync(CancellationToken.None, dispatcher);
        var source = AddContactForIncomingText(device);

        var packet = new MeshTextOutgoing(source.SenderSelf, source.ReceiverContact, Encoding.UTF8.GetBytes("hello"), timestamp: 123);
        await device.HandleFrameAsync(new RadioFrame(packet.ToWire(), Snr: 1.25), CancellationToken.None);

        var sync = await device.ProcessCommandAsync([CompanionRadioProtocol.CmdSyncNextMessage], CancellationToken.None);

        Assert.Contains(link.SentFrames, frame => frame.SequenceEqual([CompanionRadioProtocol.PushMessageWaiting]));
        Assert.NotNull(sync);
        Assert.Equal(CompanionRadioProtocol.RespContactMessageRecvV3, sync![0]);
        Assert.Equal(source.SenderSelf.PublicKey[..6], sync[4..10]);
        Assert.Equal("hello", Encoding.UTF8.GetString(sync[16..]));
    }

    private static CompanionRadioDevice NewDevice(out FakeCompanionAppLink link)
    {
        var self = new SelfIdentity(new MeshEd25519PrivateKey(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()), "Companion");
        var channels = ChannelStore.Load(maxChannels: 4);
        link = new FakeCompanionAppLink();
        return new CompanionRadioDevice(self, new IdentityStore(), channels, link, new HardwarePlatform());
    }

    private static (SelfIdentity SenderSelf, MeshIdentity ReceiverContact) AddContactForIncomingText(CompanionRadioDevice device)
    {
        var senderKey = new MeshEd25519PrivateKey(Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());
        var sender = new SelfIdentity(senderKey, "Sender");
        var senderForDevice = new MeshIdentity(new AdvertData(sender.Data), []);
        senderForDevice.CreateSharedSecret(device.Self.PrivateKey);
        device.Identities.AddIdentity(senderForDevice);

        var deviceForSender = new MeshIdentity(new AdvertData(device.Self.Data), []);
        deviceForSender.CreateSharedSecret(senderKey);
        return (sender, deviceForSender);
    }

    private sealed class FakeCompanionAppLink : ICompanionAppLink
    {
        public List<byte[]> SentFrames { get; } = [];
        public Queue<byte[]> ReceiveFrames { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ReceiveFrames.Dequeue());
        }

        public Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            SentFrames.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
