using ShortP2P.LoRa.Jurisdiction;
using ShortP2P.LoRa.Link;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class MeshtasticRegionTests
{
    [Fact]
    public void PatchLoRaConfigRegion_Keeps_Modem_Channel_And_TxPower()
    {
        // Goal: a region write patches field 7 only and does not clear sibling LoRaConfig fields.
        // Expected: modem preset, hop limit, tx power, and channel slot survive; region becomes RU (9).
        // field 2 modem_preset = 0, field 7 region = 1, field 8 hop_limit = 3,
        // field 10 tx_power = 14, field 11 channel_num = 5.
        byte[] existing =
        [
            0x10, 0x00,
            0x38, 0x01,
            0x40, 0x03,
            0x50, 0x0E,
            0x58, 0x05
        ];

        var patched = MeshtasticProto.PatchLoRaConfigRegion(existing, MeshtasticProto.RegionCode.Ru);

        Assert.Equal(MeshtasticProto.LoRaConfigRegionField, 7);
        Assert.Equal((byte)((MeshtasticProto.LoRaConfigRegionField << 3) | 0), (byte)0x38);
        Assert.Equal(
            new byte[]
            {
                0x10, 0x00,
                0x40, 0x03,
                0x50, 0x0E,
                0x58, 0x05,
                0x38, 0x09
            },
            patched);
        Assert.Equal(1, patched.Count(b => b == 0x38));
    }

    [Fact]
    public void Unset_Is_Not_A_Region_Write_And_Ru_Maps_To_The_Proto_Discriminant()
    {
        // Goal: UNSET must not be written; RU uses the RegionCode value already declared for the catalog.
        // Expected: TryGetCode is false for UNSET and true for RU with MeshtasticProto.RegionCode.Ru.
        Assert.False(MeshtasticRegionMap.TryGetCode(JurisdictionCatalog.UnsetCode, out _));
        Assert.False(MeshtasticRegionMap.TryGetCode(null, out _));
        Assert.True(MeshtasticRegionMap.TryGetCode("RU", out var ru));
        Assert.Equal(MeshtasticProto.RegionCode.Ru, ru);
        Assert.NotEqual(0u, ru);

        foreach (var region in JurisdictionCatalog.Regions)
        {
            if (region.IsUnset)
            {
                Assert.False(MeshtasticRegionMap.TryGetCode(region.Code, out _));
                continue;
            }

            Assert.True(MeshtasticRegionMap.TryGetCode(region.Code, out var code));
            Assert.NotEqual(MeshtasticProto.RegionCode.Unset, code);
        }
    }

    [Fact]
    public void PatchLoRaConfigRegion_Rejects_Unset()
    {
        // Goal: a zero region code is not turned into a partial config write.
        // Expected: ArgumentOutOfRangeException and no blob.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MeshtasticProto.PatchLoRaConfigRegion([0x10, 0x00, 0x50, 0x0E], MeshtasticProto.RegionCode.Unset));
    }
}
