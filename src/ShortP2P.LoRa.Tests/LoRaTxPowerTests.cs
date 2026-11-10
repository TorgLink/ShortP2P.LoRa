using ShortP2P.LoRa.Jurisdiction;
using ShortP2P.LoRa.Link;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class LoRaTxPowerTests
{
    [Fact]
    public void UiChoices_Are_5_Through_22()
    {
        // Goal: UI TX-power choices match the product band 5–22 dBm inclusive.
        // Expected: first=5, last=22, exactly 18 discrete values.
        Assert.Equal(5, LoRaTxPower.UiChoices[0]);
        Assert.Equal(22, LoRaTxPower.UiChoices[^1]);
        Assert.Equal(18, LoRaTxPower.UiChoices.Count);
    }

    [Fact]
    public void DefaultForJurisdiction_Clamps_Region_Max_Into_Ui_Band()
    {
        // Goal: default TX power comes from jurisdiction PowerMaxDbm, clamped into the UI band.
        // Expected: Unset/EU_868 → 22; EU_433 → 10; RU → 20.
        Assert.Equal(22, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Unset));
        Assert.Equal(22, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Require("EU_868"))); // 27 → 22
        Assert.Equal(10, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Require("EU_433")));
        Assert.Equal(20, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Require("RU")));
    }

    [Fact]
    public void EffectiveForBoard_Respects_Region_Cap()
    {
        // Goal: board-applied power never exceeds the jurisdiction cap or the UI band.
        // Expected: EU_433 clamps 22→10 and keeps 8; Unset keeps 22.
        var eu433 = JurisdictionCatalog.Require("EU_433");
        Assert.Equal(10, LoRaTxPower.EffectiveForBoard(22, eu433));
        Assert.Equal(8, LoRaTxPower.EffectiveForBoard(8, eu433));
        Assert.Equal(22, LoRaTxPower.EffectiveForBoard(22, JurisdictionCatalog.Unset));
    }

    [Fact]
    public void PatchLoRaConfigTxPower_Keeps_Other_Fields_And_Sets_Power()
    {
        // Goal: merge tx_power into an existing LoRaConfig without dropping sibling fields.
        // Expected: region (field 7) survives; tx_power (field 10) becomes 20.
        // region=7 varint value 1 (EU_868), hop_limit=8 varint 3, previous tx_power=10 value 14
        byte[] existing =
        [
            0x38, 0x01, // field 7 = 1
            0x40, 0x03, // field 8 = 3
            0x50, 0x0E // field 10 = 14
        ];
        var patched = MeshtasticProto.PatchLoRaConfigTxPower(existing, 20);

        Assert.Contains((byte)0x38, patched);
        Assert.Contains((byte)0x40, patched);
        // field 10 tag 0x50, value 20 = 0x14
        Assert.Contains(patched, b => true);
        var idx = patched.AsSpan().LastIndexOf((byte)0x50);
        Assert.True(idx >= 0 && idx + 1 < patched.Length);
        Assert.Equal(20, patched[idx + 1]);
        Assert.Equal(1, patched.Count(b => b == 0x50)); // only one tx_power field
    }

    [Fact]
    public void Admin_SetFullLoraConfig_Protobuf_Contains_Expected_Tags()
    {
        // Goal: set_config wraps a full LoRaConfig and optional session_passkey.
        // Expected: set_config tag path present; passkey round-trips via TryRead.
        byte[] lora = [0x38, 0x01, 0x50, 0x14]; // region=1, tx_power=20
        var bytes = MeshtasticProto.BuildAdminSetLoraConfig(lora, [1, 2, 3, 4]);
        Assert.Contains((byte)0x92, bytes); // set_config tag low byte
        Assert.True(MeshtasticProto.TryReadAdminSessionPasskey(
            MeshtasticProto.BuildAdminSetLoraConfig(lora, [9, 8, 7]), out var key));
        Assert.Equal(new byte[] { 9, 8, 7 }, key);
    }

    [Fact]
    public void TryReadAdminGetConfigResponseLora_Extracts_Lora_Blob()
    {
        // Goal: parse get_config_response → Config.lora bytes.
        // Expected: returns the nested LoRaConfig payload.
        byte[] lora = [0x38, 0x01, 0x50, 0x0A];
        using var config = new MemoryStream();
        WriteTagLen(config, 6, lora); // Config.lora = 6
        using var admin = new MemoryStream();
        WriteTagLen(admin, 6, config.ToArray()); // get_config_response = 6

        Assert.True(MeshtasticProto.TryReadAdminGetConfigResponseLora(admin.ToArray(), out var parsed));
        Assert.Equal(lora, parsed);
    }

    private static void WriteTagLen(Stream stream, int field, byte[] bytes)
    {
        // tag = (field<<3)|2, then length, then bytes
        var tag = (uint)((field << 3) | 2);
        while (tag >= 0x80)
        {
            stream.WriteByte((byte)(tag | 0x80));
            tag >>= 7;
        }

        stream.WriteByte((byte)tag);
        var len = (uint)bytes.Length;
        while (len >= 0x80)
        {
            stream.WriteByte((byte)(len | 0x80));
            len >>= 7;
        }

        stream.WriteByte((byte)len);
        stream.Write(bytes, 0, bytes.Length);
    }
}
