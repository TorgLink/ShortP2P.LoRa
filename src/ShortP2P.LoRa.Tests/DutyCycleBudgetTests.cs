using ShortP2P.LoRa.Reliability;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class DutyCycleBudgetTests
{
    [Fact]
    public void Even_Pacing_Defers_Before_Hourly_Budget_Is_Exhausted()
    {
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
        var budget = new DutyCycleBudget(
            dutyCyclePercent: 10,
            window: TimeSpan.FromSeconds(100),
            airtimePerByteMs: 10.0,
            burstEarnSeconds: 0);

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // 2000 bytes * 10 ms = 20 s > 10 s budget.
        Assert.Equal(TimeSpan.Zero, budget.WaitTimeFor(2000, t0));
    }
}
