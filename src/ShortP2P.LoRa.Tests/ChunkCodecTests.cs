using ShortP2P.LoRa.Chunking;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class ChunkCodecTests
{
    [Fact]
    public void NewId_Format()
    {
        // Goal: generated chunk ids are 6 ASCII letter/digit chars starting with a letter.
        // Expected: every NewId() length=6, first byte IsLetter, all chars alphanumeric.
        for (var i = 0; i < 100; i++)
        {
            var id = ChunkCodec.NewId();
            Assert.Equal(6, id.Length);
            Assert.True(ChunkCodec.IsLetter((byte)id[0]));
            foreach (var c in id)
                Assert.True(char.IsAsciiLetterOrDigit(c));
        }
    }

    [Fact]
    public void RoundTrip()
    {
        // Goal: WriteChunk/TryParse preserve id, indexes, CRC type/value, and payload.
        // Expected: parse recovers Ab3xY9, 2/3, WireType, 0xDEADBEEF, and data bytes.
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var buf = new byte[ChunkCodec.HeaderLength + data.Length];
        var written = ChunkCodec.WriteChunk(buf, "Ab3xY9", number: 2, total: 3, crc: 0xDEADBEEF, data);

        Assert.Equal(buf.Length, written);
        Assert.True(ChunkCodec.TryParse(buf, out var chunk));
        Assert.Equal("Ab3xY9", chunk.Id);
        Assert.Equal((ushort)2, chunk.Number);
        Assert.Equal((ushort)3, chunk.Total);
        Assert.Equal(Crc32.WireType, chunk.CrcType);
        Assert.Equal(0xDEADBEEFu, chunk.Crc);
        Assert.Equal(data, chunk.Data);
    }

    [Fact]
    public void Rejects_NumberZero_And_TotalBelowTwo()
    {
        // Goal: invalid chunk indexes are rejected at write time.
        // Expected: ArgumentOutOfRangeException for number=0 and for total=1.
        var buf = new byte[ChunkCodec.HeaderLength + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkCodec.WriteChunk(buf, "Ab3xY9", 0, 3, 0, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkCodec.WriteChunk(buf, "Ab3xY9", 1, 1, 0, [1]));
    }

    [Fact]
    public void TryParse_Rejects_Digit_FirstByte_And_ShortFrames()
    {
        // Goal: non-chunk frames and truncated buffers are not parsed as chunks.
        // Expected: TryParse false for invite-like first byte, header-only, and 1-byte frames.
        Assert.False(ChunkCodec.TryParse([0x30, 1, 2], out _)); // invite-like
        Assert.False(ChunkCodec.TryParse(new byte[ChunkCodec.HeaderLength], out _)); // no data
        Assert.False(ChunkCodec.TryParse([0x01], out _));
    }

    [Fact]
    public void Chunk_Never_Collides_With_ShortP2P_Frames()
    {
        // Goal: ShortP2P control/cipher first bytes are never treated as chunk letters.
        // Expected: IsLetter is false for 0x30/0x01/0x04/0x02/0x32/0x33.
        foreach (var b in new byte[] { 0x30, 0x01, 0x04, 0x02, 0x32, 0x33 })
            Assert.False(ChunkCodec.IsLetter(b));
    }
}
