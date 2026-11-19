using Xunit;

namespace ShortP2P.LoRa.Tests;

public class MeshtasticPresenceDeferralTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Duty_Wait_Parks_Presence_Instead_Of_Dropping_It()
    {
        // Goal: a presence frame that cannot go now stays queued until the duty window allows it.
        // Expected: the queue still holds the 0x31 payload, due after the duty wait. It is not discarded.
        var payload = new byte[] { 0x31, 0x01 };
        var parked = MeshtasticPresenceQueue.Park(
            [], to: 0xA1, payload, dutyWait: TimeSpan.FromSeconds(30), Now, nonPresenceDueUtc: []);

        Assert.Single(parked);
        Assert.Equal(0xA1u, parked[0].To);
        Assert.Equal(payload, parked[0].Payload);
        Assert.Equal(Now.AddSeconds(30), parked[0].DueUtc);
    }

    [Fact]
    public void Presence_Is_Parked_Behind_Already_Deferred_Non_Presence()
    {
        // Goal: presence must not cut in front of non-presence work that is already deferred.
        // Expected: due time is strictly after the deferred chat frame, even when duty wait is zero.
        var chatDue = Now.AddSeconds(10);
        var parked = MeshtasticPresenceQueue.Park(
            [],
            to: 0xB2,
            payload: [0x31, 0x02],
            dutyWait: TimeSpan.Zero,
            Now,
            nonPresenceDueUtc: [chatDue]);

        Assert.Single(parked);
        Assert.True(parked[0].DueUtc > chatDue);
    }

    [Fact]
    public void Second_Presence_To_The_Same_Destination_Replaces_The_First()
    {
        // Goal: only one pending presence per destination.
        // Expected: two offers to 0xA1 collapse to the newer payload; a different destination stays.
        var first = MeshtasticPresenceQueue.Park(
            [], 0xA1, [0x31, 0x01], TimeSpan.FromSeconds(5), Now, []);
        var other = MeshtasticPresenceQueue.Park(
            first, 0xC3, [0x31, 0x03], TimeSpan.FromSeconds(5), Now, []);
        var coalesced = MeshtasticPresenceQueue.Park(
            other, 0xA1, [0x31, 0x09], TimeSpan.FromSeconds(5), Now, []);

        Assert.Equal(2, coalesced.Count);
        var toA1 = Assert.Single(coalesced, slot => slot.To == 0xA1);
        Assert.Equal(new byte[] { 0x31, 0x09 }, toA1.Payload);
        Assert.Contains(coalesced, slot => slot.To == 0xC3);
    }
}
