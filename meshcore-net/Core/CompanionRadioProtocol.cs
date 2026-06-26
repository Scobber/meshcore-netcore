using System.Text;

namespace MeshCoreNet;

/// <summary>
/// Constants and frame builders for the MeshCore companion app protocol.
/// </summary>
public static class CompanionRadioProtocol
{
    // These firmware values are compatibility metadata shown to existing apps.
    public const byte FirmwareVersionCode = 5;
    public const string FirmwareBuildDate = "9 May 2025";
    public const string FirmwareVersion = "v1.6.0";

    public const byte CmdAppStart = 1;
    public const byte CmdSendTextMessage = 2;
    public const byte CmdSendChannelTextMessage = 3;
    public const byte CmdGetContacts = 4;
    public const byte CmdGetDeviceTime = 5;
    public const byte CmdSetDeviceTime = 6;
    public const byte CmdSendSelfAdvert = 7;
    public const byte CmdSetAdvertName = 8;
    public const byte CmdAddUpdateContact = 9;
    public const byte CmdSyncNextMessage = 10;
    public const byte CmdSetRadioParams = 11;
    public const byte CmdSetRadioTxPower = 12;
    public const byte CmdResetPath = 13;
    public const byte CmdSetAdvertLatLon = 14;
    public const byte CmdRemoveContact = 15;
    public const byte CmdShareContact = 16;
    public const byte CmdGetBatteryVoltage = 20;
    public const byte CmdDeviceQuery = 22;
    public const byte CmdExportPrivateKey = 23;
    public const byte CmdImportPrivateKey = 24;
    public const byte CmdSendLogin = 26;
    public const byte CmdSendStatusReq = 27;
    public const byte CmdGetContactByKey = 30;
    public const byte CmdGetChannel = 31;
    public const byte CmdSetChannel = 32;
    public const byte CmdSendTracePath = 36;
    public const byte CmdSetOtherParams = 38;
    public const byte CmdGetAdvertPath = 42;

    public const byte RespOk = 0;
    public const byte RespErr = 1;
    public const byte RespContactsStart = 2;
    public const byte RespContact = 3;
    public const byte RespEndOfContacts = 4;
    public const byte RespSelfInfo = 5;
    public const byte RespSent = 6;
    public const byte RespNoMoreMessages = 10;
    public const byte RespBatteryVoltage = 12;
    public const byte RespDeviceInfo = 13;
    public const byte RespContactMessageRecvV3 = 16;
    public const byte RespChannelMessageRecvV3 = 17;
    public const byte RespChannelInfo = 18;
    public const byte RespAdvertPath = 22;
    public const byte RespCurrentTime = 9;

    public const byte PushAdvert = 0x80;
    public const byte PushPathUpdated = 0x81;
    public const byte PushSendConfirmed = 0x82;
    public const byte PushMessageWaiting = 0x83;
    public const byte PushRawData = 0x84;
    public const byte PushLoginSuccess = 0x85;
    public const byte PushLoginFail = 0x86;
    public const byte PushStatusResponse = 0x87;
    public const byte PushLogRxData = 0x88;
    public const byte PushTraceData = 0x89;
    public const byte PushNewAdvert = 0x8A;
    public const byte PushTelemetryResponse = 0x8B;

    public const byte ErrUnsupportedCommand = 1;
    public const byte ErrNotFound = 2;
    public const byte ErrTableFull = 3;
    public const byte ErrBadState = 4;
    public const byte ErrFileIo = 5;
    public const byte ErrIllegalArgument = 6;

    public static byte[] Ok() => [RespOk];

    public static byte[] Error(byte code) => [RespErr, code];

    public static byte[] SentResponse(MeshPacket packet, byte[] tag, uint estimatedRttMs = 10_000)
    {
        if (tag.Length != 4)
        {
            throw new ArgumentException("Sent response tags must be four bytes.", nameof(tag));
        }

        // Sent response layout: response code, flood flag, four-byte app tag, estimated RTT.
        return [RespSent, (byte)(packet.IsFlood ? 1 : 0), .. tag, .. BitConverter.GetBytes(estimatedRttMs)];
    }

    public static byte[] SelfInfo(SelfIdentity self, RadioConfig radioConfig)
    {
        // The app treats this as a packed binary structure; keep field order stable.
        var latLon = self.LatLon;
        var lat = latLon is null ? 0 : (int)(latLon.Value.Latitude * 1_000_000);
        var lon = latLon is null ? 0 : (int)(latLon.Value.Longitude * 1_000_000);
        var name = self.Name ?? [];

        var response = new List<byte>
        {
            RespSelfInfo,
            (byte)self.DeviceType,
            radioConfig.TxPower,
            radioConfig.MaxTxPower
        };
        response.AddRange(self.PublicKey);
        response.AddRange(BitConverter.GetBytes(lat));
        response.AddRange(BitConverter.GetBytes(lon));
        response.AddRange(BitConverter.GetBytes((ushort)0));
        response.Add(0);
        response.Add(0);
        response.AddRange(BitConverter.GetBytes(radioConfig.FrequencyKhz));
        response.AddRange(BitConverter.GetBytes(radioConfig.BandwidthHz));
        response.Add(radioConfig.SpreadingFactor);
        response.Add(radioConfig.CodingRate);
        response.AddRange(name);
        return response.ToArray();
    }

    public static byte[] DeviceInfo(int channelCount)
    {
        // Keep the "Python Companion" label to preserve current app-facing identity.
        var response = new List<byte>
        {
            RespDeviceInfo,
            FirmwareVersionCode,
            255,
            checked((byte)channelCount)
        };
        response.AddRange(BitConverter.GetBytes(123456u));
        response.AddRange(MeshUtilities.Pad(FirmwareBuildDate, 12));
        response.AddRange(MeshUtilities.Pad("Python Companion", 40));
        response.AddRange(MeshUtilities.Pad(FirmwareVersion, 20));
        return response.ToArray();
    }

    public static byte[] BatteryVoltage(ushort millivolts = 0xffff)
    {
        return [RespBatteryVoltage, .. BitConverter.GetBytes(millivolts)];
    }

    public static byte[] CurrentTime(uint? timestamp = null)
    {
        return [RespCurrentTime, .. BitConverter.GetBytes(timestamp ?? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())];
    }

    public static byte[] ContactFrame(byte responseCode, MeshIdentity contact)
    {
        // Contact frames are fixed-width enough for older apps to parse by offsets.
        var path = contact.Path ?? [];
        var latLon = contact.LatLon;
        var lat = latLon is null ? 0 : (int)(latLon.Value.Latitude * 1_000_000);
        var lon = latLon is null ? 0 : (int)(latLon.Value.Longitude * 1_000_000);

        var frame = new List<byte> { responseCode };
        frame.AddRange(contact.PublicKey);
        frame.Add((byte)contact.Advert.Type);
        frame.Add((byte)contact.Advert.Flags);
        frame.Add((byte)path.Length);
        frame.AddRange(MeshUtilities.Pad(path, 64));
        frame.AddRange(MeshUtilities.Pad(Encoding.UTF8.GetBytes(contact.Name), 32));
        frame.AddRange(BitConverter.GetBytes(contact.Timestamp));
        frame.AddRange(BitConverter.GetBytes(lat));
        frame.AddRange(BitConverter.GetBytes(lon));
        frame.AddRange(BitConverter.GetBytes(contact.Timestamp));
        return frame.ToArray();
    }

    public static IReadOnlyList<byte[]> Contacts(IEnumerable<MeshDestination> contacts)
    {
        // CMD_GET_CONTACTS is a multi-frame response: start, zero or more contacts, end.
        var meshContacts = contacts.OfType<MeshIdentity>().ToArray();
        var startFrame = new byte[5];
        startFrame[0] = RespContactsStart;
        BitConverter.GetBytes((uint)meshContacts.Length).CopyTo(startFrame, 1);
        var frames = new List<byte[]> { startFrame };
        uint mostRecent = 0;
        foreach (var contact in meshContacts)
        {
            frames.Add(ContactFrame(RespContact, contact));
            if (contact.Timestamp > mostRecent)
            {
                mostRecent = contact.Timestamp;
            }
        }

        var endFrame = new byte[5];
        endFrame[0] = RespEndOfContacts;
        BitConverter.GetBytes(mostRecent).CopyTo(endFrame, 1);
        frames.Add(endFrame);
        return frames;
    }

    public static byte[] ChannelInfo(byte channelIndex, Channel channel)
    {
        return [RespChannelInfo, channelIndex, .. MeshUtilities.Pad(channel.Name, 32), .. channel.Key];
    }
}
