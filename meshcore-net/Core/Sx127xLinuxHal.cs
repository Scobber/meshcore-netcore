using System.Device.Gpio;
using System.Device.Spi;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MeshCoreNet;

public sealed class Sx127xLinuxHal : ISxRadioHal
{
    private readonly ManagedSx127xRadio _inner;

    public Sx127xLinuxHal(LoRaOptions options)
    {
        _inner = new ManagedSx127xRadio(options);
    }

    public Task InitializeAsync(CancellationToken cancellationToken) => _inner.InitializeAsync(cancellationToken);

    public Task<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken) => _inner.TransmitAsync(packetData, cancellationToken);

    public IAsyncEnumerable<RadioFrame> ReceiveAsync(CancellationToken cancellationToken) => _inner.ReceiveAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

public sealed class ManagedSx127xRadio : ISxRadioHal
{
    private const byte RegFifo = 0x00;
    private const byte RegOpMode = 0x01;
    private const byte RegFrMsb = 0x06;
    private const byte RegFrMid = 0x07;
    private const byte RegFrLsb = 0x08;
    private const byte RegPaConfig = 0x09;
    private const byte RegOcp = 0x0B;
    private const byte RegLna = 0x0C;
    private const byte RegFifoAddrPtr = 0x0D;
    private const byte RegFifoTxBaseAddr = 0x0E;
    private const byte RegFifoRxBaseAddr = 0x0F;
    private const byte RegFifoRxCurrentAddr = 0x10;
    private const byte RegIrqFlagsMask = 0x11;
    private const byte RegIrqFlags = 0x12;
    private const byte RegRxNbBytes = 0x13;
    private const byte RegPktSnrValue = 0x19;
    private const byte RegPktRssiValue = 0x1A;
    private const byte RegModemConfig1 = 0x1D;
    private const byte RegModemConfig2 = 0x1E;
    private const byte RegPreambleMsb = 0x20;
    private const byte RegPreambleLsb = 0x21;
    private const byte RegPayloadLength = 0x22;
    private const byte RegMaxPayloadLength = 0x23;
    private const byte RegHopPeriod = 0x24;
    private const byte RegModemConfig3 = 0x26;
    private const byte RegSyncWord = 0x39;
    private const byte RegDioMapping1 = 0x40;
    private const byte RegDioMapping2 = 0x41;
    private const byte RegPaDac = 0x4D;

    private const byte ModeSleep = 0x00;
    private const byte ModeStandby = 0x01;
    private const byte ModeTx = 0x03;
    private const byte ModeRxContinuous = 0x05;
    private const byte IrqTxDone = 0x08;
    private const byte IrqRxDone = 0x40;

    private readonly LoRaOptions _options;
    private SpiDevice? _spi;
    private GpioController? _gpio;
    private bool _initialized;

    public ManagedSx127xRadio(LoRaOptions options)
    {
        _options = options;
    }

    public static uint BuildFrequencyRegister(uint frequency)
    {
        return (uint)(((ulong)frequency << 19) / 32_000_000UL);
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
        if (_options.IrqPin >= 0)
        {
            OpenPin(_options.IrqPin, PinMode.Input);
        }
        if (_options.ChipSelectPin is int chipSelectPin && chipSelectPin >= 0)
        {
            OpenPin(chipSelectPin, PinMode.Output);
            _gpio?.Write(chipSelectPin, PinValue.High);
        }

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

        WriteRegister(RegOpMode, 0x80); // LoRa mode, sleep
        WriteRegister(RegFifoTxBaseAddr, 0x00);
        WriteRegister(RegFifoRxBaseAddr, 0x00);
        WriteRegister(RegLna, 0x03);
        var bwCode = Sx127xBandwidthCode(_options.Bandwidth);
        var crCode = Sx127xCodingRateCode(_options.CodingRate);
        var sf = (byte)Math.Clamp((int)_options.SpreadingFactor, 6, 12);
        // RegModemConfig1: bits[7:4]=BW, bits[3:1]=CR, bit[0]=explicit-header(0)
        var modemConfig1 = (byte)((bwCode << 4) | (crCode << 1));
        // RegModemConfig2: bits[7:4]=SF, bit[2]=RxPayloadCrcOn(1)
        var modemConfig2 = (byte)((sf << 4) | 0x04);
        // RegModemConfig3: bit[3]=LowDataRateOptimize (required for SF11/SF12 with BW<=125kHz), bit[2]=AgcAutoOn
        var modemConfig3 = (byte)(NeedLowDataRateOptimize(sf, _options.Bandwidth) ? 0x0C : 0x04);

        WriteRegister(RegModemConfig1, modemConfig1);
        WriteRegister(RegModemConfig2, modemConfig2);
        WriteRegister(RegModemConfig3, modemConfig3);
        WriteRegister(RegPreambleMsb, 0x00);
        WriteRegister(RegPreambleLsb, 0x08);
        WriteRegister(RegPayloadLength, 0x00);
        WriteRegister(RegMaxPayloadLength, 0xFF);
        WriteRegister(RegHopPeriod, 0x00);
        WriteRegister(RegSyncWord, 0x12);
        WriteRegister(RegDioMapping1, 0x00);
        WriteRegister(RegDioMapping2, 0x00);
        SetFrequency(_options.Frequency);
        WriteRegister(RegPaConfig, (byte)(0x80 | Math.Clamp((int)_options.TxPower, 2, 20)));
        WriteRegister(RegPaDac, 0x87);
        WriteRegister(RegOcp, 0x0B);
        WriteRegister(RegOpMode, ModeStandby);

        Console.WriteLine($"SX127x LoRa initialized: freq={_options.Frequency} Hz sf={sf} bw={_options.Bandwidth} Hz cr=4/{_options.CodingRate} txpower={_options.TxPower} dBm ldro={NeedLowDataRateOptimize(sf, _options.Bandwidth)}");
        _initialized = true;
    }

    public async Task<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
    {
        ThrowIfNotInitialized();

        SetRxTx(tx: true);
        WriteRegister(RegIrqFlags, 0xFF);
        WriteRegister(RegFifoAddrPtr, 0x00);
        WriteRegister(RegPayloadLength, (byte)packetData.Length);
        WriteFifo(packetData);
        WriteRegister(RegOpMode, ModeTx);

        var sw = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            var flags = ReadRegister(RegIrqFlags);
            if ((flags & IrqTxDone) != 0)
            {
                WriteRegister(RegIrqFlags, 0xFF);
                SetRxTx(tx: false);
                return sw.Elapsed.TotalMilliseconds;
            }

            if ((flags & 0x20) != 0)
            {
                WriteRegister(RegIrqFlags, 0xFF);
                SetRxTx(tx: false);
                return 0;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    public async IAsyncEnumerable<RadioFrame> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfNotInitialized();

        WriteRegister(RegIrqFlags, 0xFF);
        WriteRegister(RegFifoAddrPtr, 0x00);
        WriteRegister(RegPayloadLength, 0x00);
        WriteRegister(RegOpMode, ModeRxContinuous);

        while (!cancellationToken.IsCancellationRequested)
        {
            var flags = ReadRegister(RegIrqFlags);
            if ((flags & IrqRxDone) != 0)
            {
                var length = ReadRegister(RegRxNbBytes);
                var payload = ReadFifo(length);
                var snr = (sbyte)ReadRegister(RegPktSnrValue) / 4d;
                var rssi = -ReadRegister(RegPktRssiValue) / 2d;
                WriteRegister(RegIrqFlags, 0xFF);
                yield return new RadioFrame(payload, rssi, snr);
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _spi?.Dispose();
        _gpio?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void SetFrequency(uint frequency)
    {
        var frf = BuildFrequencyRegister(frequency);
        WriteRegister(RegFrMsb, (byte)(frf >> 16));
        WriteRegister(RegFrMid, (byte)(frf >> 8));
        WriteRegister(RegFrLsb, (byte)frf);
    }

    private void WriteRegister(byte register, byte value)
    {
        if (_spi is null)
        {
            throw new InvalidOperationException("SX127x radio is not initialized.");
        }

        var write = new byte[] { (byte)(0x80 | register), value };
        WithChipSelected(spi => spi.Write(write));
    }

    private byte ReadRegister(byte register)
    {
        if (_spi is null)
        {
            throw new InvalidOperationException("SX127x radio is not initialized.");
        }

        var write = new byte[] { register, 0x00 };
        var read = new byte[2];
        WithChipSelected(spi => spi.TransferFullDuplex(write, read));
        return read[1];
    }

    private void WriteFifo(byte[] payload)
    {
        if (_spi is null)
        {
            throw new InvalidOperationException("SX127x radio is not initialized.");
        }

        var write = new byte[payload.Length + 1];
        write[0] = RegFifo;
        payload.CopyTo(write, 1);
        WithChipSelected(spi => spi.Write(write));
    }

    private byte[] ReadFifo(int length)
    {
        if (_spi is null)
        {
            throw new InvalidOperationException("SX127x radio is not initialized.");
        }

        var write = new byte[length + 1];
        var read = new byte[length + 1];
        write[0] = RegFifo;
        WithChipSelected(spi => spi.TransferFullDuplex(write, read));
        return read[1..];
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

    private void WithChipSelected(Action<SpiDevice> action)
    {
        if (_spi is null)
        {
            throw new InvalidOperationException("SX127x radio is not initialized.");
        }

        if (_gpio is null || _options.ChipSelectPin is not int chipSelectPin || chipSelectPin < 0)
        {
            action(_spi);
            return;
        }

        _gpio.Write(chipSelectPin, PinValue.Low);
        try
        {
            action(_spi);
        }
        finally
        {
            _gpio.Write(chipSelectPin, PinValue.High);
        }
    }

    private void SetRxTx(bool tx)
    {
        if (_gpio is null)
        {
            return;
        }

        if (_options.TxEnablePin >= 0)
        {
            _gpio.Write(_options.TxEnablePin, tx ? PinValue.High : PinValue.Low);
        }

        if (_options.RxEnablePin >= 0)
        {
            _gpio.Write(_options.RxEnablePin, tx ? PinValue.Low : PinValue.High);
        }
    }

    private void ThrowIfNotInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SX127x radio is not initialized.");
        }
    }

    // SX127x (SX1276) RegModemConfig1 bits [7:4] — bandwidth code table (datasheet table 41).
    public static byte Sx127xBandwidthCode(uint bandwidth) => bandwidth switch
    {
        7_800 or 7_810 => 0,
        10_400 or 10_420 => 1,
        15_600 or 15_630 => 2,
        20_800 or 20_830 => 3,
        31_250 => 4,
        41_700 => 5,
        62_500 => 6,
        125_000 => 7,
        250_000 => 8,
        500_000 => 9,
        _ => 7  // default: 125 kHz
    };

    // SX127x RegModemConfig1 bits [3:1] — coding-rate code. CR 4/5=1, 4/6=2, 4/7=3, 4/8=4.
    public static byte Sx127xCodingRateCode(byte codingRate) => codingRate switch
    {
        5 => 1,
        6 => 2,
        7 => 3,
        8 => 4,
        _ => 1  // default: 4/5
    };

    // LowDataRateOptimize (RegModemConfig3 bit 3) is mandatory for SF11/SF12 when BW<=125kHz.
    public static bool NeedLowDataRateOptimize(byte spreadingFactor, uint bandwidth)
        => spreadingFactor >= 11 && bandwidth <= 125_000;
}
