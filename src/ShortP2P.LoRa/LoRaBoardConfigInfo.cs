using ShortP2P.LoRa.Jurisdiction;

namespace ShortP2P.LoRa;

/// <summary>
///     Snapshot of a board <c>Config.LoRaConfig</c>: raw protobuf bytes plus the fields
///     the host needs for duty cycle and UI (region, modem preset, TX power, duty cycle).
/// </summary>
/// <param name="OverrideDutyCycle">
///     Board field 12 (<c>override_duty_cycle</c>). When true the firmware may ignore the region
///     airtime cap; the host still applies its own software budget from <see cref="DutyCycleMaxPercent"/>.
/// </param>
/// <param name="DutyCycleMaxPercent">
///     Software TX budget percent derived from <see cref="RegionCode"/> via
///     <see cref="JurisdictionCatalog"/> (fallback 10 when the region is unknown or UNSET).
/// </param>
public sealed record LoRaBoardConfigInfo(
    byte[] RawBytes,
    int RegionProto,
    string RegionCode,
    int ModemPresetProto,
    string ModemPresetName,
    int TxPowerDbm,
    bool UsePreset,
    bool OverrideDutyCycle,
    double DutyCycleMaxPercent)
{
    public bool IsEmpty => RawBytes.Length == 0;

    /// <summary>Percent used by <see cref="MeshtasticGate.ConfigureDutyCycleAsync"/> for this board snapshot.</summary>
    public double EffectiveDutyCyclePercent
    {
        get
        {
            var regionCap = DutyCycleMaxPercent;
            if (regionCap <= 0)
                regionCap = MeshtasticGate.DefaultDutyCyclePercent;
            return Math.Min(MeshtasticGate.DefaultDutyCyclePercent, regionCap);
        }
    }

    public static double DutyCyclePercentForRegion(string? regionCode)
    {
        var profile = JurisdictionCatalog.Find(regionCode ?? "") ?? JurisdictionCatalog.Unset;
        if (profile.IsUnset || profile.DutyCycleMaxPercent <= 0)
            return MeshtasticGate.DefaultDutyCyclePercent;
        return profile.DutyCycleMaxPercent;
    }

    /// <summary>Parse a full board LoRaConfig protobuf (empty span → empty snapshot).</summary>
    public static LoRaBoardConfigInfo Parse(ReadOnlySpan<byte> loraConfig) =>
        Link.MeshtasticProto.ParseLoRaConfig(loraConfig);
}
