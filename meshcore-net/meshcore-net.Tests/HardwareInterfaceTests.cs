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
