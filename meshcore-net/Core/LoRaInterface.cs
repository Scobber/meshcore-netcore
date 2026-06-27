using System.Device.Gpio;
using System.Device.Spi;
using System.Diagnostics;

namespace MeshCoreNet;

/// <summary>
/// Linux SX126x configuration values parsed from the MeshCore TOML interface section.
/// </summary>
public sealed record LoRaOptions(
    int SpiBus,
    int ChipSelect,
    int ResetPin,
    int BusyPin,
    int IrqPin,
    int TxEnablePin,
    int RxEnablePin,
    int WakePin,
    uint Frequency,
    byte SpreadingFactor,
    uint Bandwidth,
    byte CodingRate,
    sbyte TxPower,
    double AirtimeDutyCycle,
    bool Dio2RfSwitch,
    double? Dio3Voltage,
    double? Dio3TcxoDelay,
    string? ChipKind = "sx126x",
    bool RequireHardware = true,
    int? ChipSelectPin = null);

public class LinuxLoRaInterface : MeshInterfaceBase
{
    private readonly ISxRadioHal _radio;
    private readonly LoRaOptions _options;
    private readonly Queue<(DateTimeOffset At, double Ms)> _airtime = new();
    private bool _hardwareReady = true;

    public LinuxLoRaInterface(string name, LoRaOptions options, ISxRadioHal? radio = null)
        : base(name, "lora")
    {
        _options = options;
        _radio = radio ?? SxRadioHalFactory.Create(options);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _radio.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);
        }
        catch (DllNotFoundException ex) when (ex.Message.Contains("libgpiod", StringComparison.OrdinalIgnoreCase))
        {
            if (_options.RequireHardware)
            {
                throw new InvalidOperationException(
                    $"LoRa interface '{Name}' requires libgpiod but it is unavailable: {ex.Message}", ex);
            }

            _hardwareReady = false;
            Console.WriteLine($"LoRa interface '{Name}' is running in degraded mode because libgpiod is unavailable: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex) when (ex.Message.Contains("libgpiod", StringComparison.OrdinalIgnoreCase))
        {
            if (_options.RequireHardware)
            {
                throw new InvalidOperationException(
                    $"LoRa interface '{Name}' requires libgpiod ABI compatibility but it is unavailable: {ex.Message}", ex);
            }

            _hardwareReady = false;
            Console.WriteLine($"LoRa interface '{Name}' is running in degraded mode because libgpiod ABI is incompatible: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex) when (ex.Message.Contains("gpio", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            if (_options.RequireHardware)
            {
                throw new InvalidOperationException(
                    $"LoRa interface '{Name}' requires GPIO access but the service does not have sufficient permissions. Please run with appropriate privileges (root/sudo) or set require_hardware=false in configuration: {ex.Message}", ex);
            }

            _hardwareReady = false;
            Console.WriteLine($"LoRa interface '{Name}' is running in degraded mode due to insufficient GPIO permissions: {ex.Message}");
        }
        catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException inner && inner.Message.Contains("libgpiod", StringComparison.OrdinalIgnoreCase))
        {
            if (_options.RequireHardware)
            {
                throw new InvalidOperationException(
                    $"LoRa interface '{Name}' requires libgpiod but it is unavailable: {inner.Message}", inner);
            }

            _hardwareReady = false;
            Console.WriteLine($"LoRa interface '{Name}' is running in degraded mode because libgpiod is unavailable: {inner.Message}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not initialized", StringComparison.OrdinalIgnoreCase))
        {
            _hardwareReady = false;
            Console.WriteLine($"LoRa interface '{Name}' is running in degraded mode: {ex.Message}");
        }
    }

    public override async ValueTask<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        if (!_hardwareReady)
        {
            Console.WriteLine($"LoRa interface '{Name}' dropped {packetData.Length} bytes because radio hardware is unavailable.");
            return 0;
        }

        var txTime = await _radio.TransmitAsync(packetData, cancellationToken).ConfigureAwait(false);
        _airtime.Enqueue((DateTimeOffset.UtcNow, txTime));
        while (_airtime.Count > 5)
        {
            _airtime.Dequeue();
        }

        return txTime;
    }

    public override TimeSpan TransmitWait()
    {
        // Python uses the last five transmissions to approximate duty-cycle pressure.
        if (_airtime.Count < 5)
        {
            return TimeSpan.Zero;
        }

        var earliest = _airtime.Peek().At;
        var period = Math.Max(0.001, (DateTimeOffset.UtcNow - earliest).TotalSeconds);
        var total = _airtime.Sum(item => item.Ms) / 1000d;
        var dutyCycle = 100 * total / period;

        for (var c = 0; c < 3; c++)
        {
            var fraction = 1d / (1 << c);
            var threshold = _options.AirtimeDutyCycle * fraction;
            if (dutyCycle > threshold)
            {
                var sleep = (earliest + TimeSpan.FromSeconds(total / (threshold / 100d)) - DateTimeOffset.UtcNow).TotalSeconds * fraction;
                if (sleep > 0)
                {
                    return TimeSpan.FromSeconds(sleep);
                }
            }
        }

        return TimeSpan.Zero;
    }

    public override RadioConfig GetRadioConfig()
    {
        return new RadioConfig(_options.Frequency / 1000, _options.Bandwidth, _options.SpreadingFactor, _options.CodingRate, (byte)_options.TxPower, 27);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in _radio.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReceivedFrameWriter.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }
}

