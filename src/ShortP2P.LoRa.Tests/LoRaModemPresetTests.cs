using ShortP2P.LoRa.Link;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class LoRaModemPresetTests
{
    [Fact]
    public void Catalog_Is_Only_The_Universal_Six_And_Defaults_To_LongFast()
    {
        // Goal: the menu and the radio share one allow-list.
        // Expected: six presets, factory default LONG_FAST = 0, no turbo / removed / region-only names.
        var names = LoRaModemPresets.All.Select(p => p.Name).ToArray();
        Assert.Equal(
            ["SHORT_FAST", "SHORT_SLOW", "MEDIUM_FAST", "MEDIUM_SLOW", "LONG_FAST", "LONG_MODERATE"],
            names);
        Assert.Equal(0, LoRaModemPresets.Default.ProtoValue);
        Assert.Equal("LONG_FAST", LoRaModemPresets.NormalizeName(null));
        Assert.Equal("LONG_FAST", LoRaModemPresets.NormalizeName("SHORT_TURBO"));
        Assert.Equal("LONG_FAST", LoRaModemPresets.NormalizeName("LONG_SLOW"));
        Assert.Equal("LONG_FAST", LoRaModemPresets.NormalizeName("VERY_LONG_SLOW"));
        Assert.Equal("LONG_FAST", LoRaModemPresets.NormalizeName("LITE_FAST"));
        Assert.Equal("LONG_FAST", LoRaModemPresets.NormalizeName("NARROW_SLOW"));
        Assert.Equal("LONG_FAST", LoRaModemPresets.NormalizeName("TINY_FAST"));
        Assert.Equal("SHORT_SLOW", LoRaModemPresets.NormalizeName(" short_slow "));
    }

    [Fact]
    public void SnapSpeed_Drops_Illegal_Pairs_To_Fast()
    {
        // Goal: changing range never leaves a speed that preset family does not have.
        // Expected: Long has Fast and Moderate only; Short/Medium have Fast and Slow.
        Assert.Equal(LoRaPresetSpeed.Fast, LoRaModemPresets.SnapSpeed(LoRaPresetRange.Long, LoRaPresetSpeed.Slow));
        Assert.Equal(LoRaPresetSpeed.Moderate, LoRaModemPresets.SnapSpeed(LoRaPresetRange.Long, LoRaPresetSpeed.Moderate));
        Assert.Equal(LoRaPresetSpeed.Fast, LoRaModemPresets.SnapSpeed(LoRaPresetRange.Short, LoRaPresetSpeed.Moderate));
        Assert.Equal("LONG_FAST", LoRaModemPresets.Find(LoRaPresetRange.Long, LoRaPresetSpeed.Slow).Name);
        Assert.Equal("LONG_MODERATE", LoRaModemPresets.Find(LoRaPresetRange.Long, LoRaPresetSpeed.Moderate).Name);
        Assert.Equal("MEDIUM_SLOW", LoRaModemPresets.Find(LoRaPresetRange.Medium, LoRaPresetSpeed.Slow).Name);
    }

    [Fact]
    public void AirRate_Labels_Match_The_Published_Approximations()
    {
        Assert.Equal("10.9", LoRaModemPresets.GetOrDefault("SHORT_FAST").KilobitsText);
        Assert.Equal("6.25", LoRaModemPresets.GetOrDefault("SHORT_SLOW").KilobitsText);
        Assert.Equal("3.5", LoRaModemPresets.GetOrDefault("MEDIUM_FAST").KilobitsText);
        Assert.Equal("2.0", LoRaModemPresets.GetOrDefault("MEDIUM_SLOW").KilobitsText);
        Assert.Equal("1.1", LoRaModemPresets.GetOrDefault("LONG_FAST").KilobitsText);
        Assert.Equal("0.34", LoRaModemPresets.GetOrDefault("LONG_MODERATE").KilobitsText);
    }

    [Fact]
    public void PatchLoRaConfigModemPreset_Keeps_Region_Channel_And_TxPower()
    {
        // Goal: merge use_preset + modem_preset into an existing LoRaConfig.
        // Expected: region (7), sibling field 11, and tx_power (10) survive;
        // use_preset becomes true and modem_preset becomes LONG_MODERATE (7), replacing SHORT_TURBO.
        byte[] existing =
        [
            0x08, 0x00, // field 1 use_preset = false
            0x10, 0x08, // field 2 modem_preset = 8 SHORT_TURBO
            0x38, 0x01, // field 7 region = 1
            0x50, 0x0E, // field 10 tx_power = 14
            0x58, 0x04 // field 11 = 4 (kept; stands in for channel / other siblings)
        ];

        var patched = MeshtasticProto.PatchLoRaConfigModemPreset(existing, 7);
        var (values, counts) = ReadVarints(patched);

        Assert.Equal(1u, values[1]);
        Assert.Equal(7u, values[2]);
        Assert.Equal(1u, values[7]);
        Assert.Equal(14u, values[10]);
        Assert.Equal(4u, values[11]);
        Assert.Equal(1, counts[1]);
        Assert.Equal(1, counts[2]);
    }

    [Fact]
    public void PatchLoRaConfigModemPreset_Rejects_Presets_Outside_The_Intersection()
    {
        byte[] existing = [0x38, 0x01];
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshtasticProto.PatchLoRaConfigModemPreset(existing, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshtasticProto.PatchLoRaConfigModemPreset(existing, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshtasticProto.PatchLoRaConfigModemPreset(existing, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshtasticProto.PatchLoRaConfigModemPreset(existing, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshtasticProto.PatchLoRaConfigModemPreset(existing, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshtasticProto.PatchLoRaConfigModemPreset(existing, 10));
        Assert.Throws<ArgumentException>(() => MeshtasticProto.PatchLoRaConfigModemPreset([], 0));
    }

    [Fact]
    public void ParseLoRaConfig_Reads_Modem_Region_Power_And_Duty()
    {
        // Fields: use_preset=true, SHORT_TURBO, region EU_868 (=3), tx=14, override_duty_cycle=true.
        byte[] blob =
        [
            0x08, 0x01, // use_preset
            0x10, 0x08, // modem_preset SHORT_TURBO
            0x38, 0x03, // region EU_868
            0x50, 0x0E, // tx_power 14
            0x60, 0x01 // override_duty_cycle
        ];
        var info = LoRaBoardConfigInfo.Parse(blob);
        Assert.True(info.UsePreset);
        Assert.Equal("SHORT_TURBO", info.ModemPresetName);
        Assert.Equal("EU_868", info.RegionCode);
        Assert.Equal(14, info.TxPowerDbm);
        Assert.True(info.OverrideDutyCycle);
        Assert.Equal(10.0, info.DutyCycleMaxPercent); // EU_868 catalog cap
        Assert.Equal(10.0, info.EffectiveDutyCyclePercent);
        Assert.False(info.IsEmpty);
    }

    private static (Dictionary<int, uint> Values, Dictionary<int, int> Counts) ReadVarints(byte[] payload)
    {
        var values = new Dictionary<int, uint>();
        var counts = new Dictionary<int, int>();
        MeshtasticProto.ReadFields(payload, (number, wire, varint, _) =>
        {
            if (wire != 0)
                return;
            values[number] = varint;
            counts[number] = counts.GetValueOrDefault(number) + 1;
        });
        return (values, counts);
    }
}
