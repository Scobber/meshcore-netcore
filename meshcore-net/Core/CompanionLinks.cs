using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.IO.Ports;

namespace MeshCoreNet;

/// <summary>
/// Link between this host acting as a companion radio and a MeshCore app.
/// </summary>
public interface ICompanionAppLink : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken);
    Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken);
}

public static class CompanionFrameCodec
{
    public static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        byte startMarker,
        CancellationToken cancellationToken,
        TimeSpan? byteTimeout = null)
    {
        // Both TCP and serial app links use marker + little-endian uint16 length + payload.
        while (true)
        {
            var marker = await ReadExactlyWithOptionalTimeoutAsync(stream, 1, cancellationToken, byteTimeout).ConfigureAwait(false);
            if (marker[0] == startMarker)
            {
                break;
            }
        }

        var lengthBytes = await ReadExactlyWithOptionalTimeoutAsync(stream, 2, cancellationToken, byteTimeout).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        return await ReadExactlyWithOptionalTimeoutAsync(stream, length, cancellationToken, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public static async Task WriteFrameAsync(Stream stream, byte startMarker, byte[] frame, CancellationToken cancellationToken)
    {
        if (frame.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "Companion frames cannot exceed 65535 bytes.");
        }

        var header = new byte[3];
        header[0] = startMarker;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1), (ushort)frame.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyWithOptionalTimeoutAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linked?.Token ?? cancellationToken;
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return buffer;
    }
}

public sealed class TcpCompanionAppLink : ICompanionAppLink
{
    private readonly IPAddress? _listenAddress;
    private readonly int _port;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpCompanionAppLink(int port = 5000, IPAddress? listenAddress = null)
    {
        _port = port;
        _listenAddress = listenAddress;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(_listenAddress ?? IPAddress.Any, _port);
        _listener.Start(backlog: 1);
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (_client is not null)
                {
                    // Python accepts a single active app connection; extra clients are refused.
                    client.Dispose();
                    continue;
                }

                _client = client;
                _stream = client.GetStream();
            }
        }, cancellationToken);
    }

    public async Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        while (_stream is null)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return await CompanionFrameCodec.ReadFrameAsync(_stream, (byte)'<', cancellationToken, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
    }

    public async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        try
        {
            await CompanionFrameCodec.WriteFrameAsync(_stream, (byte)'>', frame, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _stream = null;
            _client?.Dispose();
            _client = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        return ValueTask.CompletedTask;
    }
}

public sealed class SerialCompanionAppLink : ICompanionAppLink
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _serialPort;
    private Stream? _stream;
    private bool _connected;

    public SerialCompanionAppLink(string portName = "/dev/ttyS0", int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serialPort = new SerialPort(_portName, _baudRate)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        _serialPort.Open();
        _stream = _serialPort.BaseStream;
        return Task.CompletedTask;
    }

    public async Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Serial companion link has not been started.");
        }

        var frame = await CompanionFrameCodec.ReadFrameAsync(_stream, (byte)'<', cancellationToken).ConfigureAwait(false);
        _connected = true;
        return frame;
    }

    public async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (_stream is null || !_connected)
        {
            // The app has to send at least one frame before serial writes are considered connected.
            return;
        }

        try
        {
            await CompanionFrameCodec.WriteFrameAsync(_stream, (byte)'>', frame, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _connected = false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _serialPort?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface ICompanionRadioFrameLink : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken);
    Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken);
}

public sealed class SerialCompanionRadioFrameLink : ICompanionRadioFrameLink
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _serialPort;
    private Stream? _stream;

    public SerialCompanionRadioFrameLink(string portName = "/dev/ttyUSB0", int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serialPort = new SerialPort(_portName, _baudRate)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        _serialPort.Open();
        _stream = _serialPort.BaseStream;
        return Task.CompletedTask;
    }

    public Task<byte[]> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Companion radio link has not been started.");
        }

        // Direction is reversed here: the external radio sends '>' frames to this interface bridge.
        return CompanionFrameCodec.ReadFrameAsync(_stream, (byte)'>', cancellationToken);
    }

    public Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Companion radio link has not been started.");
        }

        // And this host sends '<' app-command frames back to the external companion radio.
        return CompanionFrameCodec.WriteFrameAsync(_stream, (byte)'<', frame, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _serialPort?.Dispose();
        return ValueTask.CompletedTask;
    }
}
