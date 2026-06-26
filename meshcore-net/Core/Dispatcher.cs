using System.Security.Cryptography;

namespace MeshCoreNet;

/// <summary>
/// Coordinates packet flow between mesh devices and one or more transports.
/// </summary>
public sealed class MeshDispatcher
{
    // _pending is kept sorted by priority then insertion sequence; low numeric priority sends first.
    private readonly List<DispatchEntry> _pending = [];
    // Duplicate suppression is global within this dispatcher and expires through CleanSeenTable().
    private readonly Dictionary<string, SeenPacket> _seen = new(StringComparer.Ordinal);
    private readonly List<IMeshInterface> _interfaces = [];
    private readonly List<IMeshDevice> _devices = [];
    private readonly List<Task> _interfaceLoops = [];
    private readonly object _sync = new();
    private CancellationTokenSource? _runCts;
    private Task? _loopTask;
    private long _sequence;
    private double _airtimeSeconds;

    public bool PassInternal { get; set; }
    public TimeSpan SeenPacketLifetime { get; set; } = TimeSpan.FromSeconds(60);
    public int QueueLength
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    public double AirtimeSeconds => Volatile.Read(ref _airtimeSeconds);

    public RadioConfig GetRadioConfig()
    {
        // The companion app expects one radio config. Prefer the first interface that has real values.
        lock (_sync)
        {
            return _interfaces.Select(meshInterface => meshInterface.GetRadioConfig()).FirstOrDefault(config =>
                config.FrequencyKhz != 0 ||
                config.BandwidthHz != 0 ||
                config.SpreadingFactor != 0 ||
                config.CodingRate != 0 ||
                config.TxPower != 0 ||
                config.MaxTxPower != 0) ?? RadioConfig.Empty;
        }
    }

    public void RegisterInterface(IMeshInterface meshInterface)
    {
        lock (_sync)
        {
            _interfaces.Add(meshInterface);
        }
    }

    public void RegisterDevice(IMeshDevice device)
    {
        lock (_sync)
        {
            _devices.Add(device);
        }
    }

    public bool QueuePacket(
        MeshPacket packet,
        int priority = DispatchPriority.Lowest,
        int? timeoutSeconds = null,
        Action<byte[], int>? duplicateCallback = null,
        byte[]? duplicateScope = null)
    {
        var timeout = timeoutSeconds ?? priority * 10;
        // Duplicate detection happens before queueing so waiting senders can observe cancellation.
        if (HasSeen(packet, duplicateCallback, duplicateScope))
        {
            packet.MarkSendCancelled();
            return false;
        }

        var entry = new DispatchEntry(packet, priority, DateTimeOffset.UtcNow.AddSeconds(timeout), Interlocked.Increment(ref _sequence));
        lock (_sync)
        {
            _pending.Add(entry);
            _pending.Sort((a, b) =>
            {
                var priorityComparison = a.Priority.CompareTo(b.Priority);
                if (priorityComparison != 0)
                {
                    return priorityComparison;
                }

                return a.Sequence.CompareTo(b.Sequence);
            });
        }

        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
        {
            return Task.CompletedTask;
        }

        // Use a linked CTS that StopAsync can cancel; this was the root of the earlier xUnit hang.
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => DispatchLoopAsync(_runCts.Token), CancellationToken.None);
        lock (_sync)
        {
            foreach (var meshInterface in _interfaces)
            {
                _interfaceLoops.Add(Task.Run(() => InterfaceReceiveLoopAsync(meshInterface, _runCts.Token), CancellationToken.None));
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var loopTask = _loopTask;
        if (loopTask is null)
        {
            return;
        }

        _runCts?.Cancel();

        try
        {
            await loopTask.ConfigureAwait(false);
            await Task.WhenAll(_interfaceLoops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loopTask = null;
            _interfaceLoops.Clear();
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DispatchExpiredEntries();
            CleanSeenTable();

            DispatchEntry? next;
            lock (_sync)
            {
                next = _pending.FirstOrDefault();
            }

            if (next is not null)
            {
                await SendAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void DispatchExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _pending.RemoveAll(entry => entry.ExpiresAt <= now);
        }
    }

    private void CleanSeenTable()
    {
        var cutoff = DateTimeOffset.UtcNow - SeenPacketLifetime;
        lock (_sync)
        {
            foreach (var key in _seen.Where(pair => pair.Value.FirstSeen < cutoff).Select(pair => pair.Key).ToArray())
            {
                _seen.Remove(key);
            }
        }
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        TimeSpan transmitWait;
        lock (_sync)
        {
            // If any interface is duty-cycle limited, the whole outbound packet waits.
            transmitWait = _interfaces.Count == 0
                ? TimeSpan.Zero
                : _interfaces.Select(meshInterface => meshInterface.TransmitWait()).DefaultIfEmpty(TimeSpan.Zero).Max();
        }

        if (transmitWait > TimeSpan.Zero)
        {
            await Task.Delay(transmitWait, cancellationToken).ConfigureAwait(false);
        }

        DispatchEntry? next;
        lock (_sync)
        {
            next = _pending.FirstOrDefault();
            if (next is not null)
            {
                _pending.RemoveAt(0);
            }
        }

        if (next is null)
        {
            return;
        }

        if (next.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            next.Packet.MarkSendCancelled();
            return;
        }

        var transmitTasks = new List<Task<double>>();
        lock (_sync)
        {
            foreach (var meshInterface in _interfaces)
            {
                transmitTasks.Add(meshInterface.TransmitAsync(next.Packet.ToWire(), cancellationToken).AsTask());
            }
        }

        var transmitTimes = await Task.WhenAll(transmitTasks).ConfigureAwait(false);
        if (transmitTimes.Length > 0)
        {
            // When multiple radios send the same packet, account the slowest airtime like Python.
            Interlocked.Exchange(ref _airtimeSeconds, _airtimeSeconds + transmitTimes.Max() / 1000d);
        }

        if (PassInternal && next.Packet.PathLength == 0)
        {
            // Internal delivery models Python's pass_internal zero-hop behavior for multi-device hosts.
            await DeliverFrameAsync(new RadioFrame(next.Packet.ToWire(), IsInternal: true), cancellationToken).ConfigureAwait(false);
        }

        next.Packet.MarkSent();
    }

    private async Task InterfaceReceiveLoopAsync(IMeshInterface meshInterface, CancellationToken cancellationToken)
    {
        await foreach (var frame in meshInterface.ReceivedFrames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await DeliverFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeliverFrameAsync(RadioFrame frame, CancellationToken cancellationToken)
    {
        List<IMeshDevice> devices;
        lock (_sync)
        {
            devices = [.. _devices];
        }

        await Task.WhenAll(devices.Select(device => device.HandleFrameAsync(frame, cancellationToken))).ConfigureAwait(false);
    }

    public bool HasSeen(MeshPacket packet, Action<byte[], int>? duplicateCallback = null, byte[]? duplicateScope = null, bool checkOnly = false)
    {
        var hash = PacketHash(packet, duplicateScope);
        var key = Convert.ToHexString(hash);
        lock (_sync)
        {
            if (!_seen.TryGetValue(key, out var entry))
            {
                if (checkOnly)
                {
                    return false;
                }

                _seen[key] = new SeenPacket
                {
                    Count = 1,
                    FirstSeen = DateTimeOffset.UtcNow,
                    LastSeen = DateTimeOffset.UtcNow,
                    DuplicateCallback = duplicateCallback
                };
                return false;
            }

            if (checkOnly)
            {
                return true;
            }

            entry.DuplicateCallback?.Invoke(hash, entry.Count);
            entry.Count += 1;
            entry.LastSeen = DateTimeOffset.UtcNow;
            return true;
        }
    }

    private static byte[] PacketHash(MeshPacket packet, byte[]? extra)
    {
        // Hash only stable duplicate identity fields. Trace includes path length because path mutation is the signal.
        using var sha = SHA256.Create();
        sha.TransformBlock([(byte)packet.Type], 0, 1, null, 0);
        if (packet.Type == MeshPacketType.Trace)
        {
            sha.TransformBlock([(byte)packet.PathLength], 0, 1, null, 0);
        }

        var payload = packet.Payload;
        sha.TransformBlock(payload, 0, payload.Length, null, 0);
        var final = extra ?? [];
        sha.TransformFinalBlock(final, 0, final.Length);
        return sha.Hash![..8];
    }

    private sealed class DispatchEntry(MeshPacket packet, int priority, DateTimeOffset expiresAt, long sequence)
    {
        public MeshPacket Packet { get; } = packet;
        public int Priority { get; } = priority;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public long Sequence { get; } = sequence;
    }

    private sealed class SeenPacket
    {
        public int Count { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public Action<byte[], int>? DuplicateCallback { get; set; }
    }
}

public static class DispatchPriority
{
    public const int Top = 1;
    public const int Message = 2;
    public const int Channel = 3;
    public const int Advert = 4;
    public const int Repeat = 5;
    public const int RoomTraffic = 6;
    public const int ScheduledAdvert = 7;
    public const int Lowest = 8;
}
