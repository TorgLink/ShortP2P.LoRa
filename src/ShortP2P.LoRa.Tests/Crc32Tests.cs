using System.Text;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class Crc32Tests
{
    [Fact]
    public void KnownVector_123456789()
    {
        // Goal: public CRC-32 matches the standard ZIP check vector.
        // Expected: CRC32("123456789") == 0xCBF43926.
        var crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void Empty_IsZero()
    {
        // Goal: empty input has a defined CRC of zero.
        // Expected: Compute(Empty) == 0.
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void TablePath_MatchesPublicApi_OnOddSizes()
    {
        // Goal: table-driven path stays bit-identical to the public Compute API for varied lengths.
        // Expected: for each size, Compute(data) equals ~ComputeTable(data, 0xFFFFFFFF).
        var rng = new Random(42);
        foreach (var size in new[] { 1, 2, 3, 5, 7, 9, 15, 16, 17, 100, 1000 })
        {
            var data = new byte[size];
            rng.NextBytes(data);
            var viaApi = Crc32.Compute(data);
            var viaTable = ~Crc32.ComputeTable(data, 0xFFFFFFFF);
            Assert.Equal(viaApi, viaTable);
        }
    }
}
