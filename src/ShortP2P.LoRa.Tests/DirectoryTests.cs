using ShortP2P.LoRa.Directory;
using ShortP2P.LoRa.Reliability;
using Xunit;

namespace ShortP2P.LoRa.Tests;

public class DirectoryTests
{
    private static byte[] Net(int seed)
    {
        var b = new byte[12];
        b[0] = (byte)seed;
        return b;
    }

    [Fact]
    public void RetryRequest_RoundTrip()
    {
        // Goal: RetryRequestCodec encode/parse preserves mesh id, chunk id, reason, and retry count.
        // Expected: TryParse succeeds with mesh AA..DD, id Ab9zQ2, reason 3, retry 2.
        var mesh = new MeshId(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        var frame = RetryRequestCodec.Encode(mesh, "Ab9zQ2", RetryRequestCodec.ReasonInconsistentCrc, 2);

        Assert.True(RetryRequestCodec.TryParse(frame, out var m, out var id, out var reason, out var retry));
        Assert.Equal(mesh, m);
        Assert.Equal("Ab9zQ2", id);
        Assert.Equal((byte)3, reason);
        Assert.Equal((byte)2, retry);
    }

    [Fact]
    public void Snapshot_RoundTrip_With_Flags()
    {
        // Goal: directory snapshot codec round-trips announcer, subjects, and active/confirmed flags.
        // Expected: frame starts with 0x32; four bindings parse with the same flags and subjects.
        var announcer = Net(1);
        var bindings = new List<MeshBinding>
        {
            new(Net(1), new byte[] { 1, 2, 3, 4 }, MeshSubject.Self, true, true, DateTime.MinValue),
            new(Net(1), new byte[] { 9, 9, 9, 9, 9, 9 }, MeshSubject.Self, false, true, DateTime.MinValue),
            new(Net(2), new byte[] { 5, 6, 7, 8 }, MeshSubject.Peer, true, false, DateTime.MinValue),
            new(Net(3), new byte[] { 0xDE, 0xAD }, MeshSubject.Server, true, true, DateTime.MinValue)
        };

        var frame = MeshDirectorySnapshotCodec.Encode(announcer, bindings);
        Assert.Equal(0x32, frame[0]);

        Assert.True(MeshDirectorySnapshotCodec.TryParse(frame, out var ann, out var parsed));
        Assert.Equal(announcer, ann);
        Assert.Equal(4, parsed.Count);
        Assert.Contains(parsed, b => b.MeshIdHex == "01020304" && b.IsActive && b.Confirmed && b.Subject == MeshSubject.Self);
        Assert.Contains(parsed, b => b.MeshIdHex == "090909090909" && !b.IsActive && b.Subject == MeshSubject.Self);
        Assert.Contains(parsed, b => b.MeshIdHex == "05060708" && b.Subject == MeshSubject.Peer && !b.Confirmed);
        Assert.Contains(parsed, b => b.MeshIdHex == "DEAD" && b.Subject == MeshSubject.Server);
    }

    [Fact]
    public void Remember_Confirmed_Moves_MeshId_To_New_Owner()
    {
        // Goal: a confirmed Remember for the same MeshId moves ownership to the new NetworkId.
        // Expected: one binding under Net(2), confirmed; TryGetOwnerNetworkId returns Net(2).
        var d = new MeshDirectory();
        var now = DateTime.UtcNow;
        var mesh = MeshId.FromNodeNumber(0xAABBCCDD);

        d.Remember(Net(1), mesh, MeshSubject.Peer, confirmed: false, now);
        d.Remember(Net(2), mesh, MeshSubject.Peer, confirmed: true, now);

        var all = d.Snapshot();
        Assert.Single(all);
        Assert.Equal(Convert.ToHexString(Net(2)), all[0].NetworkIdHex);
        Assert.True(all[0].Confirmed);

        Assert.True(d.TryGetOwnerNetworkId(mesh, out var owner, out var confirmed));
        Assert.Equal(Net(2), owner);
        Assert.True(confirmed);
    }

    [Fact]
    public void TryGetOwnerNetworkId_Prefers_Confirmed_Binding()
    {
        // Goal: confirmed ownership updates and clears destinations for the previous NetworkId.
        // Expected: owner starts as Net(1), then moves to Net(9); Net(1) active destinations empty.
        var d = new MeshDirectory();
        var now = DateTime.UtcNow;
        var mesh = MeshId.FromNodeNumber(0x01020304);

        d.Remember(Net(1), mesh, MeshSubject.Peer, confirmed: true, now);
        Assert.True(d.TryGetOwnerNetworkId(mesh, out var owner, out var confirmed));
        Assert.Equal(Net(1), owner);
        Assert.True(confirmed);

        // Confirmed move to a different NetworkId (same radio module, new subscriber).
        d.Remember(Net(9), mesh, MeshSubject.Peer, confirmed: true, now.AddSeconds(1));
        Assert.True(d.TryGetOwnerNetworkId(mesh, out owner, out confirmed));
        Assert.Equal(Net(9), owner);
        Assert.True(confirmed);
        Assert.Empty(d.GetActiveDestinations(Net(1)));
    }

    [Fact]
    public void Unconfirmed_Snapshot_Does_Not_Replace_Confirmed()
    {
        // Goal: an unconfirmed claim must not displace a confirmed owner.
        // Expected: snapshot stays a single binding on Net(1).
        var d = new MeshDirectory();
        var now = DateTime.UtcNow;
        var mesh = MeshId.FromNodeNumber(0xAABBCCDD);

        d.Remember(Net(1), mesh, MeshSubject.Peer, confirmed: true, now);
        // A second-hand claim for another owner must not move the confirmed binding.
        d.Remember(Net(2), mesh, MeshSubject.Peer, confirmed: false, now);

        var all = d.Snapshot();
        Assert.Single(all);
        Assert.Equal(Convert.ToHexString(Net(1)), all[0].NetworkIdHex);
    }

    [Fact]
    public void One_NetworkId_Many_MeshIds_ActiveDestinations_ConfirmedFirst()
    {
        // Goal: multiple MeshIds under one NetworkId list confirmed destinations first.
        // Expected: two destinations; confirmed MeshId is index 0.
        var d = new MeshDirectory();
        var now = DateTime.UtcNow;
        var net = Net(7);
        var unconfirmed = MeshId.FromNodeNumber(1);
        var confirmed = MeshId.FromNodeNumber(2);

        d.Remember(net, unconfirmed, MeshSubject.Peer, confirmed: false, now);
        d.Remember(net, confirmed, MeshSubject.Peer, confirmed: true, now);

        var dest = d.GetActiveDestinations(net);
        Assert.Equal(2, dest.Count);
        Assert.Equal(confirmed, dest[0]);
    }

    [Fact]
    public void Server_Subject_Comes_From_Host_Registry()
    {
        // Goal: host IsServerNetworkId callback upgrades subject to Server in the snapshot.
        // Expected: Snapshot()[0].Subject == MeshSubject.Server.
        var serverNet = Net(42);
        var d = new MeshDirectory { IsServerNetworkId = id => id.AsSpan().SequenceEqual(serverNet) };
        d.Remember(serverNet, MeshId.FromNodeNumber(9), MeshSubject.Peer, confirmed: true, DateTime.UtcNow);

        Assert.Equal(MeshSubject.Server, d.Snapshot()[0].Subject);
    }

    [Fact]
    public async Task JsonStore_Persists_And_Loads()
    {
        // Goal: JsonFileMeshDirectoryStore saves bindings and reloads them intact.
        // Expected: one Self/confirmed binding with MeshIdHex 01020304 after LoadAsync.
        var path = Path.Combine(Path.GetTempPath(), "mesh-dir-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonFileMeshDirectoryStore(path);
            var bindings = new List<MeshBinding>
            {
                new(Net(1), new byte[] { 1, 2, 3, 4 }, MeshSubject.Self, true, true, DateTime.UtcNow)
            };
            await store.SaveAsync(bindings);
            var loaded = await store.LoadAsync();

            Assert.Single(loaded);
            Assert.Equal("01020304", loaded[0].MeshIdHex);
            Assert.True(loaded[0].Confirmed);
            Assert.Equal(MeshSubject.Self, loaded[0].Subject);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
