using ShortP2P.LoRa.Link;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class MeshtasticNodeInfoTests
{
    [Fact]
    public void ReadFromRadio_ParsesNodeInfoAndConfigComplete()
    {
        // Goal: ReadFromRadio extracts NodeInfo fields and config_complete_id from protobuf bytes.
        // Expected: node num 0x11, long name "AB", NodeId "!00000011"; config complete id = 5.
        // FromRadio.node_info: NodeInfo.num = 0x11, User.long_name = "AB"
        // 22 08 | 08 11 | 12 04 | 12 02 41 42
        byte[] nodeInfo =
        [
            0x22, 0x08,
            0x08, 0x11,
            0x12, 0x04,
            0x12, 0x02, 0x41, 0x42
        ];
        // FromRadio.config_complete_id = 5
        byte[] complete = [0x38, 0x05];

        MeshtasticRadioNode? node = null;
        uint? done = null;
        MeshtasticProto.ReadFromRadio(nodeInfo, null, null, n => node = n, id => done = id);
        MeshtasticProto.ReadFromRadio(complete, null, null, null, id => done = id);

        Assert.NotNull(node);
        Assert.Equal(0x11u, node.NodeNumber);
        Assert.Equal("AB", node.LongName);
        Assert.Equal("!00000011", node.NodeId);
        Assert.Equal("AB", node.DisplayName);
        Assert.Equal(0u, node.LastHeardUnix);
        Assert.Equal(5u, done);
    }

    [Fact]
    public void ReadFromRadio_ParsesNodeLastHeardFixed32()
    {
        // Goal: NodeInfo.last_heard (mesh.proto field 5, fixed32) is surfaced on the node.
        // Expected: last_heard 1700000000; a node without the field stays at 0 (covered above).
        // 0x2D is field 5, wire type 5 (fixed32). Value 1700000000 LE = 00 F1 53 65.
        byte[] nodeInfo =
        [
            0x22, 0x0D,
            0x08, 0x11,
            0x12, 0x04,
            0x12, 0x02, 0x41, 0x42,
            0x2D, 0x00, 0xF1, 0x53, 0x65
        ];

        MeshtasticRadioNode? node = null;
        MeshtasticProto.ReadFromRadio(nodeInfo, null, null, n => node = n);

        Assert.NotNull(node);
        Assert.Equal(0x11u, node.NodeNumber);
        Assert.Equal(1_700_000_000u, node.LastHeardUnix);
        Assert.Equal((byte)((MeshtasticProto.NodeInfoLastHeardField << 3) | 5), (byte)0x2D);
    }
}
