using ShortP2P.LoRa.Chunking;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class MeshtasticTxPriorityTests
{
    private static byte[] Frame(byte first, int totalLength)
    {
        var f = new byte[Math.Max(1, totalLength)];
        f[0] = first;
        return f;
    }

    [Theory]
    [InlineData(0x30, 80, MeshtasticTxPriority.Control)]
    [InlineData(0x01, 129, MeshtasticTxPriority.Control)]
    [InlineData(0x04, 13, MeshtasticTxPriority.Control)]
    [InlineData(0x31, 40, MeshtasticTxPriority.Control)]
    [InlineData(0x32, 100, MeshtasticTxPriority.Control)]
    [InlineData(0x33, 30, MeshtasticTxPriority.Control)]
    public void Control_Frames_Are_Priority_One(byte first, int len, MeshtasticTxPriority expected)
    {
        Assert.Equal(expected, MeshtasticTxClassifier.Classify(Frame(first, len)));
        Assert.Equal(expected,
            MeshtasticTxClassifier.Classify(Frame(first, len), MeshtasticTxContentKind.Binary));
    }

    [Theory]
    [InlineData(199, MeshtasticTxPriority.ShortText)]
    [InlineData(200, MeshtasticTxPriority.ShortText)]
    [InlineData(201, MeshtasticTxPriority.MediumText)]
    [InlineData(999, MeshtasticTxPriority.MediumText)]
    [InlineData(1000, MeshtasticTxPriority.LargeText)]
    [InlineData(5000, MeshtasticTxPriority.LargeText)]
    public void Cipher_Defaults_To_Text_Size_Bands(int len, MeshtasticTxPriority expected)
    {
        Assert.Equal(expected, MeshtasticTxClassifier.Classify(Frame(0x02, len)));
        Assert.Equal(expected, MeshtasticTxClassifier.ClassifySized(MeshtasticTxContentKind.Text, len));
    }

    [Theory]
    [InlineData(1, MeshtasticTxPriority.SmallBinary)]
    [InlineData(5120, MeshtasticTxPriority.SmallBinary)]
    [InlineData(5121, MeshtasticTxPriority.LargeBinary)]
    [InlineData(20 * 1024, MeshtasticTxPriority.LargeBinary)]
    public void Binary_Size_Bands(int len, MeshtasticTxPriority expected)
    {
        Assert.Equal(expected,
            MeshtasticTxClassifier.Classify(Frame(0x02, len), MeshtasticTxContentKind.Binary));
        Assert.Equal(expected, MeshtasticTxClassifier.ClassifySized(MeshtasticTxContentKind.Binary, len));
    }

    [Fact]
    public void Chunk_Frames_Classify_As_Control()
    {
        var chunk = new byte[ChunkCodec.HeaderLength + 8];
        ChunkCodec.WriteChunk(chunk, "Ab12Cd", 1, 2, 0xAABBCCDD, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(MeshtasticTxPriority.Control, MeshtasticTxClassifier.Classify(chunk));
    }

    [Fact]
    public void ChatWire_Kind_Maps_Text_Vs_Binary()
    {
        Assert.Equal(MeshtasticTxContentKind.Text, MeshtasticTxClassifier.ContentKindFromChatWireKind(0x01));
        Assert.Equal(MeshtasticTxContentKind.Text, MeshtasticTxClassifier.ContentKindFromChatWireKind(0x09));
        Assert.Equal(MeshtasticTxContentKind.Binary, MeshtasticTxClassifier.ContentKindFromChatWireKind(0x02));
        Assert.Equal(MeshtasticTxContentKind.Binary, MeshtasticTxClassifier.ContentKindFromChatWireKind(0x03));
        Assert.Equal(MeshtasticTxContentKind.Binary, MeshtasticTxClassifier.ContentKindFromChatWireKind(0x04));
    }

    [Fact]
    public void Priority_Queues_Drain_Higher_Before_Lower_Fifo_Within()
    {
        var queues = new MeshtasticTxPriorityQueues();

        void Enqueue(MeshtasticTxPriority priority)
        {
            queues.Enqueue(new MeshtasticTxWorkItem(
                1, [(byte)priority], priority, new TaskCompletionSource<bool>(), CancellationToken.None));
        }

        Enqueue(MeshtasticTxPriority.LargeBinary);
        Enqueue(MeshtasticTxPriority.ShortText);
        Enqueue(MeshtasticTxPriority.ShortText);
        Enqueue(MeshtasticTxPriority.Control);
        Enqueue(MeshtasticTxPriority.MediumText);
        Enqueue(MeshtasticTxPriority.SmallBinary);

        var drained = new List<MeshtasticTxPriority>();
        while (queues.TryDequeue(out var item))
            drained.Add(item.Priority);

        Assert.Equal(
        [
            MeshtasticTxPriority.Control,
            MeshtasticTxPriority.ShortText,
            MeshtasticTxPriority.ShortText,
            MeshtasticTxPriority.MediumText,
            MeshtasticTxPriority.SmallBinary,
            MeshtasticTxPriority.LargeBinary
        ], drained);
    }

}
