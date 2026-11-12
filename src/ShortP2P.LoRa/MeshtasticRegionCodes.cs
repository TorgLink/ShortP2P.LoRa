namespace ShortP2P.LoRa;

/// <summary>
///     Maps Meshtastic <c>Config.LoRaConfig.RegionCode</c> numbers to jurisdiction codes
///     used by <see cref="Jurisdiction.JurisdictionCatalog"/>. Numbers follow config.proto.
/// </summary>
public static class MeshtasticRegionCodes
{
    /// <summary>Known RegionCode → catalog code. Unknown numbers stay "UNSET".</summary>
    private static readonly Dictionary<int, string> ByProto = new()
    {
        [0] = "UNSET",
        [1] = "US",
        [2] = "EU_433",
        [3] = "EU_868",
        [4] = "CN",
        [5] = "JP",
        [6] = "ANZ",
        [7] = "KR",
        [8] = "TW",
        [9] = "RU",
        [10] = "IN",
        [11] = "NZ_865",
        [12] = "TH",
        [13] = "LORA_24",
        [14] = "UA_433",
        [15] = "MY_433",
        [16] = "MY_919",
        [17] = "SG_923",
        // UA_868 = 18 — not in JurisdictionCatalog; keep as label for display only.
        [18] = "UA_868"
    };

    public static string CodeForProto(int regionProto) =>
        ByProto.TryGetValue(regionProto, out var code) ? code : "UNSET";

    /// <summary>Display / file label for any modem_preset number the board may report.</summary>
    public static string ModemPresetName(int protoValue) => protoValue switch
    {
        0 => "LONG_FAST",
        1 => "LONG_SLOW",
        2 => "VERY_LONG_SLOW",
        3 => "MEDIUM_SLOW",
        4 => "MEDIUM_FAST",
        5 => "SHORT_SLOW",
        6 => "SHORT_FAST",
        7 => "LONG_MODERATE",
        8 => "SHORT_TURBO",
        9 => "LONG_TURBO",
        10 => "VERY_SHORT_SLOW",
        11 => "VERY_SHORT_FAST",
        12 => "VERY_LONG_FAST",
        13 => "LORA_24_FAST",
        14 => "LORA_24_MODERATE",
        15 => "LORA_24_SLOW",
        16 => "MEDIUM_TURBO",
        _ => $"PRESET_{protoValue}"
    };
}
