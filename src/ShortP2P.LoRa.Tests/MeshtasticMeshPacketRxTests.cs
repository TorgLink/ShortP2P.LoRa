using ShortP2P.LoRa.Link;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class MeshtasticMeshPacketRxTests
{
    [Fact]
    public void ReadFromRadio_PrivateApp_DeliversFrame()
    {
        // Goal: decoded PrivateApp MeshPacket becomes MeshLinkFrame.
        // Expected: from=0xAA, to=0xFFFFFFFF, payload [0x31].
        var payload = BuildFromRadioMeshPacket(
            from: 0xAAu,
            to: 0xFFFFFFFFu,
            decodedData: BuildData(MeshtasticProto.PortNumPrivateApp, [0x31]));

        MeshLinkFrame? frame = null;
        string? skipped = null;
        MeshtasticProto.ReadFromRadio(
            payload,
            onPacket: f => frame = f,
            onMyNodeNum: null,
            onSkippedPacket: (_, _, _, reason) => skipped = reason);

        Assert.True(frame.HasValue);
        Assert.Equal(0xAAu, frame.Value.From);
        Assert.Equal(0xFFFFFFFFu, frame.Value.To);
        Assert.Equal(new byte[] { 0x31 }, frame.Value.Payload);
        Assert.Null(skipped);
    }

    [Fact]
    public void ReadFromRadio_OtherPortnum_ReportsSkipped()
    {
        // Goal: non-PrivateApp decoded packets are not delivered as chat frames.
        // Expected: onSkipped with portnum reason; onPacket never called.
        var payload = BuildFromRadioMeshPacket(
            from: 0x11u,
            to: 0xFFFFFFFFu,
            decodedData: BuildData(portNum: 1, [0x48, 0x69])); // TEXT_MESSAGE_APP

        MeshLinkFrame? frame = null;
        uint? skippedPort = null;
        string? skippedReason = null;
        MeshtasticProto.ReadFromRadio(
            payload,
            onPacket: f => frame = f,
            onMyNodeNum: null,
            onSkippedPacket: (_, _, port, reason) =>
            {
                skippedPort = port;
                skippedReason = reason;
            });

        Assert.Null(frame);
        Assert.Equal(1u, skippedPort);
        Assert.Contains("not PrivateApp", skippedReason);
    }

    [Fact]
    public void ReadFromRadio_EncryptedOnly_ReportsSkipped()
    {
        // Goal: MeshPacket.encrypted without decoded is visible for channel/PSK diagnosis.
        // Expected: onSkipped "encrypted-only"; onPacket never called.
        // FromRadio.packet = MeshPacket { from=1 fixed32=0x22, encrypted=5 bytes=01 }
        // MeshPacket: field1 fixed32 + field5 length-delimited
        byte[] meshPacket =
        [
            0x0D, 0x22, 0x00, 0x00, 0x00, // from = 0x22
            0x2A, 0x01, 0x01 // encrypted = [0x01]
        ];
        byte[] fromRadio =
        [
            0x12, (byte)meshPacket.Length,
            .. meshPacket
        ];

        MeshLinkFrame? frame = null;
        string? skipped = null;
        MeshtasticProto.ReadFromRadio(
            fromRadio,
            onPacket: f => frame = f,
            onMyNodeNum: null,
            onSkippedPacket: (_, _, _, reason) => skipped = reason);

        Assert.Null(frame);
        Assert.Contains("encrypted-only", skipped);
    }

    private static byte[] BuildData(uint portNum, byte[] payload)
    {
        using var data = new MemoryStream();
        WriteVarint(data, (1 << 3) | 0);
        WriteVarint(data, portNum);
        WriteVarint(data, (2 << 3) | 2);
        WriteVarint(data, (uint)payload.Length);
        data.Write(payload);
        return data.ToArray();
    }

    private static byte[] BuildFromRadioMeshPacket(uint from, uint to, byte[] decodedData)
    {
        using var packet = new MemoryStream();
        WriteVarint(packet, (1 << 3) | 5);
        WriteFixed32(packet, from);
        WriteVarint(packet, (2 << 3) | 5);
        WriteFixed32(packet, to);
        WriteVarint(packet, (4 << 3) | 2);
        WriteVarint(packet, (uint)decodedData.Length);
        packet.Write(decodedData);

        var packetBytes = packet.ToArray();
        using var fromRadio = new MemoryStream();
        WriteVarint(fromRadio, (2 << 3) | 2);
        WriteVarint(fromRadio, (uint)packetBytes.Length);
        fromRadio.Write(packetBytes);
        return fromRadio.ToArray();
    }

    private static void WriteVarint(Stream stream, uint value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteFixed32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        stream.Write(buf);
    }
}
