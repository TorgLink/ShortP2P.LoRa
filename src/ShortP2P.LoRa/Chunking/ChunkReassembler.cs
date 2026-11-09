namespace ShortP2P.LoRa.Chunking;

public enum ChunkAddResult
{
    /// <summary>Chunk stored; more chunks needed.</summary>
    NeedMore,

    /// <summary>All chunks present, CRC verified; datagram is ready.</summary>
    Completed,

    /// <summary>All chunks were present but the recomputed CRC does not match; the set was discarded.</summary>
    CrcMismatch,

    /// <summary>This chunk's CRC type / value / total disagrees with the rest of the datagram; the set was discarded.</summary>
    Inconsistent,

    /// <summary>Unknown CRC type byte.</summary>
    UnknownCrcType,

    /// <summary>Same chunk number already stored.</summary>
    Duplicate
}

/// <summary>
///     Reassembles chunked datagrams per (source MeshId, compressed id) (REQ-CHUNK-1, REQ-RX-1).
///     Chunk order on the wire does not matter.
/// </summary>
public sealed class ChunkReassembler
{
    private sealed class State
    {
        public required ushort Total;
        public required byte CrcType;
        public required uint Crc;
        public readonly Dictionary<ushort, byte[]> Chunks = new();
        public DateTime LastChunkUtc;
        public int RetryCount;
        public int ReceivedBytes;
        public int MaxChunkDataBytes;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    private static string KeyOf(MeshId from, string id)
    {
        return from.ToHex() + ":" + id;
    }

    public ChunkAddResult Add(MeshId from, in MeshChunk chunk, DateTime utcNow, out byte[]? datagram)
    {
        datagram = null;
        if (chunk.CrcType != Crc32.WireType)
            return ChunkAddResult.UnknownCrcType;

        var key = KeyOf(from, chunk.Id);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new State
            {
                Total = chunk.Total,
                CrcType = chunk.CrcType,
                Crc = chunk.Crc,
                LastChunkUtc = utcNow
            };
            _states[key] = state;
        }
        else
        {
            // Any disagreement between chunks of one datagram corrupts the whole set (REQ-RX-1).
            if (state.Total != chunk.Total || state.CrcType != chunk.CrcType || state.Crc != chunk.Crc)
            {
                _states.Remove(key);
                return ChunkAddResult.Inconsistent;
            }
        }

        state.LastChunkUtc = utcNow;
        if (state.Chunks.ContainsKey(chunk.Number))
            return ChunkAddResult.Duplicate;

        state.Chunks[chunk.Number] = chunk.Data;
        state.ReceivedBytes += chunk.Data.Length;
        if (chunk.Data.Length > state.MaxChunkDataBytes)
            state.MaxChunkDataBytes = chunk.Data.Length;

        if (state.Chunks.Count < state.Total)
            return ChunkAddResult.NeedMore;

        using var ms = new MemoryStream();
        for (ushort n = 1; n <= state.Total; n++)
        {
            if (!state.Chunks.TryGetValue(n, out var part))
                return ChunkAddResult.NeedMore; // should not happen: Count == Total and numbers are unique
            ms.Write(part, 0, part.Length);
        }

        _states.Remove(key);
        var bytes = ms.ToArray();
        if (Crc32.Compute(bytes) != state.Crc)
            return ChunkAddResult.CrcMismatch;

        datagram = bytes;
        return ChunkAddResult.Completed;
    }

    /// <summary>
    ///     Assemblies that saw no new chunk for a fixed <paramref name="timeout" />.
    /// </summary>
    public IReadOnlyList<(MeshId From, string Id, int RetryCount)> GetStalled(
        DateTime utcNow, TimeSpan timeout) =>
        GetStalled(utcNow, _ => timeout);

    /// <summary>
    ///     Assemblies that saw no new chunk for the timeout selected from estimated reassembled size.
    ///     Size estimate = received bytes + missingChunks × max observed chunk data length.
    /// </summary>
    public IReadOnlyList<(MeshId From, string Id, int RetryCount)> GetStalled(
        DateTime utcNow, Func<int, TimeSpan> timeoutForSize)
    {
        ArgumentNullException.ThrowIfNull(timeoutForSize);

        var result = new List<(MeshId, string, int)>();
        foreach (var (key, state) in _states)
        {
            var estimated = EstimateBytes(state);
            var timeout = timeoutForSize(estimated);
            if (utcNow - state.LastChunkUtc < timeout)
                continue;

            var sep = key.IndexOf(':');
            var from = new MeshId(Convert.FromHexString(key[..sep]));
            result.Add((from, key[(sep + 1)..], state.RetryCount));
        }

        return result;
    }

    private static int EstimateBytes(State state)
    {
        var missing = state.Total - state.Chunks.Count;
        if (missing <= 0)
            return state.ReceivedBytes;
        var perChunk = state.MaxChunkDataBytes;
        return state.ReceivedBytes + missing * perChunk;
    }

    /// <summary>Marks one Receive-Retry request for this assembly. Returns the new retry count.</summary>
    public int NoteRetry(MeshId from, string id)
    {
        var key = KeyOf(from, id);
        if (!_states.TryGetValue(key, out var state))
            return 0;
        state.RetryCount++;
        state.LastChunkUtc = DateTime.UtcNow;
        return state.RetryCount;
    }

    public void Drop(MeshId from, string id)
    {
        _states.Remove(KeyOf(from, id));
    }
}
