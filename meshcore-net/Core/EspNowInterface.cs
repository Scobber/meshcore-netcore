using System.Buffers.Binary;
using System.Net.NetworkInformation;
using SharpPcap;
using SharpPcap.LibPcap;

namespace MeshCoreNet;

/// <summary>
/// ESP-NOW monitor-mode capture/injection options.
/// </summary>
public sealed record EspNowOptions(
    string Device,
    string? LocalMac = null,
    bool AcceptBroadcast = true,
    bool AcceptAll = true,
    int SnapLength = 2048,
    int ReadTimeoutMs = 1000);

public sealed class EspNowInterface : MeshInterfaceBase
{
    private readonly IEspNowSocket _socket;

    public EspNowInterface(string name, EspNowOptions options, IEspNowSocket? socket = null)
        : base(name, "espnow")
    {
        _socket = socket ?? new SharpPcapEspNowSocket(options);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _socket.FrameReceived += OnFrameReceived;
        _socket.Start();
        return Task.CompletedTask;
    }

    public override ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        // ESP-NOW airtime is not tracked by Python and is not duty-cycle limited like LoRa.
        _socket.SendBroadcast(packetData);
        return ValueTask.FromResult(0d);
    }

    private void OnFrameReceived(object? sender, EspNowReceivedFrame frame)
    {
        _ = ReceivedFrameWriter.WriteAsync(new RadioFrame(frame.Payload, frame.Rssi, 0));
    }
}

public sealed record EspNowReceivedFrame(byte[] SourceMac, byte[] DestinationMac, byte[] Payload, double Rssi = 0);

public interface IEspNowSocket
{
    event EventHandler<EspNowReceivedFrame>? FrameReceived;
    void Start();
    void SendBroadcast(byte[] payload);
}

public sealed class SharpPcapEspNowSocket : IEspNowSocket
{
    private static readonly byte[] BroadcastMac = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff];

    private readonly EspNowOptions _options;
    private LibPcapLiveDevice? _device;
    private byte[]? _localMac;

    public SharpPcapEspNowSocket(EspNowOptions options)
    {
        _options = options;
    }

    public event EventHandler<EspNowReceivedFrame>? FrameReceived;

    public void Start()
    {
        // The interface must already be in monitor mode and on the ESP-NOW channel.
        _device = LibPcapLiveDeviceList.Instance.FirstOrDefault(device =>
            string.Equals(device.Name, _options.Device, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(device.Description, _options.Device, StringComparison.OrdinalIgnoreCase));

        if (_device is null)
        {
            throw new InvalidOperationException($"ESP-NOW capture device '{_options.Device}' was not found.");
        }

        _localMac = ParseMac(_options.LocalMac) ?? _device.MacAddress?.GetAddressBytes() ?? BroadcastMac;
        _device.Open(new DeviceConfiguration
        {
            // Promiscuous/Immediate keeps behavior close to Scapy's AsyncSniffer path.
            Mode = DeviceModes.Promiscuous | DeviceModes.MaxResponsiveness,
            ReadTimeout = _options.ReadTimeoutMs,
            Snaplen = _options.SnapLength,
            Immediate = true
        });
        _device.Filter = BuildFilter(_localMac);
        _device.OnPacketArrival += HandlePacketArrival;
        _device.StartCapture();
        Console.WriteLine($"ESP-NOW interface '{_options.Device}' is listening in monitor mode.");
    }

    public void SendBroadcast(byte[] payload)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("ESP-NOW device is not started.");
        }

        _localMac ??= _device.MacAddress?.GetAddressBytes() ?? BroadcastMac;
        // Python always broadcasts MeshCore payloads to FF:FF:FF:FF:FF:FF.
        var frame = EspNowCodec.BuildActionFrame(BroadcastMac, _localMac, payload);
        _device.SendPacket(frame, default);
    }

    private void HandlePacketArrival(object sender, PacketCapture capture)
    {
        var data = capture.Data.ToArray();
        if (EspNowCodec.TryDecode(data, _options.AcceptBroadcast, _options.AcceptAll, _localMac, out var frame))
        {
            FrameReceived?.Invoke(this, frame);
        }
    }

    private string BuildFilter(byte[] localMac)
    {
        if (_options.AcceptAll)
        {
            return "type mgt subtype action and wlan[24:4] = 0x7f18fe34";
        }

        var mac = FormatMac(localMac);
        return $"type mgt subtype action and wlan[24:4] = 0x7f18fe34 and (wlan addr1 {mac} or wlan addr1 ff:ff:ff:ff:ff:ff)";
    }

    private static byte[]? ParseMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.Length == 12 ? Convert.FromHexString(compact) : null;
    }

    private static string FormatMac(byte[] mac) => string.Join(':', mac.Select(value => value.ToString("x2")));
}

public static class EspNowCodec
{
    // Plaintext ESP-NOW vendor action frame markers copied from ESPythoNOW.
    private const byte FrameControlActionSubtype = 0xd0;
    private static readonly byte[] EspNowOui = [0x7f, 0x18, 0xfe, 0x34];
    private static readonly byte[] VendorOui = [0x18, 0xfe, 0x34];
    private static readonly byte[] BroadcastMac = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff];
    private static readonly object RandomLock = new();
    private static readonly Queue<uint> RecentRandomValues = new();
    private static readonly Random Random = new();

    public static byte[] BuildActionFrame(byte[] destinationMac, byte[] sourceMac, byte[] payload)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(destinationMac.Length, 6);
        ArgumentOutOfRangeException.ThrowIfNotEqual(sourceMac.Length, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, 250);

        // This builds a bare 802.11 management action frame. Radiotap headers are supplied by the OS/driver.
        var body = BuildBody(payload);
        var frame = new byte[24 + body.Length];
        frame[0] = FrameControlActionSubtype;
        frame[1] = 0x00;
        destinationMac.CopyTo(frame.AsSpan(4, 6));
        sourceMac.CopyTo(frame.AsSpan(10, 6));
        BroadcastMac.CopyTo(frame.AsSpan(16, 6));
        body.CopyTo(frame.AsSpan(24));
        return frame;
    }

    public static byte[] BuildBody(byte[] payload, uint? randomValue = null)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, 250);
        var body = new byte[15 + payload.Length];
        // Body layout: ESP-NOW OUI, random anti-replay value, vendor element, then MeshCore payload.
        EspNowOui.CopyTo(body.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), randomValue ?? NextRandomValue());
        body[8] = 0xdd;
        body[9] = (byte)(payload.Length + 5);
        VendorOui.CopyTo(body.AsSpan(10, 3));
        body[13] = 0x04;
        body[14] = 0x01;
        payload.CopyTo(body.AsSpan(15));
        return body;
    }

    public static bool TryDecode(byte[] frameBytes, bool acceptBroadcast, bool acceptAll, byte[]? localMac, out EspNowReceivedFrame frame)
    {
        frame = new EspNowReceivedFrame([], [], []);

        // Captures may include a radiotap header, so find the ESP-NOW body rather than assuming offset 24.
        var bodyOffset = FindBodyOffset(frameBytes);
        if (bodyOffset < 0 || frameBytes.Length < bodyOffset + 15)
        {
            return false;
        }

        var source = ReadMac(frameBytes, 10);
        var destination = ReadMac(frameBytes, 4);
        var destinationIsBroadcast = destination.SequenceEqual(BroadcastMac);
        var destinationIsLocal = localMac is not null && destination.SequenceEqual(localMac);
        if (!acceptAll && !destinationIsLocal && !(acceptBroadcast && destinationIsBroadcast))
        {
            return false;
        }

        if (!frameBytes.AsSpan(bodyOffset, 4).SequenceEqual(EspNowOui))
        {
            return false;
        }

        if (frameBytes[bodyOffset + 8] != 0xdd ||
            !frameBytes.AsSpan(bodyOffset + 10, 3).SequenceEqual(VendorOui) ||
            frameBytes[bodyOffset + 13] != 0x04 ||
            frameBytes[bodyOffset + 14] != 0x01)
        {
            return false;
        }

        var vendorLength = frameBytes[bodyOffset + 9];
        var payloadLength = vendorLength - 5;
        if (payloadLength < 0 || bodyOffset + 15 + payloadLength > frameBytes.Length)
        {
            return false;
        }

        var randomValue = BinaryPrimitives.ReadUInt32BigEndian(frameBytes.AsSpan(bodyOffset + 4, 4));
        if (IsDuplicate(randomValue))
        {
            return false;
        }

        var payload = frameBytes.AsSpan(bodyOffset + 15, payloadLength).ToArray();
        frame = new EspNowReceivedFrame(source, destination, payload, ReadRadiotapRssi(frameBytes, bodyOffset));
        return true;
    }

    private static int FindBodyOffset(byte[] frameBytes)
    {
        if (frameBytes.Length >= 24 && frameBytes[0] == FrameControlActionSubtype)
        {
            return 24;
        }

        for (var index = 0; index <= frameBytes.Length - EspNowOui.Length; index++)
        {
            if (frameBytes.AsSpan(index, EspNowOui.Length).SequenceEqual(EspNowOui))
            {
                return index;
            }
        }

        return -1;
    }

    private static byte[] ReadMac(byte[] frameBytes, int offset)
    {
        return frameBytes.Length >= offset + 6 ? frameBytes.AsSpan(offset, 6).ToArray() : [];
    }

    private static double ReadRadiotapRssi(byte[] frameBytes, int bodyOffset)
    {
        // Minimal radiotap parsing for dBm_AntSignal. Return 0 if the capture did not include it.
        if (bodyOffset < 8 || frameBytes.Length < 8 || frameBytes[0] != 0x00)
        {
            return 0;
        }

        var present = BinaryPrimitives.ReadUInt32LittleEndian(frameBytes.AsSpan(4, 4));
        if ((present & (1u << 5)) == 0)
        {
            return 0;
        }

        var offset = 8;
        if ((present & 0x01) != 0)
        {
            offset += 8;
        }

        if ((present & 0x02) != 0)
        {
            offset += 1;
        }

        if ((present & 0x04) != 0)
        {
            offset += 1;
        }

        if ((present & 0x08) != 0)
        {
            offset += 4;
        }

        if ((present & 0x10) != 0)
        {
            offset += 2;
        }

        return offset < bodyOffset ? unchecked((sbyte)frameBytes[offset]) : 0;
    }

    private static uint NextRandomValue()
    {
        Span<byte> bytes = stackalloc byte[4];
        lock (RandomLock)
        {
            Random.NextBytes(bytes);
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static bool IsDuplicate(uint randomValue)
    {
        // ESPythoNOW keeps the last ten random values to suppress retransmitted action frames.
        lock (RecentRandomValues)
        {
            if (RecentRandomValues.Contains(randomValue))
            {
                return true;
            }

            RecentRandomValues.Enqueue(randomValue);
            while (RecentRandomValues.Count > 10)
            {
                RecentRandomValues.Dequeue();
            }
        }

        return false;
    }
}
