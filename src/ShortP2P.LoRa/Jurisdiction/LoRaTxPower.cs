namespace ShortP2P.LoRa.Jurisdiction;

/// <summary>
///     TorgLink UI TX-power band and defaults from region caps (REQ-RF-8).
/// </summary>
public static class LoRaTxPower
{
    public const int MinDbm = 5;
    public const int MaxDbm = 22;

    /// <summary>Inclusive choices for the settings dropdown: 5 … 22 dBm.</summary>
    public static IReadOnlyList<int> UiChoices { get; } =
        Enumerable.Range(MinDbm, MaxDbm - MinDbm + 1).ToArray();

    public static int ClampUi(int dbm) => Math.Clamp(dbm, MinDbm, MaxDbm);

    /// <summary>
    ///     Default dropdown value when a jurisdiction is selected:
    ///     region <see cref="JurisdictionProfile.PowerMaxDbm"/> clamped into 5…22; UNSET → 22.
    /// </summary>
    public static int DefaultForJurisdiction(JurisdictionProfile? jurisdiction)
    {
        if (jurisdiction == null || jurisdiction.IsUnset || jurisdiction.PowerMaxDbm <= 0)
            return MaxDbm;
        return ClampUi(jurisdiction.PowerMaxDbm);
    }

    /// <summary>
    ///     Value written to the board: UI selection, never above the region cap when known.
    /// </summary>
    public static int EffectiveForBoard(int selectedDbm, JurisdictionProfile? jurisdiction)
    {
        var value = ClampUi(selectedDbm);
        if (jurisdiction is { IsUnset: false, PowerMaxDbm: > 0 })
            value = Math.Min(value, jurisdiction.PowerMaxDbm);
        return Math.Clamp(value, MinDbm, MaxDbm);
    }
}
