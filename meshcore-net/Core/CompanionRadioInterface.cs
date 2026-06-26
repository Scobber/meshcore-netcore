namespace MeshCoreNet;

/// <summary>
/// Treats an external serial companion radio as a mesh transport.
/// </summary>
public sealed class CompanionRadioMeshInterface : MeshInterfaceBase
{
    public const byte CmdSendRawPacket = 0xc0;

    private readonly ICompanionRadioFrameLink _link;
    private bool _connected;
    private bool _txWarningShown;
    private RadioConfig _radioConfig = RadioConfig.Empty;

    public CompanionRadioMeshInterface(string name, ICompanionRadioFrameLink link)
        : base(name, "companion")
    {
        _link = link;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RadioLoopAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public override async ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            Console.WriteLine("Cannot send, companion radio is not connected.");
            return 0;
        }

        await _link.SendFrameAsync([CmdSendRawPacket, .. packetData], cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override RadioConfig GetRadioConfig() => _radioConfig;

    private async Task RadioLoopAsync(CancellationToken cancellationToken)
    {
        // Reconnect loop mirrors Python's companion interface behavior when a serial radio disappears.
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var start = DateTimeOffset.UtcNow;
            try
            {
                await ConnectAndReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Companion radio interface error: {ex.Message}");
            }

            _connected = false;
            if (DateTimeOffset.UtcNow - start > TimeSpan.FromSeconds(2))
            {
                delay = TimeSpan.FromSeconds(1);
            }
            else
            {
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAndReadAsync(CancellationToken cancellationToken)
    {
        await _link.StartAsync(cancellationToken).ConfigureAwait(false);
        // Query first so the bridge can expose channel/radio details back to companion devices.
        await _link.SendFrameAsync([CompanionRadioProtocol.CmdDeviceQuery, 3], cancellationToken).ConfigureAwait(false);
        var frame = await _link.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame.Length < 20 || frame[0] != CompanionRadioProtocol.RespDeviceInfo)
        {
            throw new InvalidOperationException("Invalid response to companion radio device query.");
        }

        // App start subscribes this bridge to raw packet pushes from the external radio.
        await _link.SendFrameAsync([CompanionRadioProtocol.CmdAppStart, 1, 0, 0, 0, 0, 0, 0, .. "CompanionInterface"u8.ToArray()], cancellationToken).ConfigureAwait(false);
        frame = await _link.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame.Length < 58 || frame[0] != CompanionRadioProtocol.RespSelfInfo)
        {
            throw new InvalidOperationException("Invalid response to companion radio app start.");
        }

        _radioConfig = new RadioConfig(
            BitConverter.ToUInt32(frame, 48),
            BitConverter.ToUInt32(frame, 52),
            frame[56],
            frame[57],
            frame[2],
            frame[3]);
        _connected = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            frame = await _link.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Length == 2 && frame[0] == CompanionRadioProtocol.RespErr)
            {
                if (frame[1] == CompanionRadioProtocol.ErrUnsupportedCommand && !_txWarningShown)
                {
                    Console.WriteLine("Companion radio is receive-only; transmit disabled.");
                    _txWarningShown = true;
                }

                continue;
            }

            if (frame.Length >= 3 && frame[0] == CompanionRadioProtocol.PushLogRxData)
            {
                // PushLogRxData payload is snr*4, rssi, then raw MeshCore packet bytes.
                var snr = unchecked((sbyte)frame[1]) / 4d;
                var rssi = unchecked((sbyte)frame[2]);
                await ReceivedFrameWriter.WriteAsync(new RadioFrame(frame[3..], rssi, snr), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
