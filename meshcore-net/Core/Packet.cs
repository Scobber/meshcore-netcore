using System.Text;

namespace MeshCoreNet;

/// <summary>
/// MeshCore route bits stored in the low two bits of the packet header.
/// </summary>
public enum MeshPacketRoute : byte
{
    Flood = 0x01,
    Direct = 0x02
}

/// <summary>
/// MeshCore v1 packet type bits stored in header bits 2 through 5.
/// </summary>
public enum MeshPacketType : byte
{
    Req = 0x00,
    Response = 0x01,
    TextMessage = 0x02,
    Ack = 0x03,
    Advert = 0x04,
    GroupText = 0x05,
    GroupData = 0x06,
    AnonymousReq = 0x07,
    Path = 0x08,
    Trace = 0x09,
    RawCustom = 0x0F
}

public class MeshPacket
{
    private readonly List<byte> _path;
    private readonly byte[] _payload;
    private readonly TaskCompletionSource<bool> _sentCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MeshPacket(MeshPacketType type, MeshPacketRoute route, byte[] payload, byte[]? path = null)
    {
        // The Python implementation enforces the LoRa-oriented MeshCore MTU at this envelope level.
        if (payload.Length + 2 + (path?.Length ?? 0) > MeshCoreLimits.MaxPacketPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Packet payload exceeds the MeshCore maximum size.");
        }

        var pathBytes = path ?? Array.Empty<byte>();
        if (pathBytes.Length > MeshCoreLimits.MaxPathSize - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(path), "Packet path exceeds the MeshCore maximum size.");
        }

        _path = [.. pathBytes];
        _payload = payload;
        Header = (byte)(((byte)type << 2) | (byte)route);
    }

    protected MeshPacket(byte header, byte[] path, byte[] payload)
    {
        Header = header;
        _path = [.. path];
        _payload = payload;
    }

    public byte Header { get; private set; }

    public MeshPacketRoute Route => (MeshPacketRoute)(Header & 0x03);
    public MeshPacketType Type => (MeshPacketType)((Header >> 2) & 0x0F);
    public int Version => Header >> 6;
    public byte[] Path => _path.ToArray();
    public IReadOnlyList<byte> PathHops => _path;
    public byte[] Payload => _payload;
    public int PathLength => _path.Count;
    public Task<bool> Sent => _sentCompletion.Task;
    public bool IsFlood => Route == MeshPacketRoute.Flood;
    public bool IsDirect => Route == MeshPacketRoute.Direct;

    public void Flood()
    {
        // Converting to flood deliberately drops any return path bytes.
        _path.Clear();
        Header = (byte)((Header & 0xFC) | (byte)MeshPacketRoute.Flood);
    }

    public void AppendHop(byte hop)
    {
        if (_path.Count >= MeshCoreLimits.MaxPathSize - 1)
        {
            throw new InvalidMeshCorePacketException("Packet path is already at the repeat hop limit.");
        }

        _path.Add(hop);
    }

    public bool PopNextHop(out byte hop)
    {
        // Direct packets consume their path one hop at a time while being repeated.
        if (_path.Count == 0)
        {
            hop = 0;
            return false;
        }

        hop = _path[0];
        _path.RemoveAt(0);
        return true;
    }

    public void MarkSent() => _sentCompletion.TrySetResult(true);

    public void MarkSendCancelled() => _sentCompletion.TrySetCanceled();

    public byte[] ToWire()
    {
        // Wire format is intentionally tiny: header, path length, path bytes, then payload bytes.
        if (Version != 0)
        {
            throw new UnknownMeshCoreVersionException($"Unsupported MeshCore packet version {Version}.");
        }

        var packet = new List<byte> { Header, (byte)PathLength };
        packet.AddRange(Path);
        packet.AddRange(Payload);
        return packet.ToArray();
    }

    public static MeshPacket Parse(byte[] packet)
    {
        if (packet.Length < 2)
        {
            throw new InvalidMeshCorePacketException("Packet is too short to contain a MeshCore header.");
        }

        var header = packet[0];
        var pathLength = packet[1];
        // A malicious or corrupt frame can otherwise make the path slice run into payload bytes.
        if (pathLength > MeshCoreLimits.MaxPathSize - 1 || pathLength > packet.Length - 2)
        {
            throw new InvalidMeshCorePacketException("Packet path length exceeds the available payload.");
        }

        var path = packet.Skip(2).Take(pathLength).ToArray();
        var payload = packet.Skip(2 + pathLength).ToArray();
        return new MeshPacket(header, path, payload);
    }

    public static MeshPacket CreateAdvert(string message, byte[]? path = null)
    {
        return new MeshPacket(MeshPacketType.Advert, MeshPacketRoute.Flood, Encoding.UTF8.GetBytes(message), path);
    }

    public override string ToString()
    {
        return $"{Type} [{Route}] path={PathLength} payload={Payload.Length} bytes";
    }
}

public static class MeshCoreLimits
{
    // These constants mirror the Python protocol limits and should not be changed without fixtures.
    public const int CipherMacSize = 2;
    public const int PathHashSize = 1;
    public const int MaxPacketPayload = 184;
    public const int MaxPathSize = 64;
    public const int MaxTransUnit = 255;
    public const int MaxTextMessage = 171;
}

public class MeshCorePacketException(string message) : Exception(message);

public class InvalidMeshCorePacketException(string message) : MeshCorePacketException(message);

public sealed class UnknownMeshCoreVersionException(string message) : InvalidMeshCorePacketException(message);

public sealed class UnknownMeshCoreTypeException(string message) : InvalidMeshCorePacketException(message);

public sealed class UnknownMeshCoreRoutingException(string message) : InvalidMeshCorePacketException(message);
