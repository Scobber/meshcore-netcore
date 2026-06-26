using System.Device.Gpio;
using System.Device.Spi;
using System.Diagnostics;

namespace MeshCoreNet;

public sealed class Sx126xLinuxHal : ISxRadioHal
{
    // SX126x command opcodes. Keep these near the driver so hardware fixes are easy to audit.
    private const byte GetStatus = 0xC0;
    private const byte SetStandby = 0x80;
    private const byte SetPacketType = 0x8A;
    private const byte SetRfFrequency = 0x86;
    private const byte SetPaConfig = 0x95;
    private const byte SetTxParams = 0x8E;
    private const byte SetBufferBaseAddress = 0x8F;
    private const byte WriteBuffer = 0x0E;
    private const byte ReadBuffer = 0x1E;
    private const byte SetModulationParams = 0x8B;
    private const byte SetPacketParams = 0x8C;
    private const byte SetDioIrqParams = 0x08;
    private const byte GetIrqStatus = 0x12;
    private const byte ClearIrqStatus = 0x02;
    private const byte SetRx = 0x82;
    private const byte SetTx = 0x83;
    private const byte GetRxBufferStatus = 0x13;
    private const byte GetPacketStatus = 0x14;
    private const byte SetDio2AsRfSwitchCtrl = 0x9D;
    private const byte SetDio3AsTcxoCtrl = 0x97;
    private const ushort IrqTxDone = 0x0001;
    private const ushort IrqRxDone = 0x0002;
    private const ushort IrqTimeout = 0x0200;

    private readonly LoRaOptions _options;
    private SpiDevice? _spi;
    private GpioController? _gpio;

    public Sx126xLinuxHal(LoRaOptions options)
    {
        _options = options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var settings = new SpiConnectionSettings(_options.SpiBus, _options.ChipSelect)
        {
            ClockFrequency = 1_000_000,
            Mode = SpiMode.Mode0
        };
        _spi = SpiDevice.Create(settings);
        _gpio = GpioControllerFactory.Create();

        OpenPin(_options.ResetPin, PinMode.Output);
        OpenPin(_options.BusyPin, PinMode.Input);
        if (_options.TxEnablePin >= 0)
        {
            OpenPin(_options.TxEnablePin, PinMode.Output);
        }

        if (_options.RxEnablePin >= 0)
        {
            OpenPin(_options.RxEnablePin, PinMode.Output);
        }

        Reset();
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        // The init sequence follows the LoRaRF defaults used by the Python host.
        Command(SetStandby, [0x00]);
        Command(SetPacketType, [0x01]);
        Command(SetRfFrequency, FrequencyBytes(_options.Frequency));
        Command(SetPaConfig, [0x04, 0x07, 0x00, 0x01]);
        Command(SetTxParams, [(byte)_options.TxPower, 0x04]);
        Command(SetBufferBaseAddress, [0x00, 0x00]);
        Command(SetModulationParams, [_options.SpreadingFactor, BandwidthCode(_options.Bandwidth), CodingRateCode(_options.CodingRate), 0x00]);
        Command(SetPacketParams, [0x00, 0x10, 0x00, 0xff, 0x01, 0x00]);
        if (_options.Dio2RfSwitch)
        {
            Command(SetDio2AsRfSwitchCtrl, [0x01]);
        }

        if (_options.Dio3Voltage is not null && _options.Dio3TcxoDelay is not null)
        {
            Command(SetDio3AsTcxoCtrl, [Dio3VoltageCode(_options.Dio3Voltage.Value), .. TcxoDelayBytes(_options.Dio3TcxoDelay.Value)]);
        }
    }

    public async Task<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        if (_spi is null)
        {
            throw new InvalidOperationException("LoRa radio is not initialized.");
        }

        SetRxTx(tx: true);
        Command(WriteBuffer, [0x00, .. packetData]);
        Command(SetDioIrqParams, UInt16Pair(IrqTxDone | IrqTimeout, IrqTxDone | IrqTimeout, 0, 0));
        Command(ClearIrqStatus, UInt16(IrqTxDone | IrqTimeout));
        var sw = Stopwatch.StartNew();
        Command(SetTx, [0x00, 0x00, 0x00]);

        while (!cancellationToken.IsCancellationRequested)
        {
            var irq = ReadIrq();
            if ((irq & IrqTxDone) != 0)
            {
                Command(ClearIrqStatus, UInt16(IrqTxDone | IrqTimeout));
                SetRxTx(tx: false);
                return sw.Elapsed.TotalMilliseconds;
            }

            if ((irq & IrqTimeout) != 0 || sw.Elapsed > TimeSpan.FromSeconds(5))
            {
                Command(ClearIrqStatus, UInt16(IrqTxDone | IrqTimeout));
                SetRxTx(tx: false);
                return 0;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    public async IAsyncEnumerable<RadioFrame> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Continuous RX is polled here because the Python LoRaRF path also busy-waited on chip status.
        Command(SetDioIrqParams, UInt16Pair(IrqRxDone | IrqTimeout, IrqRxDone | IrqTimeout, 0, 0));
        Command(ClearIrqStatus, UInt16(0xffff));
        Command(SetRx, [0xff, 0xff, 0xff]);
        SetRxTx(tx: false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var irq = ReadIrq();
            if ((irq & IrqRxDone) != 0)
            {
                var status = ReadCommand(GetRxBufferStatus, 2);
                var length = status[0];
                var offset = status[1];
                var packet = ReadBufferBytes(offset, length);
                var packetStatus = ReadCommand(GetPacketStatus, 3);
                var rssi = packetStatus.Length > 0 ? -packetStatus[0] / 2d : 0;
                var snr = packetStatus.Length > 1 ? unchecked((sbyte)packetStatus[1]) / 4d : 0;
                Command(ClearIrqStatus, UInt16(IrqRxDone | IrqTimeout));
                yield return new RadioFrame(packet, rssi, snr);
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _spi?.Dispose();
        _gpio?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OpenPin(int pin, PinMode mode)
    {
        if (pin < 0 || _gpio is null)
        {
            return;
        }

        try
        {
            if (!_gpio.IsPinOpen(pin))
            {
                _gpio.OpenPin(pin, mode);
            }
            else
            {
                _gpio.SetPinMode(pin, mode);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Unable to open GPIO pin {pin} - insufficient permissions. Please run with root privileges or use a GPIO driver that doesn't require elevated permissions.", ex);
        }
    }

    private void Reset()
    {
        if (_gpio is null || _options.ResetPin < 0)
        {
            return;
        }

        _gpio.Write(_options.ResetPin, PinValue.Low);
        Thread.Sleep(10);
        _gpio.Write(_options.ResetPin, PinValue.High);
        Thread.Sleep(10);
    }

    private void SetRxTx(bool tx)
    {
        if (_gpio is null)
        {
            return;
        }

        if (_options.TxEnablePin >= 0)
        {
            // Some boards expose explicit TX/RX switch GPIOs instead of using DIO2.
            _gpio.Write(_options.TxEnablePin, tx ? PinValue.High : PinValue.Low);
        }

        if (_options.RxEnablePin >= 0)
        {
            _gpio.Write(_options.RxEnablePin, tx ? PinValue.Low : PinValue.High);
        }
    }

    private void Command(byte opcode, ReadOnlySpan<byte> payload)
    {
        // All commands wait before and after SPI access because BUSY can assert for either phase.
        WaitBusy();
        var write = new byte[1 + payload.Length];
        write[0] = opcode;
        payload.CopyTo(write.AsSpan(1));
        _spi!.Write(write);
        WaitBusy();
    }

    private byte[] ReadCommand(byte opcode, int length)
    {
        WaitBusy();
        var write = new byte[2 + length];
        var read = new byte[2 + length];
        write[0] = opcode;
        _spi!.TransferFullDuplex(write, read);
        WaitBusy();
        return read[2..];
    }

    private byte[] ReadBufferBytes(byte offset, byte length)
    {
        WaitBusy();
        var write = new byte[2 + length];
        var read = new byte[2 + length];
        write[0] = ReadBuffer;
        write[1] = offset;
        _spi!.TransferFullDuplex(write, read);
        WaitBusy();
        return read[2..];
    }

    private ushort ReadIrq()
    {
        var bytes = ReadCommand(GetIrqStatus, 2);
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }

    private void WaitBusy()
    {
        if (_gpio is null || _options.BusyPin < 0)
        {
            return;
        }

        var start = Stopwatch.StartNew();
        while (_gpio.Read(_options.BusyPin) == PinValue.High)
        {
            if (start.Elapsed > TimeSpan.FromSeconds(1))
            {
                throw new TimeoutException("Timed out waiting for SX126x BUSY pin.");
            }

            Thread.Sleep(1);
        }
    }

    private static byte[] FrequencyBytes(uint frequency)
    {
        // SX126x RF frequency register is frequency * 2^25 / 32 MHz, big-endian.
        var rfFreq = (uint)Math.Round(frequency * Math.Pow(2, 25) / 32_000_000d);
        return [(byte)(rfFreq >> 24), (byte)(rfFreq >> 16), (byte)(rfFreq >> 8), (byte)rfFreq];
    }

    private static byte BandwidthCode(uint bandwidth) => bandwidth switch
    {
        7_800 or 7_810 => 0x00,
        10_400 or 10_420 => 0x08,
        15_600 or 15_630 => 0x01,
        20_800 or 20_830 => 0x09,
        31_250 => 0x02,
        41_700 => 0x0A,
        62_500 => 0x03,
        125_000 => 0x04,
        250_000 => 0x05,
        500_000 => 0x06,
        _ => 0x03
    };

    private static byte CodingRateCode(byte codingRate) => codingRate switch
    {
        5 => 0x01,
        6 => 0x02,
        7 => 0x03,
        8 => 0x04,
        _ => codingRate
    };

    private static byte Dio3VoltageCode(double voltage) => voltage switch
    {
        1.6 => 0x00,
        1.7 => 0x01,
        1.8 => 0x02,
        2.2 => 0x03,
        2.4 => 0x04,
        2.7 => 0x05,
        3.0 => 0x06,
        3.3 => 0x07,
        _ => throw new ArgumentOutOfRangeException(nameof(voltage), "Invalid DIO3 TCXO voltage.")
    };

    private static byte[] TcxoDelayBytes(double delayMs)
    {
        var delay = delayMs switch
        {
            2.5 => 0x0140,
            5 => 0x0280,
            10 => 0x0560,
            _ => throw new ArgumentOutOfRangeException(nameof(delayMs), "Invalid DIO3 TCXO delay.")
        };
        return [(byte)(delay >> 16), (byte)(delay >> 8), (byte)delay];
    }

    private static byte[] UInt16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] UInt16Pair(ushort a, ushort b, ushort c, ushort d)
    {
        return [.. UInt16(a), .. UInt16(b), .. UInt16(c), .. UInt16(d)];
    }
}
