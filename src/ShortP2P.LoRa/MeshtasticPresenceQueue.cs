namespace ShortP2P.LoRa;

/// <summary>
///     Parks a presence frame until the duty window allows it.
///     The gate uses this instead of completing the send as a successful drop.
///     One pending presence per destination, and its due time is not earlier than
///     already-deferred non-presence work.
/// </summary>
internal static class MeshtasticPresenceQueue
{
    internal readonly record struct Slot(DateTime DueUtc, uint To, byte[] Payload);

    /// <summary>
    ///     When the frame cannot go now: duty wait, and one millisecond after any already-deferred
    ///     non-presence due time so presence does not cut in front of that work.
    /// </summary>
    public static DateTime DueUtc(DateTime utcNow, TimeSpan dutyWait, IReadOnlyList<DateTime> nonPresenceDueUtc)
    {
        var due = utcNow + (dutyWait > TimeSpan.Zero ? dutyWait : TimeSpan.Zero);
        foreach (var other in nonPresenceDueUtc)
        {
            var behind = other.AddMilliseconds(1);
            if (behind > due)
                due = behind;
        }

        return due;
    }

    /// <summary>
    ///     Queue <paramref name="payload"/> for <paramref name="to"/>.
    ///     The result always still contains that destination (the frame is not dropped).
    ///     A second presence for the same destination replaces the first.
    /// </summary>
    public static List<Slot> Park(
        IReadOnlyList<Slot> presenceSlots,
        uint to,
        byte[] payload,
        TimeSpan dutyWait,
        DateTime utcNow,
        IReadOnlyList<DateTime> nonPresenceDueUtc)
    {
        var due = DueUtc(utcNow, dutyWait, nonPresenceDueUtc);
        var list = new List<Slot>(presenceSlots.Count + 1);
        var replaced = false;
        foreach (var slot in presenceSlots)
        {
            if (slot.To == to)
            {
                list.Add(new Slot(due, to, payload));
                replaced = true;
            }
            else
            {
                list.Add(slot);
            }
        }

        if (!replaced)
            list.Add(new Slot(due, to, payload));

        return list;
    }
}
