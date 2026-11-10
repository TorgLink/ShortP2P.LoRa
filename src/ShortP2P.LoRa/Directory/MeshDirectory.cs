namespace ShortP2P.LoRa.Directory;

/// <summary>
///     Local map NetworkId (12 bytes) → many Mesh Ids, for self, peers, and servers (REQ-DIR-2).
///     Unique key is (NetworkId, MeshId). A confirmed observation moves a MeshId to the new owner;
///     an unconfirmed (snapshot-sourced) binding never removes or replaces a confirmed one.
/// </summary>
public sealed class MeshDirectory
{
    private readonly object _sync = new();
    private readonly Dictionary<string, MeshBinding> _bindings = new(StringComparer.Ordinal);

    /// <summary>Raised after the directory content changed (used to trigger snapshot sharing).</summary>
    public event Action? Changed;

    /// <summary>Resolves whether a NetworkId belongs to a messenger server (host-registered).</summary>
    public Func<byte[], bool>? IsServerNetworkId { get; set; }

    public IReadOnlyList<MeshBinding> Snapshot()
    {
        lock (_sync)
            return _bindings.Values.ToArray();
    }

    public void Load(IEnumerable<MeshBinding> bindings)
    {
        lock (_sync)
        {
            _bindings.Clear();
            foreach (var b in bindings)
                _bindings[Key(b.NetworkId, b.MeshId)] = b;
        }
    }

    private MeshSubject SubjectFor(byte[] networkId, MeshSubject fallback)
    {
        if (IsServerNetworkId?.Invoke(networkId) == true)
            return MeshSubject.Server;
        return fallback;
    }

    /// <summary>
    ///     Learn or refresh one binding.
    ///     Confirmed = this device was the local radio, or a cleartext frame from that MeshId
    ///     carried this NetworkId (invite 0x30 / session setup 0x04), or the host confirmed the sender.
    /// </summary>
    public MeshBinding Remember(ReadOnlySpan<byte> networkId, MeshId meshId, MeshSubject subjectHint,
        bool confirmed, DateTime utcNow)
    {
        if (networkId.Length != MeshBinding.NetworkIdLength)
            throw new ArgumentOutOfRangeException(nameof(networkId), "NetworkId must be 12 bytes (wire form).");

        var net = networkId.ToArray();
        var mesh = meshId.Bytes.ToArray();
        var subject = SubjectFor(net, subjectHint);
        MeshBinding result;
        var changed = false;

        lock (_sync)
        {
            var meshKey = MeshKey(mesh);
            if (confirmed)
            {
                // A confirmed observation moves the MeshId to the new owner.
                foreach (var (k, v) in _bindings.ToArray())
                    if (MeshKey(v.MeshId) == meshKey && !v.NetworkId.AsSpan().SequenceEqual(net))
                    {
                        _bindings.Remove(k);
                        changed = true;
                    }
            }
            else
            {
                // An unconfirmed claim must not move a MeshId that is already confirmed elsewhere.
                foreach (var v in _bindings.Values)
                    if (MeshKey(v.MeshId) == meshKey && v.Confirmed && !v.NetworkId.AsSpan().SequenceEqual(net))
                        return v;
            }

            var key = Key(net, mesh);
            if (_bindings.TryGetValue(key, out var existing))
            {
                var merged = existing with
                {
                    Subject = subject,
                    IsActive = true, // hearing this device again re-activates the binding
                    Confirmed = existing.Confirmed || confirmed,
                    LastSeenUtc = utcNow
                };
                _bindings[key] = merged;
                result = merged;
                changed = !existing.IsActive || existing.Confirmed != merged.Confirmed ||
                          existing.Subject != merged.Subject;
            }
            else
            {
                result = new MeshBinding(net, mesh, subject, IsActive: true, Confirmed: confirmed, LastSeenUtc: utcNow);
                _bindings[key] = result;
                changed = true;
            }
        }

        if (changed)
            Changed?.Invoke();
        return result;
    }

    /// <summary>Active destinations for a NetworkId, confirmed ids first (REQ-DIR-2).</summary>
    public IReadOnlyList<MeshId> GetActiveDestinations(ReadOnlySpan<byte> networkId)
    {
        var hex = Convert.ToHexString(networkId);
        lock (_sync)
        {
            return _bindings.Values
                .Where(b => b.IsActive && b.NetworkIdHex == hex)
                .OrderByDescending(b => b.Confirmed)
                .ThenByDescending(b => b.LastSeenUtc)
                .Select(b => new MeshId(b.MeshId))
                .ToArray();
        }
    }

    /// <summary>
    ///     Resolve the current NetworkId owner of a MeshId (radio / link layer → subscriber layer).
    ///     Prefers a confirmed active binding; does not treat MeshId as NetworkId.
    /// </summary>
    public bool TryGetOwnerNetworkId(MeshId meshId, out byte[] networkIdWire12, out bool confirmed)
    {
        networkIdWire12 = [];
        confirmed = false;
        var meshKey = MeshKey(meshId.Bytes.ToArray());
        lock (_sync)
        {
            MeshBinding? best = null;
            foreach (var b in _bindings.Values)
            {
                if (!b.IsActive || MeshKey(b.MeshId) != meshKey)
                    continue;
                if (best == null ||
                    (b.Confirmed && !best.Confirmed) ||
                    (b.Confirmed == best.Confirmed && b.LastSeenUtc > best.LastSeenUtc))
                    best = b;
            }

            if (best == null)
                return false;

            networkIdWire12 = (byte[])best.NetworkId.Clone();
            confirmed = best.Confirmed;
            return true;
        }
    }

    /// <summary>All active bindings of peers and servers (snapshot unicast targets, REQ-DIR-4).</summary>
    public IReadOnlyList<MeshBinding> GetActiveRemoteBindings()
    {
        lock (_sync)
            return _bindings.Values
                .Where(b => b.IsActive && b.Subject != MeshSubject.Self)
                .ToArray();
    }

    public IReadOnlyList<MeshBinding> GetSelfBindings()
    {
        lock (_sync)
            return _bindings.Values.Where(b => b.Subject == MeshSubject.Self).ToArray();
    }

    /// <summary>Merge an inbound 0x32 snapshot. Second-hand entries are never confirmed for us.</summary>
    public void MergeSnapshot(IReadOnlyList<MeshBinding> bindings, DateTime utcNow)
    {
        var any = false;
        foreach (var b in bindings)
        {
            var before = Count;
            Remember(b.NetworkId, new MeshId(b.MeshId), b.Subject, confirmed: false, utcNow);
            if (Count != before)
                any = true;
        }

        _ = any;
    }

    private int Count
    {
        get
        {
            lock (_sync)
                return _bindings.Count;
        }
    }

    private static string Key(byte[] networkId, byte[] meshId)
    {
        return Convert.ToHexString(networkId) + ":" + Convert.ToHexString(meshId);
    }

    private static string MeshKey(byte[] meshId)
    {
        return Convert.ToHexString(meshId);
    }
}
