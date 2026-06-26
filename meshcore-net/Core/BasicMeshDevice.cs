using System.Collections.Concurrent;

namespace MeshCoreNet;

/// <summary>
/// Base MeshCore device pipeline. Subclasses override Rx* hooks for role-specific behavior.
/// </summary>
public class BasicMeshDevice : IMeshDevice
{
    private readonly ConcurrentDictionary<string, long> _stats = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _waitingAck = [];
    private readonly object _ackSync = new();
    private MeshDispatcher? _dispatcher;

    public BasicMeshDevice(string name, string type, SelfIdentity self, IdentityStore identities, IReadOnlyList<Channel>? channels = null)
    {
        Name = name;
        Type = type;
        Self = self;
        Identities = identities;
        Channels = channels ?? [];
    }

    public string Name { get; }
    public string Type { get; }
    public SelfIdentity Self { get; }
    public IdentityStore Identities { get; }
    public IReadOnlyList<Channel> Channels { get; }
    public bool IsRepeater { get; protected set; }
    public IReadOnlyDictionary<string, long> Stats => _stats;
    protected MeshDispatcher? Dispatcher => _dispatcher;

    public virtual Task StartAsync(CancellationToken cancellationToken, MeshDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        return Task.CompletedTask;
    }

    public virtual Task HandlePacketAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        return ReceivePacketAsync(packet, cancellationToken);
    }

    public virtual Task HandleFrameAsync(RadioFrame frame, CancellationToken cancellationToken)
    {
        // Interfaces deliver raw bytes; devices convert once so hooks can work at typed-packet level.
        var packet = MeshPacketConverter.ConvertIncoming(frame.Packet, Self, Identities, Channels, frame.Rssi, frame.Snr);
        return ReceivePacketAsync(packet, cancellationToken);
    }

    // Hook ordering: RxRawAsync, duplicate/stats handling, type-specific hook, RxAsync, optional repeat.
    public virtual Task RxRawAsync(MeshIncomingPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxAsync(MeshIncomingPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxAdvertAsync(MeshAdvertPacket packet, CancellationToken cancellationToken) => AddAdvertAsync(packet);
    public virtual Task RxTextAsync(MeshTextPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxPathAsync(MeshPathPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxReqAsync(MeshReqPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxAnonReqAsync(MeshAnonReqPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxResponseAsync(MeshSrcDestPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxTraceAsync(MeshTracePacket packet, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task RxGroupTextAsync(MeshGroupTextPacket packet, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task TransmitPacketAsync(
        MeshPacket packet,
        CancellationToken cancellationToken,
        Action<byte[], int>? duplicateCallback = null,
        int? priority = null)
    {
        if (_dispatcher is null)
        {
            throw new InvalidOperationException("Device has not been started with a dispatcher.");
        }

        _dispatcher.QueuePacket(packet, priority ?? PriorityFor(packet), duplicateCallback: duplicateCallback, duplicateScope: Self.PublicKey);
        Increment("sent");
        Increment("sent." + (packet.IsFlood ? "Flood" : "Direct"));
        await Task.CompletedTask;
    }

    public async Task<bool> SendTextWithRetriesAsync(MeshTextOutgoing text, int retries = 3, CancellationToken cancellationToken = default)
    {
        if (text.TextType == TextMessageType.CliData)
        {
            await TransmitPacketAsync(text, cancellationToken).ConfigureAwait(false);
            return true;
        }

        for (var attempt = 0; attempt < retries; attempt++)
        {
            if (attempt + 1 == retries)
            {
                // Match Python: the final retry becomes a flood when direct attempts fail.
                text.Flood();
            }

            var ackHash = text.MessageAckHash();
            await TransmitPacketAsync(text, cancellationToken).ConfigureAwait(false);
            if (await AwaitAckAsync(ackHash, text.Sent, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    protected async Task<bool> AwaitAckAsync(byte[] ackHash, Task<bool> sent, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(ackHash);
        var ackFuture = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_ackSync)
        {
            _waitingAck[key] = ackFuture;
        }

        try
        {
            await sent.ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await ackFuture.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            lock (_ackSync)
            {
                _waitingAck.Remove(key);
            }
        }
    }

    protected virtual async Task ReceivePacketAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        if (packet is not MeshIncomingPacket incoming)
        {
            incoming = new MeshIncomingPacket(packet.ToWire());
        }

        await RxRawAsync(incoming, cancellationToken).ConfigureAwait(false);
        Increment("received");

        if (_dispatcher is not null && _dispatcher.HasSeen(incoming, duplicateScope: Self.PublicKey))
        {
            Increment("duplicate");
            Increment("duplicate." + (incoming.IsFlood ? "Flood" : "Direct"));
            return;
        }

        Increment("received." + (incoming.IsFlood ? "Flood" : "Direct"));
        Increment($"received.{(incoming.IsFlood ? "Flood" : "Direct")}.{incoming.PathLength}");
        Increment("type." + incoming.Type);

        if (incoming is MeshPathPacket pathPacket && pathPacket.Decrypted && pathPacket.Source is not null && pathPacket.PathData is not null)
        {
            // PATH replies refresh the contact return path used by future direct sends.
            pathPacket.Source.Path = pathPacket.PathData;
            Identities.AddIdentity(pathPacket.Source);
        }

        switch (incoming)
        {
            case MeshAdvertPacket advert when advert.Advert.Validate():
                await RxAdvertAsync(advert, cancellationToken).ConfigureAwait(false);
                break;
            case MeshGroupTextPacket groupText when groupText.Decrypted:
                await RxGroupTextAsync(groupText, cancellationToken).ConfigureAwait(false);
                break;
            case MeshAckPacket ack:
                await RxAckAsync(ack.AckHash).ConfigureAwait(false);
                break;
            case MeshPathPacket { Decrypted: true, AckHash: not null } pathAck:
                await RxAckAsync(pathAck.AckHash).ConfigureAwait(false);
                break;
            case MeshResponsePacket { Decrypted: true, Response: not null } response:
                await RxResponseAsync(response, cancellationToken).ConfigureAwait(false);
                break;
            case MeshPathPacket { Decrypted: true, Response: not null } pathResponse:
                await RxResponseAsync(pathResponse, cancellationToken).ConfigureAwait(false);
                break;
            case MeshTextPacket { Decrypted: true } text:
                await ReceivedTextAsync(text, cancellationToken).ConfigureAwait(false);
                break;
            case MeshAnonReqPacket { Decrypted: true } anonReq:
                await RxAnonReqAsync(anonReq, cancellationToken).ConfigureAwait(false);
                break;
            case MeshReqPacket { Decrypted: true } req:
                await RxReqAsync(req, cancellationToken).ConfigureAwait(false);
                break;
            case MeshTracePacket trace:
                await RxTraceAsync(trace, cancellationToken).ConfigureAwait(false);
                break;
        }

        await RxAsync(incoming, cancellationToken).ConfigureAwait(false);

        if (IsRepeater && incoming.Repeat)
        {
            await RepeatPacketAsync(incoming, cancellationToken).ConfigureAwait(false);
        }
    }

    protected virtual async Task ReceivedTextAsync(MeshTextPacket packet, CancellationToken cancellationToken)
    {
        await SendAckAsync(packet, cancellationToken).ConfigureAwait(false);
        await RxTextAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task SendAckAsync(MeshTextPacket packet, CancellationToken cancellationToken)
    {
        if (packet.Source is null)
        {
            return;
        }

        MeshPacket? ackPacket;
        if (packet.TextType == TextMessageType.CliData)
        {
            // CLI data over flood receives only a return path, not a message ACK.
            ackPacket = packet.IsFlood ? new MeshPathOutgoing(Self, packet.Source, packet.Path) : null;
        }
        else
        {
            ackPacket = packet.IsFlood
                ? new MeshPathOutgoing(Self, packet.Source, packet.Path, packet.MessageAckHash())
                : new MeshAckOutgoing(packet, packet.Source.Path ?? []);
        }

        if (ackPacket is null)
        {
            return;
        }

        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        await TransmitPacketAsync(ackPacket, cancellationToken).ConfigureAwait(false);
    }

    protected virtual Task RxAckAsync(byte[] ackHash)
    {
        var key = Convert.ToHexString(ackHash);
        lock (_ackSync)
        {
            if (_waitingAck.TryGetValue(key, out var future))
            {
                future.TrySetResult(true);
            }
        }

        return Task.CompletedTask;
    }

    protected virtual Task AddAdvertAsync(MeshAdvertPacket packet)
    {
        var identity = new MeshIdentity(packet.Advert, advertPath: packet.Path);
        identity.CreateSharedSecret(Self.PrivateKey);
        Identities.AddIdentity(identity);
        return Task.CompletedTask;
    }

    protected virtual async Task RepeatPacketAsync(MeshIncomingPacket packet, CancellationToken cancellationToken)
    {
        if (packet is MeshTracePacket)
        {
            return;
        }

        if (packet.IsFlood)
        {
            if (packet.PathLength >= MeshCoreLimits.MaxPathSize - 1)
            {
                Increment("repeat.Flood.too_long");
                return;
            }

            packet.AppendHop(Self.Hash);
            Increment($"repeat.Flood.{packet.PathLength}");
        }
        else
        {
            // Direct repeaters consume the first path byte only when it is addressed to this node.
            if (packet.PathLength == 0)
            {
                Increment("repeat.Direct.zerohop");
                return;
            }

            if (packet.Path[0] != Self.Hash)
            {
                Increment("repeat.Direct.notme");
                return;
            }

            packet.PopNextHop(out _);
            Increment($"repeat.Direct.{packet.PathLength}");
        }

        await TransmitPacketAsync(packet, cancellationToken, priority: DispatchPriority.Repeat).ConfigureAwait(false);
    }

    protected void Increment(string key)
    {
        _stats.AddOrUpdate(key, 1, (_, current) => current + 1);
    }

    private static int PriorityFor(MeshPacket packet)
    {
        return packet switch
        {
            MeshTextOutgoing { TextType: TextMessageType.SignedPlain } => DispatchPriority.RoomTraffic,
            MeshSrcDestOutgoing or MeshAckOutgoing => DispatchPriority.Message,
            MeshGroupOutgoing or MeshPathOutgoing => DispatchPriority.Channel,
            MeshAdvertOutgoing => DispatchPriority.Advert,
            MeshIncomingPacket => DispatchPriority.Repeat,
            _ => DispatchPriority.Advert
        };
    }
}
