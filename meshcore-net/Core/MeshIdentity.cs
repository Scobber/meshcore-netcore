using System.Collections.Concurrent;
using System.Text;

namespace MeshCoreNet;

/// <summary>
/// Device type nibble encoded in advert payload byte 0.
/// </summary>
public enum MeshAdvertType : byte
{
    None = 0,
    Chat = 1,
    Repeater = 2,
    Room = 3,
    Sensor = 4
}

[Flags]
public enum MeshAdvertFlags : byte
{
    None = 0x00,
    LatLon = 0x10,
    Battery = 0x20,
    Temperature = 0x40,
    Name = 0x80
}

public sealed class AdvertData
{
    // Advert wire format: public key, timestamp, signature, then typed optional fields.
    public const int PublicKeySize = 32;
    public const int TimestampSize = 4;
    public const int SignatureSize = 64;
    public const int AdvertStart = PublicKeySize + TimestampSize + SignatureSize;
    public const int MaxAdvertDataSize = 32;

    public AdvertData(byte[] data)
    {
        if (data.Length < AdvertStart + 1)
        {
            throw new InvalidMeshCorePacketException("Advert payload is too short.");
        }

        Data = data.ToArray();
        PublicKey = Data[..PublicKeySize];
        Timestamp = BitConverter.ToUInt32(Data, PublicKeySize);
        Signature = Data[(PublicKeySize + TimestampSize)..AdvertStart];

        var advert = Data[AdvertStart..];
        Type = (MeshAdvertType)(advert[0] & 0x0f);
        Flags = (MeshAdvertFlags)(advert[0] & 0xf0);

        var offset = 1;
        if (Flags.HasFlag(MeshAdvertFlags.LatLon))
        {
            var lat = BitConverter.ToInt32(advert, offset);
            var lon = BitConverter.ToInt32(advert, offset + 4);
            LatLon = (lat / 1_000_000.0, lon / 1_000_000.0);
            offset += 8;
        }

        if (Flags.HasFlag(MeshAdvertFlags.Battery))
        {
            Battery = advert[offset..(offset + 2)];
            offset += 2;
        }

        if (Flags.HasFlag(MeshAdvertFlags.Temperature))
        {
            Temperature = advert[offset..(offset + 2)];
            offset += 2;
        }

        Name = Flags.HasFlag(MeshAdvertFlags.Name)
            ? Encoding.UTF8.GetString(advert[offset..])
            : "Unnamed " + Convert.ToHexString(PublicKey[..4]).ToLowerInvariant();
    }

    public byte[] Data { get; }
    public byte[] PublicKey { get; }
    public uint Timestamp { get; }
    public byte[] Signature { get; }
    public MeshAdvertType Type { get; }
    public MeshAdvertFlags Flags { get; }
    public (double Latitude, double Longitude)? LatLon { get; }
    public byte[]? Battery { get; }
    public byte[]? Temperature { get; }
    public string Name { get; }

    public bool Validate(byte[]? publicKey = null)
    {
        // The signature covers pubkey + timestamp + advert fields, not the signature bytes themselves.
        var key = publicKey ?? PublicKey;
        var message = new byte[PublicKeySize + TimestampSize + (Data.Length - AdvertStart)];
        PublicKey.CopyTo(message, 0);
        Data.AsSpan(PublicKeySize, TimestampSize).CopyTo(message.AsSpan(PublicKeySize));
        Data.AsSpan(AdvertStart).CopyTo(message.AsSpan(PublicKeySize + TimestampSize));
        return MeshEd25519PrivateKey.Verify(key, message, Signature);
    }
}

public class MeshDestination
{
    public byte[]? Path { get; set; }
    public byte[]? SharedSecret { get; protected set; }
    public byte[] PublicKey { get; protected init; } = [];
    public bool IsAdmin { get; set; }
    public bool IsWriter { get; set; }
    public double? Rssi { get; set; }
    public double? Snr { get; set; }
    public uint LastMessageTime { get; set; }
    public virtual string Name => "AnonReq: " + Convert.ToHexString(PublicKey).ToLowerInvariant();
    public virtual uint Timestamp => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public byte Hash => PublicKey[0];

    public void CreateSharedSecret(MeshEd25519PrivateKey privateKey)
    {
        // Shared secrets are cached because direct packet decryption tries all contacts with a one-byte hash.
        SharedSecret = privateKey.SharedSecret(PublicKey);
    }
}

public sealed class AnonymousIdentity : MeshDestination
{
    private readonly uint _timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public AnonymousIdentity(byte[] publicKey)
    {
        PublicKey = publicKey.ToArray();
    }

    public override uint Timestamp => _timestamp;
}

public sealed class MeshIdentity : MeshDestination
{
    public MeshIdentity(AdvertData advert, byte[]? path = null, byte[]? advertPath = null)
    {
        Advert = advert;
        PublicKey = advert.PublicKey;
        Path = path?.ToArray();
        AdvertPath = advertPath?.ToArray();
        ReceivedAt = DateTimeOffset.UtcNow;
    }

    public AdvertData Advert { get; }
    public byte[]? AdvertPath { get; set; }
    public DateTimeOffset ReceivedAt { get; }
    public override string Name => Advert.Name;
    public override uint Timestamp => Advert.Timestamp;
    public (double Latitude, double Longitude)? LatLon => Advert.LatLon;
}

public sealed class SelfIdentity
{
    private byte[]? _name;

    public SelfIdentity(
        MeshEd25519PrivateKey privateKey,
        string? name = null,
        (double Latitude, double Longitude)? latLon = null,
        MeshAdvertType deviceType = MeshAdvertType.Chat)
    {
        PrivateKey = privateKey;
        Name = name is null ? null : Encoding.UTF8.GetBytes(name);
        LatLon = latLon;
        DeviceType = deviceType;
    }

    public MeshEd25519PrivateKey PrivateKey { get; }
    public byte[] PublicKey => PrivateKey.PublicKey;
    public byte Hash => PublicKey[0];
    public (double Latitude, double Longitude)? LatLon { get; set; }
    public MeshAdvertType DeviceType { get; }

    public byte[]? Name
    {
        get => _name?.ToArray();
        set
        {
            if (value is not null && value.Length > AdvertData.MaxAdvertDataSize)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Advert names are limited to 32 bytes.");
            }

            _name = value?.ToArray();
        }
    }

    public byte[] Data
    {
        get
        {
            // Build and sign a fresh advert each time so timestamps stay current.
            var timestamp = MeshUtilities.UInt32LittleEndian((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var flags = MeshAdvertFlags.None;
            if (LatLon is not null)
            {
                flags |= MeshAdvertFlags.LatLon;
            }

            if (_name is not null)
            {
                flags |= MeshAdvertFlags.Name;
            }

            var advert = new List<byte> { (byte)((byte)DeviceType | (byte)flags) };
            if (LatLon is not null)
            {
                advert.AddRange(BitConverter.GetBytes((int)(LatLon.Value.Latitude * 1_000_000)));
                advert.AddRange(BitConverter.GetBytes((int)(LatLon.Value.Longitude * 1_000_000)));
            }

            if (_name is not null)
            {
                advert.AddRange(_name);
            }

            var message = new List<byte>();
            message.AddRange(PublicKey);
            message.AddRange(timestamp);
            message.AddRange(advert);
            var signature = PrivateKey.Sign(message.ToArray());

            var data = new List<byte>();
            data.AddRange(PublicKey);
            data.AddRange(timestamp);
            data.AddRange(signature);
            data.AddRange(advert);
            return data.ToArray();
        }
    }
}

public class IdentityStore
{
    // Contacts are bucketed by the one-byte public-key hash used on direct packet payloads.
    private readonly ConcurrentDictionary<byte, List<MeshDestination>> _identities = [];

    public virtual bool AddIdentity(MeshDestination identity)
    {
        var bucket = _identities.GetOrAdd(identity.Hash, _ => []);
        lock (bucket)
        {
            for (var index = 0; index < bucket.Count; index++)
            {
                var current = bucket[index];
                if (!current.PublicKey.SequenceEqual(identity.PublicKey))
                {
                    continue;
                }

                // Replace only when the advert or message sync timestamp is at least as fresh.
                if (current.Timestamp <= identity.Timestamp || current.LastMessageTime <= identity.LastMessageTime)
                {
                    bucket[index] = identity;
                    return true;
                }

                return false;
            }

            bucket.Add(identity);
            return true;
        }
    }

    public virtual bool DeleteIdentity(byte[] publicKey)
    {
        if (!_identities.TryGetValue(publicKey[0], out var bucket))
        {
            return false;
        }

        lock (bucket)
        {
            var index = bucket.FindIndex(identity => identity.PublicKey.SequenceEqual(publicKey));
            if (index < 0)
            {
                return false;
            }

            bucket.RemoveAt(index);
            return true;
        }
    }

    public IReadOnlyList<MeshDestination> GetAll()
    {
        return _identities.Values.SelectMany(bucket =>
        {
            lock (bucket)
            {
                return bucket.ToArray();
            }
        }).ToArray();
    }

    public IReadOnlyList<MeshDestination> FindByHash(byte hash)
    {
        if (!_identities.TryGetValue(hash, out var bucket))
        {
            return [];
        }

        lock (bucket)
        {
            return bucket.ToArray();
        }
    }

    public MeshDestination? FindByName(string partial)
    {
        return GetAll().FirstOrDefault(identity => identity.Name.Contains(partial, StringComparison.Ordinal));
    }

    public MeshDestination? FindByPublicKey(byte[] publicKeyPrefix)
    {
        return GetAll().FirstOrDefault(identity => identity.PublicKey.AsSpan().StartsWith(publicKeyPrefix));
    }
}

public sealed class FileIdentityStore : IdentityStore
{
    private readonly string _filename;

    public FileIdentityStore(string filename, MeshEd25519PrivateKey privateKey)
    {
        _filename = filename;
        if (!File.Exists(filename))
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(filename))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // contacts.mesh line format: advert[/path[/advertPath]][@lastMessage][:snr]
            var snrFields = line.Split(':', 2);
            var sinceFields = snrFields[0].Split('@', 2);
            var fields = sinceFields[0].Split('/');
            var advert = new AdvertData(Convert.FromHexString(fields[0]));
            var path = fields.Length >= 2 && fields[1].Length > 0 ? Convert.FromHexString(fields[1]) : null;
            var advertPath = fields.Length >= 3 && fields[2].Length > 0 ? Convert.FromHexString(fields[2]) : null;
            var identity = new MeshIdentity(advert, path, advertPath);
            identity.CreateSharedSecret(privateKey);
            if (sinceFields.Length >= 2 && uint.TryParse(sinceFields[1], out var lastMessage))
            {
                identity.LastMessageTime = lastMessage;
            }

            if (snrFields.Length >= 2 && double.TryParse(snrFields[1], out var snr))
            {
                identity.Snr = snr;
            }

            base.AddIdentity(identity);
        }
    }

    public override bool AddIdentity(MeshDestination identity)
    {
        var result = base.AddIdentity(identity);
        if (result)
        {
            WriteFile();
        }

        return result;
    }

    public override bool DeleteIdentity(byte[] publicKey)
    {
        var result = base.DeleteIdentity(publicKey);
        if (result)
        {
            WriteFile();
        }

        return result;
    }

    private void WriteFile()
    {
        // Preserve the Python-compatible contacts.mesh format so either implementation can read it.
        using var writer = new StreamWriter(_filename, false, Encoding.UTF8);
        writer.WriteLine("# Stored adverts for contacts");
        foreach (var identity in GetAll().OfType<MeshIdentity>())
        {
            writer.WriteLine("# " + identity.Name);
            writer.Write(Convert.ToHexString(identity.Advert.Data).ToLowerInvariant());
            if (identity.Path is not null)
            {
                writer.Write('/' + Convert.ToHexString(identity.Path).ToLowerInvariant());
                if (identity.AdvertPath is not null)
                {
                    writer.Write('/' + Convert.ToHexString(identity.AdvertPath).ToLowerInvariant());
                }
            }

            if (identity.LastMessageTime > 0)
            {
                writer.Write('@' + identity.LastMessageTime.ToString());
            }

            if (identity.Snr is not null)
            {
                writer.Write(':' + identity.Snr.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            writer.WriteLine();
        }
    }
}
