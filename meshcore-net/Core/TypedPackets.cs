using System.Text;

namespace MeshCoreNet;

// This file is the typed layer above MeshPacket. Keep raw envelope changes in Packet.cs;
// keep payload layout, encryption, and MeshCore command semantics here.

public enum TextMessageType : byte
{
    Plain = 0,
    CliData = 1,
    SignedPlain = 2
}

public enum MeshRequestType : byte
{
    Login = 0x00,
    GetStatus = 0x01,
    KeepAlive = 0x02,
    GetTelemetryData = 0x03,
    GetAverageMinMax = 0x04,
    GetAccessList = 0x05
}

public static class MeshResponseTypes
{
    public const byte ServerLoginOk = 0;
}

public class MeshIncomingPacket : MeshPacket
{
    public MeshIncomingPacket(byte[] packet, double rssi = 0, double snr = 0)
        : base(Parse(packet).Header, Parse(packet).Path, Parse(packet).Payload)
    {
        Rssi = rssi;
        Snr = snr;
    }

    protected MeshIncomingPacket(MeshPacket packet, double rssi = 0, double snr = 0)
        : base(packet.Header, packet.Path, packet.Payload)
    {
        Rssi = rssi;
        Snr = snr;
    }

    public double Rssi { get; init; }
    public double Snr { get; init; }
    public bool Repeat { get; set; } = true;
}

public static class MeshPacketConverter
{
    public static MeshIncomingPacket ConvertIncoming(
        byte[] packet,
        SelfIdentity self,
        IdentityStore identities,
        IReadOnlyList<Channel>? channels = null,
        double rssi = 0,
        double snr = 0)
    {
        // Conversion is intentionally best-effort. Undecryptable direct packets still surface as typed packets
        // with Decrypted=false so repeaters can forward them without knowing their contents.
        var parsed = MeshPacket.Parse(packet);
        return parsed.Type switch
        {
            MeshPacketType.Req => new MeshReqPacket(parsed, self, identities, rssi, snr),
            MeshPacketType.Response => new MeshResponsePacket(parsed, self, identities, rssi, snr),
            MeshPacketType.TextMessage => new MeshTextPacket(parsed, self, identities, rssi, snr),
            MeshPacketType.Ack => new MeshAckPacket(parsed, rssi, snr),
            MeshPacketType.Advert => new MeshAdvertPacket(parsed, rssi, snr),
            MeshPacketType.GroupText => new MeshGroupTextPacket(parsed, channels ?? [], rssi, snr),
            MeshPacketType.GroupData => new MeshGroupDataPacket(parsed, channels ?? [], rssi, snr),
            MeshPacketType.AnonymousReq => new MeshAnonReqPacket(parsed, self, rssi, snr),
            MeshPacketType.Path => new MeshPathPacket(parsed, self, identities, rssi, snr),
            MeshPacketType.Trace => new MeshTracePacket(parsed, rssi, snr),
            _ => new MeshUnknownPacket(parsed, rssi, snr)
        };
    }
}

public sealed class MeshUnknownPacket(MeshPacket packet, double rssi = 0, double snr = 0) : MeshIncomingPacket(packet, rssi, snr);

public sealed class MeshAdvertPacket : MeshIncomingPacket
{
    public MeshAdvertPacket(MeshPacket packet, double rssi = 0, double snr = 0)
        : base(packet, rssi, snr)
    {
        if (Payload.Length < 101)
        {
            throw new InvalidMeshCorePacketException("Advert payload is too short.");
        }

        Advert = new AdvertData(Payload);
    }

    public AdvertData Advert { get; }
}

public class MeshSrcDestPacket : MeshIncomingPacket
{
    public MeshSrcDestPacket(MeshPacket packet, SelfIdentity self, IdentityStore identities, double rssi = 0, double snr = 0)
        : base(packet, rssi, snr)
    {
        if (Payload.Length < 20)
        {
            throw new InvalidMeshCorePacketException("Source/destination packet payload is too short.");
        }

        // Direct encrypted payload prefix: destination hash, source hash, two-byte MAC, encrypted plaintext.
        DestinationHash = Payload[0];
        SourceHash = Payload[1];
        Mac = Payload[2..4];
        EncryptedPacketData = Payload[4..];

        if (DestinationHash != self.Hash)
        {
            // Not for this node. Leave it encrypted and repeatable.
            return;
        }

        // Hashes are one byte, so try every known identity with the matching hash until the MAC validates.
        foreach (var identity in identities.FindByHash(SourceHash))
        {
            if (identity.SharedSecret is null)
            {
                continue;
            }

            var data = MeshCrypto.MacAndDecrypt(identity.SharedSecret, Mac, EncryptedPacketData);
            if (data.Length == 0)
            {
                continue;
            }

            PacketData = data;
            Source = identity;
            Repeat = false;
            break;
        }
    }

    public byte DestinationHash { get; }
    public byte SourceHash { get; }
    public byte[] Mac { get; }
    public byte[] EncryptedPacketData { get; }
    public MeshDestination? Source { get; protected set; }
    public byte[]? PacketData { get; protected set; }
    public bool Decrypted => PacketData is not null;
}

public sealed class MeshTextPacket : MeshSrcDestPacket
{
    private readonly byte[] _selfPublicKey;

    public MeshTextPacket(MeshPacket packet, SelfIdentity self, IdentityStore identities, double rssi = 0, double snr = 0)
        : base(packet, self, identities, rssi, snr)
    {
        _selfPublicKey = self.PublicKey;
        if (PacketData is null)
        {
            return;
        }

        // Text plaintext: uint timestamp, flags, UTF-8 bytes, optional "\0 + attempt" retry suffix.
        Timestamp = BitConverter.ToUInt32(PacketData, 0);
        Flags = PacketData[4];
        var text = TrimTrailingZeros(PacketData[5..]);
        if (text.Length > 1 && text[^2] == 0)
        {
            Attempt = text[^1];
            text = text[..^2];
        }
        else
        {
            Attempt = (byte)(Flags & 3);
        }

        Text = text;
    }

    public uint Timestamp { get; }
    public byte Flags { get; }
    public byte Attempt { get; }
    public byte[] Text { get; } = [];
    public TextMessageType TextType => (TextMessageType)(Flags >> 2);

    public byte[] MessageAckHash()
    {
        if (PacketData is null || Source is null)
        {
            return [];
        }

        // Signed text ACKs hash against our public key; plain text ACKs hash against the sender key.
        var ackData = TrimTrailingZeros(PacketData)
            .Concat(TextType == TextMessageType.SignedPlain ? _selfPublicKey : Source.PublicKey)
            .ToArray();
        return MeshCrypto.AckHash(ackData);
    }

    private static byte[] TrimTrailingZeros(byte[] data)
    {
        var length = data.Length;
        while (length > 0 && data[length - 1] == 0)
        {
            length--;
        }

        return data[..length];
    }
}

public sealed class MeshResponsePacket(MeshPacket packet, SelfIdentity self, IdentityStore identities, double rssi = 0, double snr = 0)
    : MeshSrcDestPacket(packet, self, identities, rssi, snr)
{
    public byte[]? Response => PacketData;
}

public sealed class MeshReqPacket : MeshSrcDestPacket
{
    public MeshReqPacket(MeshPacket packet, SelfIdentity self, IdentityStore identities, double rssi = 0, double snr = 0)
        : base(packet, self, identities, rssi, snr)
    {
        if (PacketData is null)
        {
            return;
        }

        Timestamp = BitConverter.ToUInt32(PacketData, 0);
        Request = PacketData[4];
        Data = PacketData[5..];
    }

    public uint Timestamp { get; }
    public byte Request { get; }
    public byte[] Data { get; } = [];
}

public sealed class MeshPathPacket : MeshSrcDestPacket
{
    public MeshPathPacket(MeshPacket packet, SelfIdentity self, IdentityStore identities, double rssi = 0, double snr = 0)
        : base(packet, self, identities, rssi, snr)
    {
        if (PacketData is null)
        {
            return;
        }

        var pathLength = PacketData[0];
        if (PacketData.Length < pathLength + 1)
        {
            throw new InvalidMeshCorePacketException("PATH packet path data is truncated.");
        }

        PathData = PacketData[1..(1 + pathLength)];
        var extra = PacketData[(pathLength + 1)..];
        if (extra.Length == 0 || extra[0] == 0 || extra[0] == 0xff)
        {
            return;
        }

        // Python allows PATH to carry ACK or RESPONSE data, saving a separate packet.
        ExtraType = extra[0];
        if (ExtraType == (byte)MeshPacketType.Ack)
        {
            if (extra.Length < 5)
            {
                throw new InvalidMeshCorePacketException("PATH ACK payload is too short.");
            }

            AckHash = extra[1..5];
        }
        else if (ExtraType == (byte)MeshPacketType.Response)
        {
            if (extra.Length < 6)
            {
                throw new InvalidMeshCorePacketException("PATH response payload is too short.");
            }

            Response = extra[1..];
        }
        else
        {
            throw new InvalidMeshCorePacketException("PATH packet contains unknown extra data.");
        }
    }

    public byte[]? PathData { get; }
    public byte? ExtraType { get; }
    public byte[]? AckHash { get; }
    public byte[]? Response { get; }
}

public sealed class MeshAckPacket : MeshIncomingPacket
{
    public MeshAckPacket(MeshPacket packet, double rssi = 0, double snr = 0)
        : base(packet, rssi, snr)
    {
        if (Payload.Length != 4)
        {
            throw new InvalidMeshCorePacketException("ACK packet payload must be four bytes.");
        }

        AckHash = Payload.ToArray();
    }

    public byte[] AckHash { get; }
}

public class MeshGroupPacket : MeshIncomingPacket
{
    public MeshGroupPacket(MeshPacket packet, IReadOnlyList<Channel> channels, double rssi = 0, double snr = 0)
        : base(packet, rssi, snr)
    {
        if ((Payload.Length - 3) % 16 != 0)
        {
            throw new InvalidMeshCorePacketException("Group packet encrypted data length is invalid.");
        }

        // Group packets do not identify the channel by index; try non-empty keys until one decrypts.
        foreach (var channel in channels.Where(channel => !channel.IsEmpty))
        {
            var plaintext = channel.Decrypt(Payload);
            if (plaintext is null)
            {
                continue;
            }

            Channel = channel;
            Plaintext = plaintext;
            break;
        }
    }

    public Channel? Channel { get; }
    public byte[]? Plaintext { get; }
    public bool Decrypted => Plaintext is not null;
}

public sealed class MeshGroupTextPacket : MeshGroupPacket
{
    public MeshGroupTextPacket(MeshPacket packet, IReadOnlyList<Channel> channels, double rssi = 0, double snr = 0)
        : base(packet, channels, rssi, snr)
    {
        if (Plaintext is not null)
        {
            Message = new GroupTextMessage(Plaintext);
        }
    }

    public GroupTextMessage? Message { get; }
}

public sealed class MeshGroupDataPacket(MeshPacket packet, IReadOnlyList<Channel> channels, double rssi = 0, double snr = 0)
    : MeshGroupPacket(packet, channels, rssi, snr);

public sealed class MeshAnonReqPacket : MeshIncomingPacket
{
    public MeshAnonReqPacket(MeshPacket packet, SelfIdentity self, double rssi = 0, double snr = 0)
        : base(packet, rssi, snr)
    {
        if (Payload.Length < 51)
        {
            throw new InvalidMeshCorePacketException("ANON_REQ packet payload is too short.");
        }

        // ANON_REQ carries the full sender public key so servers can derive a secret before login.
        DestinationHash = Payload[0];
        SenderPublicKey = Payload[1..33];
        Mac = Payload[33..35];
        EncryptedPacketData = Payload[35..];

        if (DestinationHash != self.Hash)
        {
            return;
        }

        SharedSecret = self.PrivateKey.SharedSecret(SenderPublicKey);
        var data = MeshCrypto.MacAndDecrypt(SharedSecret, Mac, EncryptedPacketData);
        if (data.Length == 0)
        {
            return;
        }

        PacketData = data;
        Timestamp = BitConverter.ToUInt32(data, 0);
        if (self.DeviceType == MeshAdvertType.Room)
        {
            // Room login requests include an extra sync timestamp before the password.
            SyncTime = BitConverter.ToUInt32(data, 4);
            Password = TrimTrailingZeros(data[8..]);
        }
        else
        {
            Password = TrimTrailingZeros(data[4..]);
        }

        Repeat = false;
    }

    public byte DestinationHash { get; }
    public byte[] SenderPublicKey { get; }
    public byte[] Mac { get; }
    public byte[] EncryptedPacketData { get; }
    public byte[]? SharedSecret { get; }
    public byte[]? PacketData { get; }
    public uint? Timestamp { get; }
    public uint? SyncTime { get; }
    public byte[]? Password { get; }
    public bool Decrypted => PacketData is not null;

    private static byte[] TrimTrailingZeros(byte[] data)
    {
        var length = data.Length;
        while (length > 0 && data[length - 1] == 0)
        {
            length--;
        }

        return data[..length];
    }
}

public sealed class MeshTracePacket : MeshIncomingPacket
{
    public MeshTracePacket(MeshPacket packet, double rssi = 0, double snr = 0)
        : base(packet, rssi, snr)
    {
        if (Payload.Length < 10 || Payload.Length > 73)
        {
            throw new InvalidMeshCorePacketException("TRACE packet payload length is invalid.");
        }

        Tag = Payload[0..4];
        Auth = Payload[4..8];
        Flags = Payload[8];
        TracePath = Payload[9..];
    }

    public byte[] Tag { get; }
    public byte[] Auth { get; }
    public byte Flags { get; }
    public byte[] TracePath { get; }
}

public sealed class MeshAdvertOutgoing(SelfIdentity identity, bool flood = false)
    : MeshPacket(MeshPacketType.Advert, flood ? MeshPacketRoute.Flood : MeshPacketRoute.Direct, identity.Data, flood ? null : []);

public abstract class MeshSrcDestOutgoing : MeshPacket
{
    protected MeshSrcDestOutgoing(SelfIdentity source, MeshDestination destination, MeshPacketType type)
        : base(type, destination.Path is null ? MeshPacketRoute.Flood : MeshPacketRoute.Direct, BuildPayload(source, destination, type, []), destination.Path)
    {
        Source = source;
        Destination = destination;
    }

    protected MeshSrcDestOutgoing(SelfIdentity source, MeshDestination destination, MeshPacketType type, byte[] plaintext)
        : base(type, destination.Path is null ? MeshPacketRoute.Flood : MeshPacketRoute.Direct, BuildPayload(source, destination, type, plaintext), destination.Path)
    {
        Source = source;
        Destination = destination;
    }

    public SelfIdentity Source { get; }
    public MeshDestination Destination { get; }

    private static byte[] BuildPayload(SelfIdentity source, MeshDestination destination, MeshPacketType type, byte[] plaintext)
    {
        if (destination.SharedSecret is null)
        {
            throw new InvalidOperationException("Destination shared secret has not been calculated.");
        }

        // Outbound direct encrypted packets use the same two-byte hash prefix as inbound parsing.
        var encrypted = MeshCrypto.EncryptAndMac(destination.SharedSecret, plaintext);
        var result = new byte[2 + encrypted.Length];
        result[0] = destination.Hash;
        result[1] = source.Hash;
        encrypted.CopyTo(result.AsSpan(2));
        return result;
    }
}

public sealed class MeshTextOutgoing : MeshSrcDestOutgoing
{
    private readonly byte[] _plaintext;

    public MeshTextOutgoing(
        SelfIdentity source,
        MeshDestination destination,
        byte[] text,
        TextMessageType textType = TextMessageType.Plain,
        byte attempt = 0,
        uint? timestamp = null)
        : base(source, destination, MeshPacketType.TextMessage, BuildPlaintext(text, textType, attempt, timestamp, out var plaintext))
    {
        Text = text.ToArray();
        TextType = textType;
        Attempt = attempt;
        Timestamp = BitConverter.ToUInt32(plaintext, 0);
        _plaintext = plaintext;
    }

    public byte[] Text { get; }
    public TextMessageType TextType { get; }
    public byte Attempt { get; }
    public uint Timestamp { get; }
    public byte Flags => (byte)((Attempt & 3) + ((byte)TextType << 2));

    public byte[] MessageAckHash()
    {
        var ackData = _plaintext.Concat(TextType == TextMessageType.SignedPlain ? Destination.PublicKey : Source.PublicKey).ToArray();
        return MeshCrypto.AckHash(ackData);
    }

    private static byte[] BuildPlaintext(byte[] text, TextMessageType textType, byte attempt, uint? timestamp, out byte[] plaintext)
    {
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(timestamp ?? MeshUtilities.UniqueUnixTime()));
        data.Add((byte)((attempt & 3) + ((byte)textType << 2)));
        data.AddRange(text);
        if (attempt > 3)
        {
            // MeshCore stores retry attempts above 3 as a trailing zero byte plus full attempt count.
            if (data.Count >= MeshCoreLimits.MaxTextMessage - 1)
            {
                data = data[..(MeshCoreLimits.MaxTextMessage - 2)];
            }

            data.Add(0);
            data.Add(attempt);
        }

        plaintext = data.ToArray();
        return plaintext;
    }
}

public sealed class MeshPathOutgoing : MeshSrcDestOutgoing
{
    public MeshPathOutgoing(SelfIdentity source, MeshDestination destination, byte[] returnPath, byte[]? ackHash = null, byte[]? response = null)
        : base(source, destination, MeshPacketType.Path, BuildPlaintext(returnPath, ackHash, response))
    {
        ReturnPath = returnPath.ToArray();
        AckHash = ackHash?.ToArray();
        Response = response?.ToArray();
    }

    public byte[] ReturnPath { get; }
    public byte[]? AckHash { get; }
    public byte[]? Response { get; }

    private static byte[] BuildPlaintext(byte[] returnPath, byte[]? ackHash, byte[]? response)
    {
        var data = new List<byte> { (byte)returnPath.Length };
        data.AddRange(returnPath);
        if (ackHash is not null)
        {
            data.Add((byte)MeshPacketType.Ack);
            data.AddRange(ackHash);
        }
        else if (response is not null)
        {
            data.Add((byte)MeshPacketType.Response);
            data.AddRange(response);
        }
        else
        {
            // Python fills no-extra PATH packets with 0xff plus a timestamp-like nonce.
            data.Add(0xff);
            data.AddRange(BitConverter.GetBytes(MeshUtilities.UniqueUnixTime()));
        }

        return data.ToArray();
    }
}

public sealed class MeshReqOutgoing : MeshSrcDestOutgoing
{
    public MeshReqOutgoing(SelfIdentity source, MeshDestination destination, MeshRequestType requestType, byte[] data, uint? timestamp = null)
        : this(source, destination, requestType, data, timestamp ?? MeshUtilities.UniqueUnixTime())
    {
    }

    private MeshReqOutgoing(SelfIdentity source, MeshDestination destination, MeshRequestType requestType, byte[] data, uint timestamp)
        : base(source, destination, MeshPacketType.Req,
            BitConverter.GetBytes(timestamp).Concat([(byte)requestType]).Concat(data).ToArray())
    {
        Timestamp = timestamp;
        RequestType = requestType;
        Data = data.ToArray();
    }

    public uint Timestamp { get; }
    public MeshRequestType RequestType { get; }
    public byte[] Data { get; }
}

public sealed class MeshResponseOutgoing(SelfIdentity source, MeshDestination destination, byte[] data, uint? timestamp = null)
    : MeshSrcDestOutgoing(source, destination, MeshPacketType.Response,
        BitConverter.GetBytes(timestamp ?? MeshUtilities.UniqueUnixTime()).Concat(data).ToArray());

public sealed class MeshAckOutgoing(MeshTextPacket packet, byte[]? path = null)
    : MeshPacket(MeshPacketType.Ack, MeshPacketRoute.Direct, packet.MessageAckHash(), path ?? []);

public class MeshGroupOutgoing(Channel channel, byte[] plaintext, MeshPacketType type = MeshPacketType.GroupText)
    : MeshPacket(type, MeshPacketRoute.Flood, channel.Encrypt(plaintext))
{
    public Channel Channel { get; } = channel;
    public byte[] Plaintext { get; } = plaintext;
}

public sealed class MeshGroupTextOutgoing(Channel channel, byte[] message, uint? timestamp = null, byte messageType = GroupTextMessage.TextTypePlain)
    : MeshGroupOutgoing(channel, GroupTextMessage.Create(message, timestamp, messageType).MessageData);

public sealed class MeshAnonReqOutgoing : MeshPacket
{
    public MeshAnonReqOutgoing(SelfIdentity source, MeshDestination destination, byte[] password, uint? since = null)
        : base(MeshPacketType.AnonymousReq, destination.Path is null ? MeshPacketRoute.Flood : MeshPacketRoute.Direct,
            BuildPayload(source, destination, password, since), destination.Path)
    {
    }

    private static byte[] BuildPayload(SelfIdentity source, MeshDestination destination, byte[] password, uint? since)
    {
        if (destination.SharedSecret is null)
        {
            throw new InvalidOperationException("Destination shared secret has not been calculated.");
        }

        // ANON_REQ plaintext is login timestamp, optional room sync timestamp, then password bytes.
        var plaintext = new List<byte>();
        plaintext.AddRange(BitConverter.GetBytes(MeshUtilities.UniqueUnixTime()));
        if (since is not null)
        {
            plaintext.AddRange(BitConverter.GetBytes(since.Value));
        }

        plaintext.AddRange(password);
        var encrypted = MeshCrypto.EncryptAndMac(destination.SharedSecret, plaintext.ToArray());
        return [(byte)destination.Hash, .. source.PublicKey, .. encrypted];
    }
}

public sealed class MeshTraceOutgoing : MeshPacket
{
    public MeshTraceOutgoing(byte[] path, byte[] tag, byte[]? auth = null, byte flags = 0)
        : base(MeshPacketType.Trace, MeshPacketRoute.Direct, BuildPayload(path, tag, auth ?? new byte[4], flags), [])
    {
    }

    private static byte[] BuildPayload(byte[] path, byte[] tag, byte[] auth, byte flags)
    {
        if (path.Length is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(path), "Trace path must contain between 1 and 64 hops.");
        }

        if (tag.Length != 4 || auth.Length != 4)
        {
            throw new ArgumentException("Trace tag and auth values must be four bytes.");
        }

        return [.. tag, .. auth, flags, .. path];
    }
}
