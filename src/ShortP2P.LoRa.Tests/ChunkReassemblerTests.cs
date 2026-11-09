using ShortP2P.LoRa.Chunking;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class ChunkReassemblerTests
{
    private static MeshId From => MeshId.FromNodeNumber(0x11223344);

    private static MeshChunk MakeChunk(string id, ushort number, ushort total, uint crc, params byte[] data)
    {
        return new MeshChunk(id, number, total, Crc32.WireType, crc, data);
    }

    [Fact]
    public void OutOfOrder_Chunks_Assemble()
    {
        var datagram = new byte[] { 10, 20, 30, 40, 50 };
        var crc = Crc32.Compute(datagram);
        var r = new ChunkReassembler();
        var now = DateTime.UtcNow;

        Assert.Equal(ChunkAddResult.NeedMore,
            r.Add(From, MakeChunk("aBc123", 3, 3, crc, 50), now, out _));
        Assert.Equal(ChunkAddResult.NeedMore,
            r.Add(From, MakeChunk("aBc123", 1, 3, crc, 10, 20), now, out _));
        var done = r.Add(From, MakeChunk("aBc123", 2, 3, crc, 30, 40), now, out var assembled);

        Assert.Equal(ChunkAddResult.Completed, done);
        Assert.Equal(datagram, assembled);
    }

    [Fact]
    public void FullSet_With_Bad_Crc_Is_Reported()
    {
        var r = new ChunkReassembler();
        var now = DateTime.UtcNow;
        var crc = Crc32.Compute([1, 2, 3, 4]);

        r.Add(From, MakeChunk("zzZZ99", 1, 2, crc, 1, 2), now, out _);
        // Corrupted content: bytes differ from what the CRC was computed over.
        var result = r.Add(From, MakeChunk("zzZZ99", 2, 2, crc, 3, 0x7F), now, out _);

        Assert.Equal(ChunkAddResult.CrcMismatch, result);
    }

    [Fact]
    public void Inconsistent_Crc_Between_Chunks_Discards_Set()
    {
        var datagram = new byte[] { 1, 2 };
        var crc = Crc32.Compute(datagram);
        var r = new ChunkReassembler();
        var now = DateTime.UtcNow;

        r.Add(From, MakeChunk("qWe111", 1, 2, crc, 1), now, out _);
        // Second chunk of the same datagram carries a different CRC: corrupt set.
        var result = r.Add(From, MakeChunk("qWe111", 2, 2, crc ^ 0xFF, 2), now, out _);

        Assert.Equal(ChunkAddResult.Inconsistent, result);
    }

    [Fact]
    public void Stalled_Assembly_Is_Reported_And_Can_Be_Dropped()
    {
        var r = new ChunkReassembler();
        var t0 = DateTime.UtcNow;
        r.Add(From, MakeChunk("sT0001", 1, 3, 0x1234, 9), t0, out _);

        Assert.Empty(r.GetStalled(t0 + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        var stalled = r.GetStalled(t0 + TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(10));
        Assert.Single(stalled);
        Assert.Equal("sT0001", stalled[0].Id);

        Assert.Equal(1, r.NoteRetry(From, "sT0001", t0 + TimeSpan.FromSeconds(11)));
        // NoteRetry must not clear the stall gap — assembly stays stalled until a real chunk arrives.
        Assert.Single(r.GetStalled(t0 + TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10)));
        Assert.Equal(1, r.GetStalled(t0 + TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10))[0].RetryCount);

        r.Drop(From, "sT0001");
        Assert.Empty(r.GetStalled(t0 + TimeSpan.FromHours(1), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Stalled_Uses_Size_Based_Timeout_Selector()
    {
        var r = new ChunkReassembler();
        var t0 = DateTime.UtcNow;
        // One 500-byte chunk of 3 → estimate 500 + 2*500 = 1500 (≥ large band)
        r.Add(From, MakeChunk("Lg0001", 1, 3, 0xABCD, new byte[500]), t0, out _);

        TimeSpan Selector(int estimated) =>
            estimated >= 1000 ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(141);

        Assert.Empty(r.GetStalled(t0 + TimeSpan.FromSeconds(150), Selector));
        var stalled = r.GetStalled(t0 + TimeSpan.FromSeconds(181), Selector);
        Assert.Single(stalled);
        Assert.Equal("Lg0001", stalled[0].Id);
    }

    [Fact]
    public void Stalled_Partial_Assembly_Uses_Progress_Timeout()
    {
        var r = new ChunkReassembler();
        var t0 = DateTime.UtcNow;
        // Last chunk of 2 arrived; first is missing — accelerate resend vs full RxTimeout.
        r.Add(From, MakeChunk("BEHDiT", 2, 2, 0x1111, new byte[60]), t0, out _);

        TimeSpan Progress(int estimated, int missing, int received) =>
            missing > 0 && received > 0
                ? TimeSpan.FromSeconds(45)
                : TimeSpan.FromSeconds(141);

        Assert.Empty(r.GetStalled(t0 + TimeSpan.FromSeconds(40), Progress));
        var stalled = r.GetStalled(t0 + TimeSpan.FromSeconds(46), Progress);
        Assert.Single(stalled);
        Assert.Equal("BEHDiT", stalled[0].Id);
    }

    [Fact]
    public void MidTrain_Holes_Report_Progress_And_Fresh_Incomplete()
    {
        var r = new ChunkReassembler();
        var t0 = DateTime.UtcNow;
        // Vt3Mi0-style: 1, then 5..7 — holes at 2..4, last present.
        r.Add(From, MakeChunk("Vt3Mi0", 1, 7, 0xABCD, new byte[60]), t0, out _);
        Assert.True(r.HasFreshIncomplete(t0 + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(22)));
        Assert.False(r.HasFreshIncomplete(t0 + TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(22)));

        r.Add(From, MakeChunk("Vt3Mi0", 5, 7, 0xABCD, new byte[60]), t0.AddSeconds(18), out _);
        r.Add(From, MakeChunk("Vt3Mi0", 6, 7, 0xABCD, new byte[60]), t0.AddSeconds(28), out _);
        r.Add(From, MakeChunk("Vt3Mi0", 7, 7, 0xABCD, new byte[40]), t0.AddSeconds(34), out _);

        Assert.True(r.TryGetProgress(From, "Vt3Mi0", out var progress));
        Assert.Equal(7, progress.Total);
        Assert.Equal(4, progress.Received);
        Assert.Equal(3, progress.Missing);
        Assert.True(progress.HasGaps);
        Assert.True(progress.HasLastChunk);

        TimeSpan GapProgress(int estimated, int missing, int received, bool hasGaps) =>
            hasGaps ? TimeSpan.FromSeconds(18) : TimeSpan.FromSeconds(45);

        Assert.Empty(r.GetStalled(t0.AddSeconds(34 + 10), GapProgress));
        var stalled = r.GetStalled(t0.AddSeconds(34 + 19), GapProgress);
        Assert.Single(stalled);
        Assert.Equal("Vt3Mi0", stalled[0].Id);
    }
}
