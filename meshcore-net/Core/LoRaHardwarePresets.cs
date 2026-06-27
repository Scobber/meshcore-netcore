namespace MeshCoreNet;

public static class LoRaHardwarePresets
{
    public static LoRaOptions GenericPiSx126x(string? profileOrRegion = null)
    {
        var profile = LoRaProfile.ResolveOrDefault(profileOrRegion);
        return new LoRaOptions(
            SpiBus: 0,
            ChipSelect: 0,
            ResetPin: 18,
            BusyPin: 20,
            IrqPin: 16,
            TxEnablePin: 6,
            RxEnablePin: -1,
            WakePin: -1,
            Frequency: profile.Frequency,
            SpreadingFactor: profile.SpreadingFactor,
            Bandwidth: profile.Bandwidth,
            CodingRate: profile.CodingRate,
            TxPower: profile.TxPower,
            AirtimeDutyCycle: 10,
            Dio2RfSwitch: false,
            Dio3Voltage: null,
            Dio3TcxoDelay: null,
            ChipKind: "sx126x",
            RequireHardware: true);
    }

    public static LoRaOptions DraginoLoRaHatV14(string? profileOrRegion = null)
    {
        var profile = LoRaProfile.ResolveOrDefault(profileOrRegion);
        return new LoRaOptions(
            SpiBus: 0,
            ChipSelect: 0,
            ResetPin: 17,
            BusyPin: -1,
            IrqPin: 4,
            TxEnablePin: -1,
            RxEnablePin: -1,
            WakePin: -1,
            Frequency: profile.Frequency,
            SpreadingFactor: profile.SpreadingFactor,
            Bandwidth: profile.Bandwidth,
            CodingRate: profile.CodingRate,
            TxPower: profile.TxPower,
            AirtimeDutyCycle: 10,
            Dio2RfSwitch: false,
            Dio3Voltage: null,
            Dio3TcxoDelay: null,
            ChipKind: "sx127x",
            RequireHardware: true,
            ChipSelectPin: 25);
    }

    public static LoRaOptions WaveshareHat(string? profileOrRegion = null)
    {
        return GenericPiSx126x(profileOrRegion) with
        {
            Dio2RfSwitch = true
        };
    }
}
