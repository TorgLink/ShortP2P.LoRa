namespace ShortP2P.LoRa.Link;

/// <summary>
///     Whether a foreign Meshtastic node is live, from the board's <c>last_heard</c> rather than
///     from the fact that the node database repeated the row.
/// </summary>
public static class MeshtasticNodeLiveness
{
    /// <summary>
    ///     A foreign node is live only while <c>last_heard</c> falls inside this window (a few minutes).
    ///     Long enough to answer a node that just transmitted; short enough that a stored row replayed
    ///     every couple of seconds with an old timestamp is not a live hear. Zero is never live.
    /// </summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(4);

    /// <summary>Per-node memory for <see cref="Observe"/>.</summary>
    public readonly record struct NodePresenceState(uint LastHeardUnix, long LastOfferTicks, bool Bound)
    {
        public NodePresenceState WithBound() => this with { Bound = true };
    }

    /// <summary>Result of one node-database observation.</summary>
    public readonly record struct NodePresenceStep(NodePresenceState State, bool SendPresence);

    /// <summary>
    ///     True when <paramref name="lastHeardUnix"/> is non-zero and not older than
    ///     <see cref="LiveWindow"/>. A timestamp slightly in the future (board clock ahead) still counts.
    /// </summary>
    public static bool IsLive(uint lastHeardUnix, DateTime utcNow)
    {
        if (lastHeardUnix == 0)
            return false;

        var heardUtc = DateTimeOffset.FromUnixTimeSeconds(lastHeardUnix).UtcDateTime;
        var age = utcNow - heardUtc;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return age <= LiveWindow;
    }

    /// <summary>
    ///     True only when this <paramref name="lastHeardUnix"/> is live and strictly newer than the
    ///     timestamp already counted. Replaying the same stored value — including every 2 seconds —
    ///     is not a new hear. A missing or zero timestamp is not a new hear.
    /// </summary>
    public static bool IsNewHear(uint previousLastHeardUnix, uint lastHeardUnix, DateTime utcNow) =>
        IsLive(lastHeardUnix, utcNow) && lastHeardUnix > previousLastHeardUnix;

    /// <summary>
    ///     Advance state from one node-database sample.
    ///     Sends presence when the node is live and not yet bound, but not more often than
    ///     <paramref name="minGapTicks"/>. A replay of an old or unchanged <c>last_heard</c> inside
    ///     the gap does not send. While the node stays live and unbound, another offer is made
    ///     after the gap until <see cref="NodePresenceState.Bound"/> is set.
    /// </summary>
    public static NodePresenceStep Observe(
        NodePresenceState state,
        uint lastHeardUnix,
        DateTime utcNow,
        long nowTicks,
        long minGapTicks)
    {
        if (!IsLive(lastHeardUnix, utcNow))
            return new NodePresenceStep(state, false);

        var heard = lastHeardUnix > state.LastHeardUnix ? lastHeardUnix : state.LastHeardUnix;
        var next = state with { LastHeardUnix = heard };
        if (next.Bound)
            return new NodePresenceStep(next, false);

        var offered = state.LastOfferTicks != 0;
        var gapOk = !offered || unchecked(nowTicks - state.LastOfferTicks) >= minGapTicks;
        if (!gapOk)
            return new NodePresenceStep(next, false);

        return new NodePresenceStep(next with { LastOfferTicks = nowTicks }, true);
    }
}
