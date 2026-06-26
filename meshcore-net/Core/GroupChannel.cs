using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeshCoreNet;

/// <summary>
/// Plaintext layout for group text before channel encryption.
/// </summary>
public sealed class GroupTextMessage
{
    public const byte TextTypePlain = 0;
    public const byte TextTypeCliData = 1;
    public const byte TextTypeSignedPlain = 2;

    private byte[] _messageData;

    public GroupTextMessage(byte[] messageData)
    {
        if (messageData.Length % 16 != 0)
        {
            throw new InvalidMeshCorePacketException("Group message length must be a multiple of 16 bytes.");
        }

        if (messageData.Length < 5 || messageData[4] != TextTypePlain)
        {
            throw new InvalidMeshCorePacketException($"Unknown group message type: {(messageData.Length < 5 ? 0 : messageData[4])}");
        }

        _messageData = messageData;
    }

    private GroupTextMessage()
    {
        _messageData = new byte[16];
    }

    public static GroupTextMessage Create(byte[] message, uint? timestamp = null, byte messageType = TextTypePlain)
    {
        var groupMessage = new GroupTextMessage
        {
            Timestamp = timestamp ?? MeshUtilities.UniqueUnixTime(),
            MessageType = messageType,
            Message = message
        };
        return groupMessage;
    }

    public byte[] MessageData => _messageData.ToArray();

    public uint Timestamp
    {
        get => BitConverter.ToUInt32(_messageData, 0);
        set => BitConverter.GetBytes(value).CopyTo(_messageData, 0);
    }

    public byte MessageType
    {
        get => _messageData[4];
        set => _messageData[4] = value;
    }

    public byte[] Message
    {
        get
        {
            var data = _messageData[5..];
            var length = data.Length;
            while (length > 0 && data[length - 1] == 0)
            {
                length--;
            }

            return data[..length];
        }
        set
        {
            if (value.Length > 155)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Group text message exceeds the maximum size.");
            }

            // Group plaintext is AES-ECB encrypted, so timestamp/type/message is padded to 16 bytes.
            var length = value.Length + 5;
            var padding = (16 - length % 16) & 15;
            var next = new byte[length + padding];
            _messageData.AsSpan(0, 5).CopyTo(next);
            value.CopyTo(next.AsSpan(5));
            _messageData = next;
        }
    }
}

public sealed class Channel
{
    public Channel(byte[]? key = null, byte[]? name = null)
    {
        var actualName = name ?? [];
        if (actualName.Length == 0 || actualName.All(value => value == 0))
        {
            Name = new byte[32];
            Key = new byte[16];
            IsEmpty = true;
            return;
        }

        if (actualName.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Channel name is too long.");
        }

        if (actualName[0] == (byte)'#' && key is null)
        {
            // Hashtag channels derive their key from the channel name in Python.
            Name = actualName.ToArray();
            Key = SHA256.HashData(Name)[..16];
            return;
        }

        if (key is null || key.Length != 16)
        {
            throw new ArgumentException("Channel key must be 16 bytes for non-hashtag channels.", nameof(key));
        }

        Key = key.ToArray();
        Name = MeshUtilities.Pad(actualName, 32);
    }

    public Channel(byte[]? key, string? name)
        : this(key, name is null ? null : Encoding.UTF8.GetBytes(name))
    {
    }

    public byte[] Key { get; }
    public byte[] Name { get; }
    public bool IsEmpty { get; }
    public string DisplayName => Encoding.UTF8.GetString(Name).TrimEnd('\0');
    public byte KeyHash => SHA256.HashData(Key)[0];

    public byte[]? Decrypt(byte[] message)
    {
        if (message.Length < 19)
        {
            throw new InvalidMeshCorePacketException("Channel message payload is too short.");
        }

        // Payload prefix is one-byte key hash, two-byte HMAC, then AES-ECB ciphertext.
        if (message[0] != KeyHash)
        {
            return null;
        }

        var encrypted = message.AsSpan(3);
        var hmac = HMACSHA256.HashData(Key, encrypted);
        if (!CryptographicOperations.FixedTimeEquals(hmac.AsSpan(0, 2), message.AsSpan(1, 2)))
        {
            return null;
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = Key;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted.ToArray(), 0, encrypted.Length);
    }

    public byte[] Encrypt(byte[] message)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = Key;
        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(message, 0, message.Length);
        var hmac = HMACSHA256.HashData(Key, encrypted);

        var result = new byte[3 + encrypted.Length];
        result[0] = KeyHash;
        hmac.AsSpan(0, 2).CopyTo(result.AsSpan(1, 2));
        encrypted.CopyTo(result.AsSpan(3));
        return result;
    }
}

public static class ChannelStore
{
    // Public channel key copied from Python so default channel lists interoperate.
    public static readonly byte[] PublicChannelKey = Convert.FromHexString("8b3387e9c5cdea6ac9e5edbaa115cd72");

    public static List<Channel> Load(string? filename = null, int maxChannels = 32, bool addPublic = true)
    {
        var channels = new List<Channel>();
        if (filename is not null && File.Exists(filename))
        {
            var document = JsonDocument.Parse(File.ReadAllText(filename));
            if (document.RootElement.TryGetProperty("channels", out var channelMap))
            {
                foreach (var item in channelMap.EnumerateObject())
                {
                    channels.Add(item.Value.ValueKind == JsonValueKind.Null
                        ? new Channel(null, item.Name)
                        : new Channel(Convert.FromHexString(item.Value.GetString()!), item.Name));
                }
            }
        }

        var alreadyHasPublic = channels.Any(channel => !channel.IsEmpty && channel.Key.SequenceEqual(PublicChannelKey));
        while (channels.Count < maxChannels)
        {
            channels.Add(new Channel());
        }

        if (addPublic && !alreadyHasPublic)
        {
            var index = channels.FindIndex(channel => channel.IsEmpty);
            if (index >= 0)
            {
                channels[index] = new Channel(PublicChannelKey, "Public");
            }
        }

        return channels;
    }

    public static void Write(IEnumerable<Channel> channels, string filename)
    {
        var channelMap = new Dictionary<string, string?>();
        foreach (var channel in channels.Where(channel => !channel.IsEmpty))
        {
            // A null value in channels.json means "derive key from hashtag name".
            if (channel.DisplayName.StartsWith('#') &&
                channel.Key.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(channel.DisplayName))[..16]))
            {
                channelMap[channel.DisplayName] = null;
            }
            else
            {
                channelMap[channel.DisplayName] = Convert.ToHexString(channel.Key).ToLowerInvariant();
            }
        }

        var json = JsonSerializer.Serialize(new { channels = channelMap }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filename, json + Environment.NewLine);
    }
}
