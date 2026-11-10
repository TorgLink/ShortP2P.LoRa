using ShortP2P.LoRa.Jurisdiction;
using ShortP2P.LoRa.Link;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class LoRaTxPowerTests
{
    [Fact]
    public void UiChoices_Are_5_Through_22()
    {
        Assert.Equal(5, LoRaTxPower.UiChoices[0]);
        Assert.Equal(22, LoRaTxPower.UiChoices[^1]);
        Assert.Equal(18, LoRaTxPower.UiChoices.Count);
    }

    [Fact]
    public void DefaultForJurisdiction_Clamps_Region_Max_Into_Ui_Band()
    {
        Assert.Equal(22, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Unset));
        Assert.Equal(22, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Require("EU_868"))); // 27 → 22
        Assert.Equal(10, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Require("EU_433")));
        Assert.Equal(20, LoRaTxPower.DefaultForJurisdiction(JurisdictionCatalog.Require("RU")));
    }

    [Fact]
    public void EffectiveForBoard_Respects_Region_Cap()
    {
        var eu433 = JurisdictionCatalog.Require("EU_433");
        Assert.Equal(10, LoRaTxPower.EffectiveForBoard(22, eu433));
        Assert.Equal(8, LoRaTxPower.EffectiveForBoard(8, eu433));
        Assert.Equal(22, LoRaTxPower.EffectiveForBoard(22, JurisdictionCatalog.Unset));
    }

    [Fact]
    public void Admin_SetTxPower_Protobuf_Contains_Expected_Tags()
    {
        var bytes = MeshtasticProto.BuildAdminSetLoraTxPower(20, [1, 2, 3, 4]);
        // set_config = field 34 → tag 0x92 0x02 (34<<3|2 = 274 → multi-byte)
        Assert.Contains((byte)0x92, bytes); // low byte of set_config tag encoding starts path
        Assert.True(MeshtasticProto.TryReadAdminSessionPasskey(
            MeshtasticProto.BuildAdminSetLoraTxPower(10, [9, 8, 7]), out var key));
        // BuildAdminSet writes passkey; TryRead should find field 101
        Assert.Equal(new byte[] { 9, 8, 7 }, key);
    }
}
