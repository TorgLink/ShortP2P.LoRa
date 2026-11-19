using ShortP2P.LoRa.Jurisdiction;

namespace ShortP2P.LoRa.Link;

/// <summary>
///     JurisdictionCatalog code → Meshtastic <see cref="MeshtasticProto.RegionCode"/>.
///     Only discriminants declared on <see cref="MeshtasticProto"/> are used.
///     UNSET and unknown codes are not written.
/// </summary>
internal static class MeshtasticRegionMap
{
    private static readonly Dictionary<string, uint> ByCatalogCode = new(StringComparer.Ordinal)
    {
        ["US"] = MeshtasticProto.RegionCode.Us,
        ["EU_433"] = MeshtasticProto.RegionCode.Eu433,
        ["EU_868"] = MeshtasticProto.RegionCode.Eu868,
        ["EU_866"] = MeshtasticProto.RegionCode.Eu866,
        ["EU_N_868"] = MeshtasticProto.RegionCode.EuN868,
        ["CN"] = MeshtasticProto.RegionCode.Cn,
        ["JP"] = MeshtasticProto.RegionCode.Jp,
        ["ANZ"] = MeshtasticProto.RegionCode.Anz,
        ["ANZ_433"] = MeshtasticProto.RegionCode.Anz433,
        ["KR"] = MeshtasticProto.RegionCode.Kr,
        ["TW"] = MeshtasticProto.RegionCode.Tw,
        ["RU"] = MeshtasticProto.RegionCode.Ru,
        ["IN"] = MeshtasticProto.RegionCode.In,
        ["NZ_865"] = MeshtasticProto.RegionCode.Nz865,
        ["TH"] = MeshtasticProto.RegionCode.Th,
        ["UA_433"] = MeshtasticProto.RegionCode.Ua433,
        ["MY_433"] = MeshtasticProto.RegionCode.My433,
        ["MY_919"] = MeshtasticProto.RegionCode.My919,
        ["SG_923"] = MeshtasticProto.RegionCode.Sg923,
        ["KZ_433"] = MeshtasticProto.RegionCode.Kz433,
        ["KZ_863"] = MeshtasticProto.RegionCode.Kz863,
        ["BR_902"] = MeshtasticProto.RegionCode.Br902,
        ["PH_433"] = MeshtasticProto.RegionCode.Ph433,
        ["PH_868"] = MeshtasticProto.RegionCode.Ph868,
        ["PH_915"] = MeshtasticProto.RegionCode.Ph915,
        ["NP_865"] = MeshtasticProto.RegionCode.Np865,
        ["LORA_24"] = MeshtasticProto.RegionCode.Lora24
    };

    /// <summary>False for UNSET, blank, and any catalog code that has no Meshtastic discriminant.</summary>
    public static bool TryGetCode(string? catalogCode, out uint regionCode)
    {
        regionCode = MeshtasticProto.RegionCode.Unset;
        if (string.IsNullOrWhiteSpace(catalogCode))
            return false;
        if (string.Equals(catalogCode, JurisdictionCatalog.UnsetCode, StringComparison.Ordinal))
            return false;
        return ByCatalogCode.TryGetValue(catalogCode, out regionCode)
               && regionCode != MeshtasticProto.RegionCode.Unset;
    }
}
