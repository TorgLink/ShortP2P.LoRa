using System.Globalization;

namespace ShortP2P.LoRa;

/// <summary>
///     Qualitative maximum range of a universal modem preset.
///     Meshtastic docs order general-purpose presets from shortest range to longest range
///     and do not publish a kilometer radius (it depends on terrain, antenna and power).
/// </summary>
public enum LoRaPresetRange
{
    Short,
    Medium,
    Long
}

/// <summary>Speed half of a universal modem preset. Not every speed exists for every range.</summary>
public enum LoRaPresetSpeed
{
    Fast,
    Slow,
    Moderate
}

/// <summary>
///     One modem preset that exists in every current Meshtastic firmware and every region.
///     <see cref="ProtoValue"/> matches <c>Config.LoRaConfig.ModemPreset</c> in meshtastic/protobufs
///     <c>config.proto</c>. This repo has no generated protobuf enum; <c>MeshtasticProto</c> already
///     uses that file's field numbers (<c>use_preset</c> = 1, <c>modem_preset</c> = 2, <c>tx_power</c> = 10).
/// </summary>
/// <param name="Name">Protobuf identifier, for example LONG_FAST.</param>
/// <param name="ProtoValue">ModemPreset enum number.</param>
/// <param name="KilobitsPerSecond">
///     Approximate air rate in kbit/s: ShortFast 10.9, ShortSlow 6.25, MediumFast 3.5,
///     MediumSlow 2.0, LongFast 1.1, LongModerate 0.34.
/// </param>
public sealed record LoRaModemPresetInfo(
    string Name,
    int ProtoValue,
    LoRaPresetRange Range,
    LoRaPresetSpeed Speed,
    decimal KilobitsPerSecond)
{
    public string KilobitsText =>
        KilobitsPerSecond.ToString("0.0#", CultureInfo.InvariantCulture);
}

/// <summary>
///     Intersection of Meshtastic modem presets: legal in every region and still present
///     in current firmware. Excluded: every <c>*_TURBO</c> (500 kHz, rejected in EU_868 / EU_866 /
///     EU_N_868), <c>LONG_SLOW</c> (removed in 2.7), <c>VERY_LONG_SLOW</c> (removed in 2.5),
///     and the region-only families <c>LITE_*</c>, <c>NARROW_*</c>, <c>TINY_*</c>.
///     Factory default is <c>LONG_FAST</c>. Both peers must use the same preset.
/// </summary>
public static class LoRaModemPresets
{
    public const string LongFastName = "LONG_FAST";

    /// <summary>Universal presets only, fast before slow within each range family.</summary>
    public static IReadOnlyList<LoRaModemPresetInfo> All { get; } =
    [
        new("SHORT_FAST", 6, LoRaPresetRange.Short, LoRaPresetSpeed.Fast, 10.9m),
        new("SHORT_SLOW", 5, LoRaPresetRange.Short, LoRaPresetSpeed.Slow, 6.25m),
        new("MEDIUM_FAST", 4, LoRaPresetRange.Medium, LoRaPresetSpeed.Fast, 3.5m),
        new("MEDIUM_SLOW", 3, LoRaPresetRange.Medium, LoRaPresetSpeed.Slow, 2.0m),
        new("LONG_FAST", 0, LoRaPresetRange.Long, LoRaPresetSpeed.Fast, 1.1m),
        new("LONG_MODERATE", 7, LoRaPresetRange.Long, LoRaPresetSpeed.Moderate, 0.34m)
    ];

    public static LoRaModemPresetInfo Default =>
        All.First(p => p.Name == LongFastName);

    public static bool IsAllowedProtoValue(int protoValue) =>
        All.Any(p => p.ProtoValue == protoValue);

    public static string? NameForProtoValue(int protoValue) =>
        All.FirstOrDefault(p => p.ProtoValue == protoValue)?.Name;

    /// <summary>Unknown, blank, turbo, or removed names become <see cref="Default"/>.</summary>
    public static LoRaModemPresetInfo GetOrDefault(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var found = All.FirstOrDefault(p =>
                string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found != null)
                return found;
        }

        return Default;
    }

    public static string NormalizeName(string? name) => GetOrDefault(name).Name;

    public static IReadOnlyList<LoRaPresetSpeed> SpeedsFor(LoRaPresetRange range) =>
        All.Where(p => p.Range == range).Select(p => p.Speed).ToArray();

    /// <summary>Illegal range/speed pairs snap to Fast (every family has a Fast preset).</summary>
    public static LoRaPresetSpeed SnapSpeed(LoRaPresetRange range, LoRaPresetSpeed speed)
    {
        var legal = SpeedsFor(range);
        return legal.Contains(speed) ? speed : LoRaPresetSpeed.Fast;
    }

    public static LoRaModemPresetInfo Find(LoRaPresetRange range, LoRaPresetSpeed speed)
    {
        var snapped = SnapSpeed(range, speed);
        return All.First(p => p.Range == range && p.Speed == snapped);
    }
}
