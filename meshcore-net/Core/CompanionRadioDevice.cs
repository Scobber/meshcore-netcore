using System.Collections.Concurrent;
using System.Text;

namespace MeshCoreNet;

/// <summary>
/// Mesh device that emulates the Python companion radio for a TCP or serial app client.
/// </summary>
public sealed class CompanionRadioDevice : BasicMeshDevice
{
    private readonly ICompanionAppLink _appLink;
    private readonly HardwarePlatform _hardware;
    // App sync is intentionally in-memory, matching Python's uncollected companion message queue.
    private readonly ConcurrentQueue<MeshIncomingPacket> _messageQueue = new();
    private readonly Dictionary<string, DateTimeOffset> _messageTimes = new(StringComparer.Ordinal);
    private byte[]? _pendingLogin;
    private byte[]? _pendingStatus;

    public CompanionRadioDevice(
        SelfIdentity self,
        IdentityStore identities,
        List<Channel> channels,
        ICompanionAppLink appLink,
        HardwarePlatform hardware)
        : base("Companion radio", "companion", self, identities, channels)
    {
        MutableChannels = channels;
        _appLink = appLink;
        _hardware = hardware;
    }

    public List<Channel> MutableChannels { get; }

    public override async Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher)
    {
        await base.StartAsync(cancellationToken, dispatcher).ConfigureAwait(false);
        await _appLink.StartAsync(cancellationToken).ConfigureAwait(false);
        _ = Task.Run(() => AppLoopAsync(cancellationToken), cancellationToken);
    }

    public override async Task RxRawAsync(MeshIncomingPacket packet, CancellationToken cancellationToken)
    {
        // Push every valid raw mesh packet to the app log stream before higher-level handling.
        var snr = packet.Snr == 0 ? (byte)0 : (byte)((int)(packet.Snr * 4) & 0xff);
        var rssi = packet.Rssi == 0 ? (byte)0 : (byte)((int)packet.Rssi & 0xff);
        await _appLink.SendFrameAsync([CompanionRadioProtocol.PushLogRxData, snr, rssi, .. packet.ToWire()], cancellationToken).ConfigureAwait(false);
    }

    public override async Task RxAdvertAsync(MeshAdvertPacket packet, CancellationToken cancellationToken)
    {
        await base.RxAdvertAsync(packet, cancellationToken).ConfigureAwait(false);
        await _appLink.SendFrameAsync([CompanionRadioProtocol.PushAdvert, .. packet.Advert.PublicKey], cancellationToken).ConfigureAwait(false);
    }

    public override async Task RxTextAsync(MeshTextPacket packet, CancellationToken cancellationToken)
    {
        if (packet.TextType == TextMessageType.SignedPlain && packet.Source is not null && packet.Timestamp > packet.Source.LastMessageTime)
        {
            packet.Source.LastMessageTime = packet.Timestamp;
            Identities.AddIdentity(packet.Source);
        }

        _messageQueue.Enqueue(packet);
        await PushMessageWaitingAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task RxGroupTextAsync(MeshGroupTextPacket packet, CancellationToken cancellationToken)
    {
        _messageQueue.Enqueue(packet);
        await PushMessageWaitingAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task RxTraceAsync(MeshTracePacket packet, CancellationToken cancellationToken)
    {
        if (packet.TracePath.Length != packet.PathLength)
        {
            return;
        }

        var msg = new List<byte>
        {
            CompanionRadioProtocol.PushTraceData,
            0,
            (byte)packet.PathLength,
            packet.Flags
        };
        msg.AddRange(packet.Tag);
        msg.AddRange(packet.Auth);
        msg.AddRange(packet.TracePath);
        msg.AddRange(packet.Path);
        msg.Add((byte)((int)(packet.Snr * 4) & 0xff));
        await _appLink.SendFrameAsync(msg.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public override async Task RxResponseAsync(MeshSrcDestPacket packet, CancellationToken cancellationToken)
    {
        var response = packet switch
        {
            MeshResponsePacket responsePacket => responsePacket.Response,
            MeshPathPacket pathPacket => pathPacket.Response,
            _ => null
        };

        if (response is null || packet.Source is null || response.Length < 5)
        {
            return;
        }

        // Login/status response packets are both generic RESPONSE packets, so correlate by pending destination.
        if (_pendingLogin is not null && packet.Source.PublicKey.SequenceEqual(_pendingLogin))
        {
            _pendingLogin = null;
            if (response[4] == MeshResponseTypes.ServerLoginOk)
            {
                await _appLink.SendFrameAsync(
                    [CompanionRadioProtocol.PushLoginSuccess, response.Length > 6 ? response[6] : (byte)0, .. packet.Source.PublicKey[..6]],
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (_pendingStatus is not null && packet.Source.PublicKey.SequenceEqual(_pendingStatus))
        {
            _pendingStatus = null;
            await _appLink.SendFrameAsync(
                [CompanionRadioProtocol.PushStatusResponse, 0, .. packet.Source.PublicKey[..6], .. response[4..]],
                cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task RxAckAsync(byte[] ackHash)
    {
        await base.RxAckAsync(ackHash).ConfigureAwait(false);
        var key = Convert.ToHexString(ackHash);
        if (!_messageTimes.TryGetValue(key, out var sentAt))
        {
            return;
        }

        var rtt = (uint)Math.Max(0, (DateTimeOffset.UtcNow - sentAt).TotalMilliseconds);
        _messageTimes.Remove(key);
        await _appLink.SendFrameAsync([CompanionRadioProtocol.PushSendConfirmed, .. ackHash, .. BitConverter.GetBytes(rtt)], CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<byte[]?> ProcessCommandAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (frame.Length == 0)
        {
            return null;
        }

        // Commands that need to stream multiple frames return null after writing directly to _appLink.
        return frame[0] switch
        {
            CompanionRadioProtocol.CmdAppStart => CompanionRadioProtocol.SelfInfo(Self, Dispatcher?.GetRadioConfig() ?? RadioConfig.Empty),
            CompanionRadioProtocol.CmdDeviceQuery => CompanionRadioProtocol.DeviceInfo(MutableChannels.Count),
            CompanionRadioProtocol.CmdGetDeviceTime => CompanionRadioProtocol.CurrentTime(),
            CompanionRadioProtocol.CmdSetDeviceTime => CompanionRadioProtocol.Ok(),
            CompanionRadioProtocol.CmdGetContacts => await SendContactFramesAsync(cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdSendSelfAdvert => await SendSelfAdvertCommandAsync(frame, cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdSetAdvertName => SetAdvertName(frame[1..]),
            CompanionRadioProtocol.CmdSetAdvertLatLon => SetAdvertLatLon(frame),
            CompanionRadioProtocol.CmdAddUpdateContact => AddUpdateContact(frame[1..]),
            CompanionRadioProtocol.CmdResetPath => ResetPath(frame[1..]),
            CompanionRadioProtocol.CmdRemoveContact => RemoveContact(frame[1..]),
            CompanionRadioProtocol.CmdShareContact => await ShareContactAsync(frame[1..], cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdGetBatteryVoltage => CompanionRadioProtocol.BatteryVoltage(_hardware.BatteryMillivolts()),
            CompanionRadioProtocol.CmdGetContactByKey => GetContactByKey(frame[1..]),
            CompanionRadioProtocol.CmdGetChannel => GetChannel(frame),
            CompanionRadioProtocol.CmdSetChannel => SetChannel(frame),
            CompanionRadioProtocol.CmdSendTextMessage => await SendTextCommandAsync(frame, cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdSendChannelTextMessage => await SendChannelTextCommandAsync(frame, cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdSyncNextMessage => SyncNextMessage(),
            CompanionRadioProtocol.CmdSendLogin => await SendLoginCommandAsync(frame, cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdSendStatusReq => await SendStatusRequestCommandAsync(frame, cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdSendTracePath => await SendTraceCommandAsync(frame, cancellationToken).ConfigureAwait(false),
            CompanionRadioProtocol.CmdGetAdvertPath => GetAdvertPath(frame),
            CompanionRadioProtocol.CmdSetRadioParams or CompanionRadioProtocol.CmdSetRadioTxPower or CompanionRadioProtocol.CmdSetOtherParams => CompanionRadioProtocol.Ok(),
            _ => CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound)
        };
    }

    private async Task AppLoopAsync(CancellationToken cancellationToken)
    {
        // The app link is request/response; unsolicited pushes happen from Rx* hooks concurrently.
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await _appLink.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            var response = await ProcessCommandAsync(frame, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await _appLink.SendFrameAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PushMessageWaitingAsync(CancellationToken cancellationToken)
    {
        await _appLink.SendFrameAsync([CompanionRadioProtocol.PushMessageWaiting], cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> SendContactFramesAsync(CancellationToken cancellationToken)
    {
        foreach (var response in CompanionRadioProtocol.Contacts(Identities.GetAll()))
        {
            await _appLink.SendFrameAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<byte[]> SendSelfAdvertCommandAsync(byte[] frame, CancellationToken cancellationToken)
    {
        var flood = frame.Length == 2 && frame[1] == 1;
        await TransmitPacketAsync(new MeshAdvertOutgoing(Self, flood), cancellationToken).ConfigureAwait(false);
        return CompanionRadioProtocol.Ok();
    }

    private byte[] SetAdvertName(byte[] name)
    {
        try
        {
            // Name/lat-lon changes affect future adverts only; Python does not persist them here.
            Self.Name = name;
            return CompanionRadioProtocol.Ok();
        }
        catch
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrIllegalArgument);
        }
    }

    private byte[] SetAdvertLatLon(byte[] frame)
    {
        if (frame.Length < 9)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrIllegalArgument);
        }

        try
        {
            Self.LatLon = MeshUtilities.ValidateLatLon(BitConverter.ToInt32(frame, 1) / 1_000_000.0, BitConverter.ToInt32(frame, 5) / 1_000_000.0);
            return CompanionRadioProtocol.Ok();
        }
        catch
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrIllegalArgument);
        }
    }

    private byte[] AddUpdateContact(byte[] contactData)
    {
        if (contactData.Length < 35)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrIllegalArgument);
        }

        var publicKey = contactData[..32];
        var contact = Identities.FindByPublicKey(publicKey);
        if (contact is null)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        var pathLength = contactData[34];
        if (contactData.Length < 35 + pathLength)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrIllegalArgument);
        }

        contact.Path = contactData[35..(35 + pathLength)];
        Identities.AddIdentity(contact);
        return CompanionRadioProtocol.Ok();
    }

    private byte[] ResetPath(byte[] publicKey)
    {
        var contact = Identities.FindByPublicKey(publicKey);
        if (contact is null)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        contact.Path = null;
        Identities.AddIdentity(contact);
        return CompanionRadioProtocol.Ok();
    }

    private byte[] RemoveContact(byte[] publicKey)
    {
        return Identities.DeleteIdentity(publicKey)
            ? CompanionRadioProtocol.Ok()
            : CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
    }

    private async Task<byte[]> ShareContactAsync(byte[] publicKey, CancellationToken cancellationToken)
    {
        if (Identities.FindByPublicKey(publicKey) is not MeshIdentity contact)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        await TransmitPacketAsync(new MeshPacket(MeshPacketType.Advert, MeshPacketRoute.Flood, contact.Advert.Data), cancellationToken).ConfigureAwait(false);
        return CompanionRadioProtocol.Ok();
    }

    private byte[] GetContactByKey(byte[] publicKey)
    {
        return Identities.FindByPublicKey(publicKey) is MeshIdentity contact
            ? CompanionRadioProtocol.ContactFrame(CompanionRadioProtocol.RespContact, contact)
            : CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
    }

    private byte[] GetChannel(byte[] frame)
    {
        if (frame.Length < 2 || frame[1] >= MutableChannels.Count)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        return CompanionRadioProtocol.ChannelInfo(frame[1], MutableChannels[frame[1]]);
    }

    private byte[] SetChannel(byte[] frame)
    {
        if (frame.Length != 50)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrUnsupportedCommand);
        }

        var index = frame[1];
        if (index >= MutableChannels.Count)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        // Channel updates are app-side runtime changes; persistence stays aligned with Python behavior.
        MutableChannels[index] = new Channel(frame[34..50], frame[2..34]);
        return CompanionRadioProtocol.Ok();
    }

    private async Task<byte[]> SendTextCommandAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (frame.Length < 13)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrIllegalArgument);
        }

        var textType = (TextMessageType)frame[1];
        var attempt = frame[2];
        var timestamp = BitConverter.ToUInt32(frame, 3);
        var recipient = Identities.FindByPublicKey(frame[7..13]);
        if (recipient is null)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        var packet = new MeshTextOutgoing(Self, recipient, frame[13..], textType, attempt, timestamp);
        var ackHash = packet.MessageAckHash();
        // Store send time by ACK hash so PUSH_SEND_CONFIRMED can include observed RTT.
        _messageTimes[Convert.ToHexString(ackHash)] = DateTimeOffset.UtcNow;
        await TransmitPacketAsync(packet, cancellationToken, SendTextDuplicateCallback).ConfigureAwait(false);
        return CompanionRadioProtocol.SentResponse(packet, ackHash);
    }

    private async Task<byte[]> SendChannelTextCommandAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (frame.Length < 7)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrIllegalArgument);
        }

        var channelIndex = frame[2];
        if (channelIndex >= MutableChannels.Count)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        var message = (Self.Name ?? []).Concat([(byte)':', (byte)' ']).Concat(frame[7..]).ToArray();
        if (message.Length > MeshCoreLimits.MaxTextMessage)
        {
            message = message[..MeshCoreLimits.MaxTextMessage];
        }

        var packet = new MeshGroupTextOutgoing(MutableChannels[channelIndex], message, BitConverter.ToUInt32(frame, 3), frame[1]);
        await TransmitPacketAsync(packet, cancellationToken, SendTextDuplicateCallback).ConfigureAwait(false);
        return CompanionRadioProtocol.Ok();
    }

    private async Task<byte[]> SendLoginCommandAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (frame.Length < 33)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        var destination = Identities.FindByPublicKey(frame[1..33]);
        if (destination is null)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        // Room logins carry LastMessageTime to let the room server sync missed messages.
        var since = destination is MeshIdentity { Advert.Type: MeshAdvertType.Room } ? destination.LastMessageTime : (uint?)null;
        var packet = new MeshAnonReqOutgoing(Self, destination, frame[33..], since);
        _pendingLogin = destination.PublicKey;
        await TransmitPacketAsync(packet, cancellationToken).ConfigureAwait(false);
        return CompanionRadioProtocol.SentResponse(packet, destination.PublicKey[..4]);
    }

    private async Task<byte[]> SendStatusRequestCommandAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (frame.Length < 33)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        var destination = Identities.FindByPublicKey(frame[1..33]);
        if (destination is null)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        var requestData = new byte[8];
        Random.Shared.NextBytes(requestData.AsSpan(4));
        var packet = new MeshReqOutgoing(Self, destination, MeshRequestType.GetStatus, requestData);
        _pendingStatus = destination.PublicKey;
        await TransmitPacketAsync(packet, cancellationToken).ConfigureAwait(false);
        return CompanionRadioProtocol.SentResponse(packet, BitConverter.GetBytes(packet.Timestamp));
    }

    private async Task<byte[]> SendTraceCommandAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (frame.Length < 10)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrUnsupportedCommand);
        }

        var packet = new MeshTraceOutgoing(frame[10..], frame[1..5], frame[5..9], frame[9]);
        await TransmitPacketAsync(packet, cancellationToken).ConfigureAwait(false);
        return CompanionRadioProtocol.SentResponse(packet, frame[1..5]);
    }

    private byte[] GetAdvertPath(byte[] frame)
    {
        if (frame.Length < 3 || Identities.FindByPublicKey(frame[2..]) is not MeshIdentity contact || contact.AdvertPath is null)
        {
            return CompanionRadioProtocol.Error(CompanionRadioProtocol.ErrNotFound);
        }

        return [CompanionRadioProtocol.RespAdvertPath, .. BitConverter.GetBytes((uint)contact.ReceivedAt.ToUnixTimeSeconds()), (byte)contact.AdvertPath.Length, .. contact.AdvertPath];
    }

    private byte[] SyncNextMessage()
    {
        // The app drains one queued contact/channel message per CMD_SYNC_NEXT_MESSAGE.
        if (!_messageQueue.TryDequeue(out var packet))
        {
            return [CompanionRadioProtocol.RespNoMoreMessages];
        }

        var snr = packet.Snr == 0 ? (byte)0 : (byte)((int)(packet.Snr * 4) & 0xff);
        if (packet is MeshTextPacket text && text.Source is not null)
        {
            return [
                CompanionRadioProtocol.RespContactMessageRecvV3,
                snr,
                0,
                0,
                .. text.Source.PublicKey[..6],
                (byte)(text.IsFlood ? text.PathLength : 0xff),
                (byte)text.TextType,
                .. BitConverter.GetBytes(text.Timestamp),
                .. text.Text
            ];
        }

        if (packet is MeshGroupTextPacket group && group.Channel is not null && group.Message is not null)
        {
            var index = Math.Max(0, MutableChannels.IndexOf(group.Channel));
            return [
                CompanionRadioProtocol.RespChannelMessageRecvV3,
                snr,
                0,
                0,
                (byte)index,
                (byte)(group.IsFlood ? group.PathLength : 0xff),
                0,
                .. BitConverter.GetBytes(group.Message.Timestamp),
                .. group.Message.Message
            ];
        }

        return [CompanionRadioProtocol.RespNoMoreMessages];
    }

    private void SendTextDuplicateCallback(byte[] hash, int duplicateCount)
    {
        _ = hash;
        _ = duplicateCount;
    }
}
