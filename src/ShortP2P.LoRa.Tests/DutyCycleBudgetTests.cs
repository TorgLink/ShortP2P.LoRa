using ShortP2P.LoRa.Reliability;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class DutyCycleBudgetTests
{
    [Fact]
    public void Even_Pacing_Defers_Before_Hourly_Budget_Is_Exhausted()
    {
        // Goal: verify even airtime pacing delays a second frame while the rolling window still has headroom.
        // Expected: first frame waits 0; after recording 1 s airtime, the next frame waits ~10 s and spent stays well under half the budget.
        // 10% of 100 s window → 10 s airtime budget; 10 ms/byte → 100 bytes = 1 s airtime.
        var budget = new DutyCycleBudget(
            dutyCyclePercent: 10,
            window: TimeSpan.FromSeconds(100),
            airtimePerByteMs: 10.0,
            burstEarnSeconds: 10); // 1 s burst credit

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int oneSecondFrame = 100;

        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, t0));
        budget.RecordTx(oneSecondFrame, t0);

        // Next 1 s frame needs ~10 s wall clock at 0.1 airtime-s / wall-s,
        // while spent (1 s) is still far below the 10 s window budget.
        var wait = budget.WaitTimeFor(oneSecondFrame, t0);
        Assert.True(wait > TimeSpan.FromSeconds(5), $"expected pace wait, got {wait}");
        Assert.True(wait < TimeSpan.FromSeconds(15), $"pace wait too large: {wait}");
        Assert.True(budget.SpentSeconds < 2);
        Assert.True(budget.SpentSeconds < budget.BudgetSeconds / 2);
    }

    [Fact]
    public void After_Pace_Wait_Frame_Is_Allowed_Again()
    {
        // Goal: confirm that once the pace wait elapses, the next frame is allowed immediately.
        // Expected: wait > 0 at t0 after first TX; at t0+wait WaitTimeFor is Zero and a second RecordTx raises spent above 1.5 s.
        var budget = new DutyCycleBudget(
            dutyCyclePercent: 10,
            window: TimeSpan.FromSeconds(100),
            airtimePerByteMs: 10.0,
            burstEarnSeconds: 10);

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int oneSecondFrame = 100;

        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, t0));
        budget.RecordTx(oneSecondFrame, t0);

        var wait = budget.WaitTimeFor(oneSecondFrame, t0);
        Assert.True(wait > TimeSpan.Zero);

        var tReady = t0 + wait;
        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, tReady));
        budget.RecordTx(oneSecondFrame, tReady);
        Assert.True(budget.SpentSeconds > 1.5);
    }

    [Fact]
    public void Rolling_Hour_Hard_Cap_Still_Applies()
    {
        // Goal: ensure burst credit cannot bypass the hard rolling-window airtime cap.
        // Expected: ten 1 s frames at t0 succeed, then WaitTimeFor returns a large hard-cap wait (> 50 s).
        var budget = new DutyCycleBudget(
            dutyCyclePercent: 10,
            window: TimeSpan.FromSeconds(100),
            airtimePerByteMs: 10.0,
            burstEarnSeconds: 10_000); // burst covers the full window budget

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int oneSecondFrame = 100;

        // Dump the full 10 s airtime budget in one instant (allowed by huge burst).
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, t0));
            budget.RecordTx(oneSecondFrame, t0);
        }

        var hardWait = budget.WaitTimeFor(oneSecondFrame, t0);
        Assert.True(hardWait > TimeSpan.FromSeconds(50), $"expected hard-cap wait, got {hardWait}");
    }

    [Fact]
    public void Oversized_Lone_Frame_Is_Never_Parked_Forever()
    {
        // Goal: a single frame larger than the window budget must still be allowed once (no permanent park).
        // Expected: WaitTimeFor(2000 bytes) at t0 returns TimeSpan.Zero even with zero burst credit.
        var budget = new DutyCycleBudget(
            dutyCyclePercent: 10,
            window: TimeSpan.FromSeconds(100),
            airtimePerByteMs: 10.0,
            burstEarnSeconds: 0);

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // 2000 bytes * 10 ms = 20 s > 10 s budget.
        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(2000, t0));
    }

    [Fact]
    public void Prepaid_Train_Burst_Skips_Pace_But_Hard_Cap_Still_Applies()
    {
        // Goal: after evenly earning credit for a train, paceExempt spend may burst; hard cap remains.
        // Expected: enforcePace=false allows spend with empty credit; after filling the window, hard wait.
        var budget = new DutyCycleBudget(
            dutyCyclePercent: 10,
            window: TimeSpan.FromSeconds(100),
            airtimePerByteMs: 10.0,
            burstEarnSeconds: 10); // 1 s burst

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int oneSecondFrame = 100;

        // Even pace still defers when credit is empty.
        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, t0));
        budget.RecordTx(oneSecondFrame, t0);
        Assert.True(budget.WaitTimeFor(oneSecondFrame, t0, enforcePace: true) > TimeSpan.Zero);

        // Prepaid train continuation may burst (caller already waited for full-train credit).
        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, t0, enforcePace: false));

        // Burn remaining hard budget with pace-exempt frames (9 more → 10 s spent).
        for (var i = 0; i < 9; i++)
        {
            Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, t0, enforcePace: false));
            budget.RecordTx(oneSecondFrame, t0);
        }

        var hardWait = budget.WaitTimeFor(oneSecondFrame, t0, enforcePace: false);
        Assert.True(hardWait > TimeSpan.FromSeconds(50), $"expected hard-cap wait, got {hardWait}");
    }

    [Fact]
    public void Full_Train_Wait_Earns_Evenly_Then_Allows_Burst_Spend()
    {
        // Goal: empty the burst tank, wait evenly for a 5 s train, then spend as a short burst.
        // Expected: after draining 5 s credit, WaitTimeFor(5s) ≈ 50 s wall; then five paceExempt frames OK.
        var budget = new DutyCycleBudget(
            dutyCyclePercent: 10,
            window: TimeSpan.FromSeconds(100),
            airtimePerByteMs: 10.0,
            burstEarnSeconds: 50); // 5 s burst capacity

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int oneSecondFrame = 100;
        const int fiveSecondTrain = 500;

        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(fiveSecondTrain, t0, enforcePace: true));
        for (var i = 0; i < 5; i++)
            budget.RecordTx(oneSecondFrame, t0);

        // Tank empty; need 5 s again → ~50 s wall at 0.1 airtime-s / wall-s.
        var wait = budget.WaitTimeFor(fiveSecondTrain, t0, enforcePace: true);
        Assert.True(wait > TimeSpan.FromSeconds(40), $"expected long even-pace wait, got {wait}");
        Assert.True(wait < TimeSpan.FromSeconds(60), $"pace wait too large: {wait}");

        var tReady = t0 + wait;
        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(fiveSecondTrain, tReady, enforcePace: true));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(oneSecondFrame, tReady, enforcePace: false));
            budget.RecordTx(oneSecondFrame, tReady);
        }

        Assert.True(budget.SpentSeconds > 9.5);
        Assert.True(budget.WaitTimeFor(oneSecondFrame, tReady, enforcePace: true) > TimeSpan.Zero);
    }
}
