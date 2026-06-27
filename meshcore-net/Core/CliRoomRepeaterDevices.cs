using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MeshCoreNet;

public sealed class HardwarePlatform
{
    // Python returns fake hardware values in this port scope; keep that behavior until real telemetry is added.
    public ushort BatteryMillivolts() => 0xffff;
}

public sealed class DeviceAccessConfig
{
    public string? AdminPassword { get; init; }
    public IReadOnlySet<string> AdminKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool GuestOpen { get; init; } = true;
    public string? GuestPassword { get; init; }
    public IReadOnlySet<string> GuestKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string? WriterPassword { get; init; }
    public IReadOnlySet<string> WriterKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool ReadOnly { get; init; }
    public string? Welcome { get; init; }
    public int AdvertFloodHours { get; init; } = -1;
    public int AdvertDirectMinutes { get; init; } = 0;
}

public class CliMeshDevice : BasicMeshDevice
{
    private const long DirectAdvertSuppressionSeconds = 120;
    private readonly DateTimeOffset _beginTime = DateTimeOffset.UtcNow;
    private readonly HardwarePlatform _hardware;
    private readonly DeviceAccessConfig _config;
    private TimeSpan _floodAdvertInterval = TimeSpan.Zero;
    private long _lastFloodAdvertUnixSeconds;

    public CliMeshDevice(
        string name,
        string type,
        SelfIdentity self,
        IdentityStore loggedInIdentities,
        IdentityStore neighbourIdentities,
        HardwarePlatform hardware,
        DeviceAccessConfig config)
        : base(name, type, self, loggedInIdentities)
    {
        NeighbourIdentities = neighbourIdentities;
        _hardware = hardware;
        _config = config;
    }

    public IdentityStore NeighbourIdentities { get; }

    /// <summary>
    /// All heard nodes regardless of type or hop count. Used for the web-server "seen nodes" view.
    /// </summary>
    public IdentityStore HeardIdentities { get; } = new IdentityStore();

    public override async Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher)
    {
        await base.StartAsync(cancellationToken, dispatcher).ConfigureAwait(false);
        _ = Task.Run(() => FloodAdvertLoopAsync(cancellationToken), CancellationToken.None);
        _ = Task.Run(() => DirectAdvertLoopAsync(cancellationToken), CancellationToken.None);
    }

    public override Task RxAdvertAsync(MeshAdvertPacket packet, CancellationToken cancellationToken)
    {
        var identity = new MeshIdentity(packet.Advert, advertPath: packet.Path)
        {
            Rssi = packet.Rssi,
            Snr = packet.Snr
        };
        HeardIdentities.AddIdentity(identity);

        // NeighbourIdentities tracks only direct-hop repeaters for routing purposes.
        if (packet.Advert.Type == MeshAdvertType.Repeater && packet.PathLength == 0)
        {
            NeighbourIdentities.AddIdentity(identity);
        }

        return Task.CompletedTask;
    }

    public virtual Task<string?> CliCommandAsync(byte[] command, CancellationToken cancellationToken)
    {
        var commandText = Encoding.UTF8.GetString(command).Trim().ToLowerInvariant();
        return commandText switch
        {
            "advert" => SendAdvertCommandAsync(cancellationToken),
            "clock" => Task.FromResult<string?>(DateTimeOffset.UtcNow.ToString("HH:mm - dd/MM/yyyy 'UTC'")),
            "ver" => Task.FromResult<string?>("0.1 (" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ")"),
            "neighbors" or "neighbours" => Task.FromResult<string?>(Neighbours()),
            _ => Task.FromResult<string?>(null)
        };
    }

    public string Neighbours()
    {
        var neighbours = NeighbourIdentities.GetAll()
            .Select(identity => new
            {
                Age = (int)Math.Max(0, (DateTimeOffset.UtcNow - (identity is MeshIdentity mesh ? mesh.ReceivedAt : DateTimeOffset.UtcNow)).TotalSeconds),
                Rssi = identity.Rssi,
                Snr = (byte)((int)((identity.Snr ?? 0) * 4) & 0xff),
                Key = identity.PublicKey[..4]
            })
            .OrderBy(item => item.Age)
            .Take(8)
            .ToArray();

        if (neighbours.Length == 0)
        {
            return "-none-";
        }

        return string.Join('\n', neighbours.Select(item =>
            $"{Convert.ToHexString(item.Key).ToLowerInvariant()}:{item.Age}:{item.Rssi?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0"}:{item.Snr}"));
    }

    public override async Task RxTextAsync(MeshTextPacket packet, CancellationToken cancellationToken)
    {
        if (packet.TextType is TextMessageType.Plain)
        {
            await RxTextDataAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        else if (packet.TextType is TextMessageType.CliData)
        {
            await RxCliDataAsync(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    public virtual Task RxTextDataAsync(MeshTextPacket packet, CancellationToken cancellationToken)
    {
        return RxCliDataAsync(packet, cancellationToken);
    }

    public virtual async Task RxCliDataAsync(MeshTextPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Source is null || !packet.Source.IsAdmin)
        {
            return;
        }

        var command = packet.Text;
        byte[]? tag = null;
        var separator = Array.IndexOf(command, (byte)'|');
        if (separator >= 0)
        {
            // Optional app tag is echoed before the pipe so clients can correlate async CLI responses.
            tag = command[..separator];
            command = command[(separator + 1)..];
        }

        var response = await CliCommandAsync(command, cancellationToken).ConfigureAwait(false) ?? "Unknown command";
        var responseBytes = Encoding.UTF8.GetBytes(response);
        if (tag is not null)
        {
            responseBytes = [.. tag, (byte)'|', .. responseBytes];
        }

        var reply = new MeshTextOutgoing(Self, packet.Source, responseBytes, TextMessageType.CliData, timestamp: packet.Timestamp + 1);
        await TransmitPacketAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    public virtual MeshDestination? Login(byte[] publicKey, byte[] password)
    {
        // Access order mirrors Python: admin credentials, admin key, open guest, guest password, guest key.
        var publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();
        if (_config.AdminPassword is not null && password.SequenceEqual(Encoding.UTF8.GetBytes(_config.AdminPassword)))
        {
            return LoginSuccess(publicKey, admin: true);
        }

        if (_config.AdminKeys.Contains(publicKeyHex))
        {
            return LoginSuccess(publicKey, admin: true);
        }

        if (_config.GuestOpen)
        {
            return LoginSuccess(publicKey);
        }

        if (_config.GuestPassword is not null && password.SequenceEqual(Encoding.UTF8.GetBytes(_config.GuestPassword)))
        {
            return LoginSuccess(publicKey);
        }

        if (_config.GuestKeys.Contains(publicKeyHex))
        {
            return LoginSuccess(publicKey);
        }

        return null;
    }

    public MeshDestination LoginSuccess(byte[] publicKey, bool admin = false)
    {
        var destination = new AnonymousIdentity(publicKey)
        {
            IsAdmin = admin
        };
        destination.CreateSharedSecret(Self.PrivateKey);
        return destination;
    }

    public override async Task RxAnonReqAsync(MeshAnonReqPacket packet, CancellationToken cancellationToken)
    {
        var destination = Login(packet.SenderPublicKey, packet.Password ?? []);
        if (destination is null)
        {
            return;
        }

        Identities.AddIdentity(destination);
        // RESP_SERVER_LOGIN_OK payload is four status bytes plus four random bytes.
        var responseData = new byte[8];
        responseData[0] = MeshResponseTypes.ServerLoginOk;
        responseData[1] = 0;
        responseData[2] = (byte)(destination.IsAdmin ? 1 : 0);
        responseData[3] = 0;
        RandomNumberGenerator.Fill(responseData.AsSpan(4));

        MeshPacket response = packet.IsFlood
            ? new MeshPathOutgoing(Self, destination, packet.Path, response: [.. BitConverter.GetBytes(MeshUtilities.UniqueUnixTime()), .. responseData])
            : new MeshResponseOutgoing(Self, destination, responseData);
        await TransmitPacketAsync(response, cancellationToken).ConfigureAwait(false);
        await LoggedInAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public virtual Task LoggedInAsync(MeshAnonReqPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;

    public override async Task RxReqAsync(MeshReqPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Request != (byte)MeshRequestType.GetStatus || packet.Source is null)
        {
            return;
        }

        var data = DeviceStats(packet.Rssi, packet.Snr);
        MeshPacket response = packet.IsFlood
            ? new MeshPathOutgoing(Self, packet.Source, packet.Path, response: [.. BitConverter.GetBytes(packet.Timestamp), .. data])
            : new MeshResponseOutgoing(Self, packet.Source, data, packet.Timestamp);
        await TransmitPacketAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public virtual byte[] DeviceStats(double rxRssi, double rxSnr)
    {
        // Binary field order is app protocol surface; change only with companion fixtures.
        var data = new List<byte>();
        AddU16(data, _hardware.BatteryMillivolts());
        AddU16(data, (ushort)(Dispatcher?.QueueLength ?? 0));
        AddI16(data, 0);
        AddI16(data, (short)rxRssi);
        AddU32(data, Stat("received"));
        AddU32(data, Stat("sent"));
        AddU32(data, (uint)(Dispatcher?.AirtimeSeconds ?? 0));
        AddU32(data, (uint)(DateTimeOffset.UtcNow - _beginTime).TotalSeconds);
        AddU32(data, Stat("sent.Flood"));
        AddU32(data, Stat("sent.Direct"));
        AddU32(data, Stat("received.Flood"));
        AddU32(data, Stat("received.Direct"));
        AddU16(data, (ushort)Stat("badpacket"));
        AddI16(data, (short)(rxSnr * 4));
        AddU16(data, (ushort)Stat("duplicate.Direct"));
        AddU16(data, (ushort)Stat("duplicate.Flood"));
        return data.ToArray();
    }

    protected uint Stat(string name) => Stats.TryGetValue(name, out var value) ? (uint)value : 0;

    private static void AddU16(List<byte> data, ushort value) => data.AddRange(BitConverter.GetBytes(value));
    private static void AddI16(List<byte> data, short value) => data.AddRange(BitConverter.GetBytes(value));
    private static void AddU32(List<byte> data, uint value) => data.AddRange(BitConverter.GetBytes(value));

    private async Task<string?> SendAdvertCommandAsync(CancellationToken cancellationToken)
    {
        await SendSelfAdvertAsync(flood: true, cancellationToken).ConfigureAwait(false);
        return "OK - Advert sent";
    }

    private async Task FloodAdvertLoopAsync(CancellationToken cancellationToken)
    {
        if (_config.AdvertFloodHours < 0)
        {
            return;
        }

        _floodAdvertInterval = TimeSpan.FromHours(_config.AdvertFloodHours);
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendSelfAdvertAsync(flood: true, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastFloodAdvertUnixSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (_config.AdvertFloodHours == 0)
            {
                break;
            }

            await Task.Delay(_floodAdvertInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DirectAdvertLoopAsync(CancellationToken cancellationToken)
    {
        if (_config.AdvertDirectMinutes < 0)
        {
            return;
        }

        var directAdvertInterval = TimeSpan.FromMinutes(_config.AdvertDirectMinutes);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!ShouldSkipDirectAdvert())
            {
                await SendSelfAdvertAsync(flood: false, cancellationToken).ConfigureAwait(false);
            }

            if (_config.AdvertDirectMinutes == 0)
            {
                break;
            }

            await Task.Delay(directAdvertInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ShouldSkipDirectAdvert()
    {
        var lastFlood = Interlocked.Read(ref _lastFloodAdvertUnixSeconds);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var floodIntervalSeconds = (long)_floodAdvertInterval.TotalSeconds;
        var nextFlood = lastFlood + floodIntervalSeconds;
        var isTooSoonAfterLastFlood = (lastFlood + DirectAdvertSuppressionSeconds) > now;
        // Skip direct adverts within the suppression window immediately before the next scheduled flood advert.
        var isApproachingNextFlood = nextFlood > now && (nextFlood - DirectAdvertSuppressionSeconds) < now;
        var isMissedFloodSchedule = nextFlood < now && floodIntervalSeconds > 0;
        return isTooSoonAfterLastFlood || isApproachingNextFlood || isMissedFloodSchedule;
    }

    private Task SendSelfAdvertAsync(bool flood, CancellationToken cancellationToken)
    {
        return TransmitPacketAsync(new MeshAdvertOutgoing(Self, flood), cancellationToken);
    }
}

public sealed class RepeaterMeshDevice : CliMeshDevice
{
    public RepeaterMeshDevice(SelfIdentity self, IdentityStore neighbours, HardwarePlatform hardware, DeviceAccessConfig config)
        : base("Repeater", "repeater", self, new IdentityStore(), neighbours, hardware, config)
    {
        IsRepeater = true;
    }

    public override async Task RxTraceAsync(MeshTracePacket packet, CancellationToken cancellationToken)
    {
        // Trace packets carry the intended hop list in payload and observed SNRs in the mutable path.
        if (packet.TracePath.Length == packet.PathLength)
        {
            return;
        }

        if (packet.TracePath.Length < packet.PathLength)
        {
            throw new InvalidMeshCorePacketException("Trace data is longer than trace path.");
        }

        var currentHop = packet.TracePath[packet.PathLength];
        if (currentHop != Self.Hash)
        {
            return;
        }

        packet.AppendHop((byte)((int)(packet.Snr * 4) & 0xff));
        await TransmitPacketAsync(packet, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RoomMeshDevice : CliMeshDevice
{
    private readonly DeviceAccessConfig _config;
    private readonly ConcurrentQueue<RoomMessage> _messages = new();

    public RoomMeshDevice(SelfIdentity self, IdentityStore loggedIn, IdentityStore neighbours, HardwarePlatform hardware, DeviceAccessConfig config)
        : base("Room server", "room", self, loggedIn, neighbours, hardware, config)
    {
        _config = config;
    }

    public override MeshDestination? Login(byte[] publicKey, byte[] password)
    {
        var publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();
        var writer = false;
        if (_config.WriterPassword is not null && password.SequenceEqual(Encoding.UTF8.GetBytes(_config.WriterPassword)))
        {
            writer = true;
        }

        if (_config.WriterKeys.Contains(publicKeyHex))
        {
            writer = true;
        }

        if (writer)
        {
            var destination = LoginSuccess(publicKey);
            destination.IsWriter = true;
            return destination;
        }

        return base.Login(publicKey, password);
    }

    public override Task RxTextDataAsync(MeshTextPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Source is null)
        {
            return Task.CompletedTask;
        }

        if (_config.ReadOnly && !packet.Source.IsAdmin && !packet.Source.IsWriter)
        {
            return Task.CompletedTask;
        }

        // Python keeps room history in memory; this queue intentionally does not persist.
        var text = packet.Text.Length > MeshCoreLimits.MaxTextMessage - 4
            ? packet.Text[..(MeshCoreLimits.MaxTextMessage - 4)]
            : packet.Text;
        _messages.Enqueue(new RoomMessage(text, packet.Source.PublicKey[..4], MeshUtilities.UniqueUnixTime()));
        while (_messages.Count > 32 && _messages.TryDequeue(out _))
        {
        }

        Increment("room.posted");
        return Task.CompletedTask;
    }

    public override byte[] DeviceStats(double rxRssi, double rxSnr)
    {
        return [.. base.DeviceStats(rxRssi, rxSnr), .. BitConverter.GetBytes((ushort)Stat("room.posted")), .. BitConverter.GetBytes((ushort)Stat("room.pushed"))];
    }

    private sealed record RoomMessage(byte[] Text, byte[] PublicKeyPrefix, uint Timestamp);
}
