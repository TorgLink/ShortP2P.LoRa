using System.Text;
using ShortP2P.LoRa.Chunking;
using ShortP2P.LoRa.Jurisdiction;
using ShortP2P.LoRa.Reliability;
using ShortP2P.Transport.Abstractions;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class MeshatasticTransportTests
{
    private const uint AliceNode = 0xA1CE0001;
    private const uint BobNode = 0xB0B00002;
    private const uint EveNode = 0xE5E50003;

    private static readonly byte[] NetworkId = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();

    // Invite 0x30: 0x30 | "SP2I" | version | NetworkId 12 (offset 6) | ...
    private static byte[] Invite(byte[] networkId)
    {
        var f = new byte[18];
        f[0] = 0x30;
        Array.Copy(Encoding.ASCII.GetBytes("SP2I"), 0, f, 1, 4);
        f[5] = 1;
        Array.Copy(networkId, 0, f, 6, 12);
        return f;
    }

    private static byte[] Cipher(int bytes = 40)
    {
        var f = new byte[2 + bytes]; // 0x02 + session/ciphertext bytes
        f[0] = 0x02;
        return f;
    }

    private static TransportAddress Addr(uint node)
    {
        return MeshatasticTransportAddress.ToTransportAddress(MeshId.FromNodeNumber(node));
    }

    private static MeshatasticTransportOptions MakeOptions(int retries = 0, byte[]? networkId = null,
        JurisdictionProfile? jurisdiction = null)
    {
        return new MeshatasticTransportOptions
        {
            ComPort = "FAKE",
            Jurisdiction = jurisdiction ?? JurisdictionCatalog.Require("EU_868"),
            LocalNetworkId = networkId,
            Retry = new RetryConfig
            {
                SendRetryIntervalBase = TimeSpan.FromMilliseconds(10),
                RxRetryIntervalBase = TimeSpan.FromMilliseconds(10),
                SendMaxRetries = retries,
                RxMaxRetries = retries,
                RxTimeout = TimeSpan.FromMilliseconds(300)
            },
            MaxRadioPayloadBytes = 233
        };
    }

    private static async Task<(MeshatasticTransport A, MeshatasticTransport B, FakeMeshLink LinkA, FakeMeshLink LinkB)>
        StartPairAsync(int aliceRetries = 0, int bobRetries = 0, bool withNetworkIds = false)
    {
        var (linkA, linkB) = FakeMeshLink.CreatePair(AliceNode, BobNode);
        var net = withNetworkIds ? NetworkId : null;
        var a = new MeshatasticTransport(MakeOptions(aliceRetries, net), linkA);
        var b = new MeshatasticTransport(MakeOptions(bobRetries, net), linkB);
        await a.StartAsync();
        await b.StartAsync();
        return (a, b, linkA, linkB);
    }

    private static async Task<byte[]> NextInboundAsync(MeshatasticTransport t, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var msg = await t.Inbound.ReadAsync(cts.Token);
        return msg.Payload.ToArray();
    }

    [Fact]
    public async Task Kind_Is_Meshtastic()
    {
        var (a, _, _, _) = await StartPairAsync();
        Assert.Equal((byte)5, a.Kind);
        Assert.Equal(TransportKind.Meshtastic, a.Kind);
        await a.DisposeAsync();
    }

    [Fact]
    public async Task SingleFrame_Datagram_Delivers_Unchanged()
    {
        var (a, b, linkA, _) = await StartPairAsync();
        var payload = Cipher();

        await a.SendAsync(payload, Addr(BobNode));

        Assert.Equal(payload, await NextInboundAsync(b, TimeSpan.FromSeconds(5)));
        // Single-frame datagram: sent as-is, one radio frame, no chunk header.
        Assert.Single(linkA.Sent);
        Assert.Equal(payload, linkA.Sent[0].Payload);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Large_Datagram_Is_Chunked_And_Reassembled()
    {
        var (a, b, linkA, _) = await StartPairAsync();

        var payload = new byte[2000];
        new Random(7).NextBytes(payload);
        payload[0] = 0x02; // cipher frame

        await a.SendAsync(payload, Addr(BobNode));

        Assert.Equal(payload, await NextInboundAsync(b, TimeSpan.FromSeconds(10)));

        var expectedChunks = (payload.Length + (233 - ChunkCodec.HeaderLength) - 1) / (233 - ChunkCodec.HeaderLength);
        Assert.Equal(expectedChunks, linkA.Sent.Count);
        Assert.All(linkA.Sent, s => Assert.True(s.Payload.Length <= 233));
        Assert.All(linkA.Sent, s => Assert.True(ChunkCodec.TryParse(s.Payload, out _)));
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task SendRetry_On_Radio_Failure()
    {
        var (a, b, linkA, _) = await StartPairAsync(aliceRetries: 3);

        linkA.FailNextSends = 2; // first two attempts throw
        await a.SendAsync(Cipher(), Addr(BobNode));

        Assert.Equal(3, linkA.Attempts); // two failures + one success
        Assert.Single(linkA.Sent); // only the successful attempt hit the air
        Assert.NotNull(await NextInboundAsync(b, TimeSpan.FromSeconds(5)));
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task SendRetry_Exhausted_Throws()
    {
        var (a, _, linkA, _) = await StartPairAsync(aliceRetries: 1);
        linkA.FailNextSends = 5;

        await Assert.ThrowsAsync<IOException>(() => a.SendAsync(Cipher(), Addr(BobNode)).AsTask());
        await a.DisposeAsync();
    }

    [Fact]
    public async Task Unset_Jurisdiction_Allows_Transmit_At_Default_Duty_Cycle()
    {
        var (linkA, _) = FakeMeshLink.CreatePair(AliceNode, BobNode);
        var options = MakeOptions(jurisdiction: JurisdictionCatalog.Unset);
        var a = new MeshatasticTransport(options, linkA);
        await a.StartAsync();

        // REQ-RF-2: UNSET no longer blocks TX — the 1% default duty cycle applies instead.
        await a.SendAsync(Cipher(), Addr(BobNode));
        Assert.Single(linkA.Sent);
        await a.DisposeAsync();
    }

    [Fact]
    public async Task Rejects_NonChat_Frames()
    {
        var (a, _, _, _) = await StartPairAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            a.SendAsync(new byte[] { 0x50, 1, 2 }, Addr(BobNode)).AsTask()); // presence
        await Assert.ThrowsAsync<ArgumentException>(() =>
            a.SendAsync(Array.Empty<byte>(), Addr(BobNode)).AsTask());
        await a.DisposeAsync();
    }

    [Fact]
    public async Task Dropped_Chunk_Triggers_RetryRequest_And_Resend()
    {
        // Bob gets one Receive-Retry budget: the first resend must succeed.
        var (a, b, linkA, linkB) = await StartPairAsync(aliceRetries: 0, bobRetries: 1);

        var payload = new byte[1000];
        new Random(3).NextBytes(payload);
        payload[0] = 0x02;

        var dropped = 0;
        var totalChunks = (payload.Length + (233 - ChunkCodec.HeaderLength) - 1) / (233 - ChunkCodec.HeaderLength);
        linkA.BeforeDeliver = (_, frame) =>
        {
            // Drop the last chunk of the first datagram (the N-1 of N stall case).
            if (ChunkCodec.TryParse(frame, out var chunk) && chunk.Number == chunk.Total &&
                System.Threading.Interlocked.CompareExchange(ref dropped, 1, 0) == 0)
                return false;
            return true;
        };

        await a.SendAsync(payload, Addr(BobNode));
        Assert.Equal(payload, await NextInboundAsync(b, TimeSpan.FromSeconds(15)));

        Assert.Equal(1, dropped);
        // Sender resent the whole datagram: original chunks + full resend under a new id.
        Assert.Equal(totalChunks * 2, linkA.Sent.Count);
        // The retry request (0x33) flowed from Bob to Alice.
        Assert.Contains(linkB.Sent, s => s.Payload.Length > 0 && s.Payload[0] == RetryRequestCodec.FrameByte);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task ThirdParty_Chunks_Do_Not_Corrupt_Others_Assembly()
    {
        var (a, b, _, linkB) = await StartPairAsync();
        var eve = new FakeMeshLink(EveNode) { Peer = linkB };
        await eve.StartAsync();

        // A partial chunk series from a third party sits in Bob's reassembler.
        var junk = new byte[ChunkCodec.HeaderLength + 3];
        ChunkCodec.WriteChunk(junk, "eVe000", number: 1, total: 3, crc: 0x1234, [9, 9, 9]);
        await eve.SendAsync(BobNode, junk);

        var payload = new byte[1000];
        new Random(11).NextBytes(payload);
        payload[0] = 0x02;
        await a.SendAsync(payload, Addr(BobNode));

        Assert.Equal(payload, await NextInboundAsync(b, TimeSpan.FromSeconds(10)));
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Invite_Registers_Sender_In_Directory()
    {
        var (a, b, _, _) = await StartPairAsync(withNetworkIds: true);
        await a.SendAsync(Invite(NetworkId), Addr(BobNode));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline &&
               !b.Directory.GetActiveDestinations(NetworkId).Any(m => m.ToNodeNumber() == AliceNode))
            await Task.Delay(25);

        Assert.Contains(b.Directory.GetActiveDestinations(NetworkId), m => m.ToNodeNumber() == AliceNode);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task Invite_Registers_Sender_For_Its_NetworkId()
    {
        var (a, b, _, _) = await StartPairAsync(withNetworkIds: true);
        var foreign = Enumerable.Range(100, 12).Select(i => (byte)i).ToArray();
        await a.SendAsync(Invite(foreign), Addr(BobNode));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && b.Directory.GetActiveDestinations(foreign).Count == 0)
            await Task.Delay(25);

        Assert.Contains(b.Directory.GetActiveDestinations(foreign), m => m.ToNodeNumber() == AliceNode);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public async Task DirectorySnapshot_Is_Sent_On_Change_And_Merged()
    {
        var (a, b, linkA, _) = await StartPairAsync(withNetworkIds: true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline &&
               !linkA.Sent.Any(s => s.Payload.Length > 0 && s.Payload[0] == 0x32))
            await Task.Delay(25);

        Assert.Contains(linkA.Sent, s => s.Payload.Length > 0 && s.Payload[0] == 0x32);

        // Bob must know Alice's Self binding from her snapshot.
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline &&
               !b.Directory.Snapshot().Any(x =>
                   x.NetworkId.AsSpan().SequenceEqual(NetworkId) &&
                   new MeshId(x.MeshId).ToNodeNumber() == AliceNode))
            await Task.Delay(25);

        Assert.Contains(b.Directory.Snapshot(), x =>
            x.NetworkId.AsSpan().SequenceEqual(NetworkId) &&
            new MeshId(x.MeshId).ToNodeNumber() == AliceNode);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }

    [Fact]
    public void RetryConfig_Backoff_Is_Exponential()
    {
        var cfg = new RetryConfig();
        Assert.Equal(TimeSpan.FromSeconds(1), cfg.SendDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(4), cfg.SendDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(2), cfg.RxDelay(1));
    }

    [Fact]
    public void SessionSetup_NetworkId_Is_Extracted()
    {
        var frame = new byte[1 + 12];
        frame[0] = 0x04;
        Array.Copy(NetworkId, 0, frame, 1, 12);
        Assert.True(FrameNetworkId.TryExtract(frame, out var net));
        Assert.Equal(NetworkId, net);
    }
}
