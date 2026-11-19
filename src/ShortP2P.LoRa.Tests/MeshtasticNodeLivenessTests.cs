using ShortP2P.LoRa.Link;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class MeshtasticNodeLivenessTests
{
    private static readonly DateTime Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime;

    private static uint Unix(DateTime utc) =>
        (uint)new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    [Fact]
    public void Zero_Or_Old_LastHeard_Is_Not_Live()
    {
        // Goal: a missing last_heard, or one older than the live window, is not a live node.
        // Expected: zero and a timestamp a minute past the window are not live; the window edge is.
        Assert.False(MeshtasticNodeLiveness.IsLive(0, Now));

        var edge = Unix(Now - MeshtasticNodeLiveness.LiveWindow);
        Assert.True(MeshtasticNodeLiveness.IsLive(edge, Now));
        Assert.False(MeshtasticNodeLiveness.IsLive(edge - 1, Now));
    }

    [Fact]
    public void Replaying_The_Same_Old_LastHeard_Is_Not_A_New_Hear()
    {
        // Goal: the board repeating a stored node every 2 seconds must not count as a new hear.
        // Expected: an hour-old last_heard stays not-live across five replays; IsNewHear stays false.
        var stale = Unix(Now - TimeSpan.FromHours(1));
        var state = default(MeshtasticNodeLiveness.NodePresenceState);
        for (var i = 0; i < 5; i++)
        {
            var at = Now.AddSeconds(2 * i);
            Assert.False(MeshtasticNodeLiveness.IsNewHear(state.LastHeardUnix, stale, at));
            var step = MeshtasticNodeLiveness.Observe(state, stale, at, nowTicks: i * 2_000, minGapTicks: 60_000);
            Assert.False(step.SendPresence);
            state = step.State;
        }

        Assert.Equal(0u, state.LastHeardUnix);
    }

    [Fact]
    public void Recent_LastHeard_Sends_Once_Then_Repeats_After_Gap_Until_Bound()
    {
        // Goal: a live last_heard offers presence, ignores a same-timestamp replay inside the gap,
        // and repeats after the gap until the node is bound.
        // Expected: send, no send at +2s, send at +60s, no send once bound.
        var heard = Unix(Now - TimeSpan.FromSeconds(30));
        var state = default(MeshtasticNodeLiveness.NodePresenceState);

        var first = MeshtasticNodeLiveness.Observe(state, heard, Now, nowTicks: 1_000, minGapTicks: 60_000);
        Assert.True(first.SendPresence);
        Assert.True(MeshtasticNodeLiveness.IsNewHear(0, heard, Now));
        Assert.False(MeshtasticNodeLiveness.IsNewHear(heard, heard, Now));

        var replay = MeshtasticNodeLiveness.Observe(
            first.State, heard, Now.AddSeconds(2), nowTicks: 3_000, minGapTicks: 60_000);
        Assert.False(replay.SendPresence);

        var again = MeshtasticNodeLiveness.Observe(
            replay.State, heard, Now.AddSeconds(60), nowTicks: 61_000, minGapTicks: 60_000);
        Assert.True(again.SendPresence);

        var bound = again.State.WithBound();
        var afterBind = MeshtasticNodeLiveness.Observe(
            bound, heard, Now.AddMinutes(2), nowTicks: 180_000, minGapTicks: 60_000);
        Assert.False(afterBind.SendPresence);
        Assert.True(afterBind.State.Bound);
    }

    [Fact]
    public void LastHeard_Moving_Forward_Inside_The_Gap_Is_Remembered_Without_An_Extra_Send()
    {
        // Goal: a newer last_heard is a new hear, but the offer gap still holds.
        // Expected: the stored timestamp advances and SendPresence is false until the gap elapses.
        var firstHeard = Unix(Now - TimeSpan.FromSeconds(20));
        var opened = MeshtasticNodeLiveness.Observe(
            default, firstHeard, Now, nowTicks: 5_000, minGapTicks: 60_000);
        Assert.True(opened.SendPresence);

        var newer = Unix(Now.AddSeconds(10));
        var stepped = MeshtasticNodeLiveness.Observe(
            opened.State, newer, Now.AddSeconds(10), nowTicks: 15_000, minGapTicks: 60_000);
        Assert.False(stepped.SendPresence);
        Assert.Equal(newer, stepped.State.LastHeardUnix);
        Assert.True(MeshtasticNodeLiveness.IsNewHear(firstHeard, newer, Now.AddSeconds(10)));
    }
}
