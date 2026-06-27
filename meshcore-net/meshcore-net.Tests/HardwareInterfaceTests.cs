using System.Threading.Channels;
using MeshCoreNet;

namespace meshcore_net.Tests;

public class HardwareInterfaceTests
{
    [Fact]
    public async Task LoRaInterfaceTransmitsAndPublishesRadioFrames()
    {
        var radio = new FakeSx126xRadio();
        var meshInterface = new LinuxLoRaInterface("lora", DefaultLoRaOptions(), radio);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await meshInterface.StartAsync(cts.Token);
        var txTime = await meshInterface.TransmitAsync([1, 2, 3], cts.Token);
        await radio.EmitAsync(new RadioFrame([4, 5, 6], -88.5, 6.25), cts.Token);
        var frame = await meshInterface.ReceivedFrames.ReadAsync(cts.Token);

        Assert.True(radio.Initialized);
        Assert.Equal(12.5, txTime);
        Assert.Equal([1, 2, 3], radio.Transmitted.Single());
        Assert.Equal([4, 5, 6], frame.Packet);
        Assert.Equal(-88.5, frame.Rssi);
        Assert.Equal(6.25, frame.Snr);
        Assert.Equal(new RadioConfig(869_618, 62_500, 8, 8, 22, 27), meshInterface.GetRadioConfig());
    }

    [Fact]
    public void SxRadioFactorySelectsRequestedChipBackend()
    {
        var options = new LoRaOptions(
            SpiBus: 0,
            ChipSelect: 0,
            ResetPin: 18,
            BusyPin: 20,
            IrqPin: 16,
            TxEnablePin: 6,
            RxEnablePin: -1,
            WakePin: -1,
            Frequency: 869_618_000,
            SpreadingFactor: 8,
            Bandwidth: 62_500,
            CodingRate: 8,
            TxPower: 22,
            AirtimeDutyCycle: 10,
            Dio2RfSwitch: false,
            Dio3Voltage: null,
            Dio3TcxoDelay: null,
            ChipKind: "sx126x");

        var hal = SxRadioHalFactory.Create(options);

        Assert.IsType<Sx126xLinuxHal>(hal);
    }

    [Fact]
    public void Sx127xFrequencyRegisterUsesTheExpectedSemtechFormula()
    {
        var frf = ManagedSx127xRadio.BuildFrequencyRegister(869_618_000u);

        Assert.Equal(14_247_821u, frf);
    }

    [Fact]
    public void Sx127xBandwidthCodeMapsStandardValues()
    {
        Assert.Equal(6, ManagedSx127xRadio.Sx127xBandwidthCode(62_500));
        Assert.Equal(7, ManagedSx127xRadio.Sx127xBandwidthCode(125_000));
        Assert.Equal(8, ManagedSx127xRadio.Sx127xBandwidthCode(250_000));
        Assert.Equal(4, ManagedSx127xRadio.Sx127xBandwidthCode(31_250));
        // Unknown bandwidth falls back to 125 kHz (code 7).
        Assert.Equal(7, ManagedSx127xRadio.Sx127xBandwidthCode(99_999));
    }

    [Fact]
    public void Sx127xCodingRateCodeMapsAllSupportedRates()
    {
        Assert.Equal(1, ManagedSx127xRadio.Sx127xCodingRateCode(5)); // 4/5
        Assert.Equal(2, ManagedSx127xRadio.Sx127xCodingRateCode(6)); // 4/6
        Assert.Equal(3, ManagedSx127xRadio.Sx127xCodingRateCode(7)); // 4/7
        Assert.Equal(4, ManagedSx127xRadio.Sx127xCodingRateCode(8)); // 4/8
        // Unknown coding rate falls back to 4/5.
        Assert.Equal(1, ManagedSx127xRadio.Sx127xCodingRateCode(99));
    }

    [Fact]
    public void Sx127xLowDataRateOptimizeIsRequiredForSf11And12OnNarrowBw()
    {
        Assert.True(ManagedSx127xRadio.NeedLowDataRateOptimize(11, 125_000));
        Assert.True(ManagedSx127xRadio.NeedLowDataRateOptimize(12, 62_500));
        Assert.False(ManagedSx127xRadio.NeedLowDataRateOptimize(11, 250_000));
        Assert.False(ManagedSx127xRadio.NeedLowDataRateOptimize(8, 62_500));
    }

    [Fact]
    public void Sx127xModemConfigRegistersReflectConfiguredProfile()
    {
        // EU narrow: SF8, BW 62.5 kHz, CR 4/8
        var bwCode = ManagedSx127xRadio.Sx127xBandwidthCode(62_500);
        var crCode = ManagedSx127xRadio.Sx127xCodingRateCode(8);

        var modemConfig1 = (byte)((bwCode << 4) | (crCode << 1));
        var modemConfig2 = (byte)((8 << 4) | 0x04);

        // BW=6(62.5kHz), CR=4(4/8) → 0x68; SF8 with CRC → 0x84.
        Assert.Equal(0x68, modemConfig1);
        Assert.Equal(0x84, modemConfig2);

        // Confirm these are NOT the old hardcoded values (0x72 / 0x74) for SF7/125kHz/CR4/5.
        Assert.NotEqual(0x72, modemConfig1);
        Assert.NotEqual(0x74, modemConfig2);
    }

    [Fact]
    public void DraginoHatPresetUsesSx127xChipsetAndManualNssPin()
    {
        var options = LoRaHardwarePresets.DraginoLoRaHatV14("868");

        Assert.Equal("sx127x", options.ChipKind);
        Assert.Equal(25, options.ChipSelectPin);
        Assert.Equal(17, options.ResetPin);
        Assert.Equal(-1, options.BusyPin);
        Assert.Equal(4, options.IrqPin);
        Assert.Equal(-1, options.TxEnablePin);
        Assert.Equal(869_618_000u, options.Frequency);
    }

    [Fact]
    public void WaveshareHatPresetUsesSx126xDefaults()
    {
        var options = LoRaHardwarePresets.WaveshareHat("868");

        Assert.Equal("sx126x", options.ChipKind);
        Assert.Null(options.ChipSelectPin);
        Assert.Equal(18, options.ResetPin);
        Assert.Equal(20, options.BusyPin);
        Assert.Equal(16, options.IrqPin);
        Assert.Equal(6, options.TxEnablePin);
        Assert.True(options.Dio2RfSwitch);
        Assert.Equal(869_618_000u, options.Frequency);
    }

    private static LoRaOptions DefaultLoRaOptions() => new(
        SpiBus: 0,
        ChipSelect: 0,
        ResetPin: 18,
        BusyPin: 20,
        IrqPin: 16,
        TxEnablePin: 6,
        RxEnablePin: -1,
        WakePin: -1,
        Frequency: 869_618_000,
        SpreadingFactor: 8,
        Bandwidth: 62_500,
        CodingRate: 8,
        TxPower: 22,
        AirtimeDutyCycle: 10,
        Dio2RfSwitch: false,
        Dio3Voltage: null,
        Dio3TcxoDelay: null);

    private sealed class FakeSx126xRadio : ISx126xRadio
    {
        private readonly Channel<RadioFrame> _frames = System.Threading.Channels.Channel.CreateUnbounded<RadioFrame>();

        public bool Initialized { get; private set; }
        public List<byte[]> Transmitted { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task<double> TransmitAsync(byte[] packetData, CancellationToken cancellationToken)
        {
            Transmitted.Add(packetData);
            return Task.FromResult(12.5);
        }

        public async IAsyncEnumerable<RadioFrame> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public ValueTask EmitAsync(RadioFrame frame, CancellationToken cancellationToken)
        {
            return _frames.Writer.WriteAsync(frame, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
