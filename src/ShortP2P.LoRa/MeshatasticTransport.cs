using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ShortP2P.LoRa.Chunking;
using ShortP2P.LoRa.Directory;
using ShortP2P.LoRa.Link;
using ShortP2P.LoRa.Reliability;
using ShortP2P.Transport.Abstractions;

namespace ShortP2P.LoRa;

/// <summary>
///     Meshatastic transport: ShortP2P <see cref="ITransport" /> (kind 5) over a Meshtastic board link.
///     Carries ShortP2P wire frames unchanged; splits large datagrams into chunks with CRC and
///     Send/Receive-Retry (REQ-TR-1, REQ-CHUNK-1, REQ-RETRY-1, REQ-RX-1), and maintains the
///     NetworkId → MeshId directory with snapshot sharing (REQ-DIR-2..4).
/// </summary>
public sealed class MeshatasticTransport : ITransport
{
    /// <summary>Broadcast radio destination; never stored in the directory.</summary>
    public const uint BroadcastNode = 0xFFFFFFFF;

    private static readonly byte[] ChatFrameBytes = [0x30, 0x01, 0x04, 0x02];

    private readonly MeshatasticTransportOptions _options;
    private readonly IMeshLink _link;
    private readonly bool _ownsLink;
    private readonly ILogger? _logger;
    private readonly Channel<TransportReceiveMessage> _channel = Channel.CreateUnbounded<TransportReceiveMessage>();
    private readonly ChunkReassembler _reassembler = new();
    private readonly SemaphoreSlim _txWindow = new(1, 1);
    private readonly ConcurrentDictionary<string, OutboxEntry> _outbox = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _resendCounts = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _sweepTask;
    private int _snapshotPending;
    private bool _started;

    public MeshatasticTransport(MeshatasticTransportOptions options, ILogger? logger = null)
        : this(options, MeshtasticGate.Instance.CreateLink(options.ComPort, options.BaudRate, logger),
            ownsLink: true, logger)
    {
    }

    /// <summary>Transport over a caller-provided board link (tests / custom hardware).</summary>
    public MeshatasticTransport(MeshatasticTransportOptions options, IMeshLink link, ILogger? logger = null)
        : this(options, link, ownsLink: false, logger)
    {
    }

    private MeshatasticTransport(MeshatasticTransportOptions options, IMeshLink link, bool ownsLink, ILogger? logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _ownsLink = ownsLink;
        _logger = logger;
        Directory = new MeshDirectory { IsServerNetworkId = options.IsServerNetworkId };
        Directory.Changed += OnDirectoryChanged;
    }

    public MeshDirectory Directory { get; }

    public MeshId? LocalMeshId { get; private set; }

    public byte Kind => TransportKind.Meshtastic;

    public ChannelReader<TransportReceiveMessage> Inbound => _channel.Reader;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            return;
        _options.Validate();

        if (_options.DirectoryStore != null)
            Directory.Load(await _options.DirectoryStore.LoadAsync(cancellationToken).ConfigureAwait(false));

        await _link.StartAsync(cancellationToken).ConfigureAwait(false);
        var node = _link.LocalNodeNumber;
        if (node == null)
        {
            await _link.StopAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("LoRa board did not report its node number after connect.");
        }

        LocalMeshId = MeshId.FromNodeNumber(node.Value);
        _logger?.LogInformation("Meshatastic transport up on node {Node}", LocalMeshId.ToHex());

        if (_options.LocalNetworkId != null)
            Directory.Remember(_options.LocalNetworkId, LocalMeshId, MeshSubject.Self, confirmed: true, DateTime.UtcNow);

        _link.Received += OnLinkFrame;
        _started = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sweepTask = Task.Run(() => SweepLoopAsync(_cts.Token));

        QueueDirectorySnapshot();
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _started = false;
        _link.Received -= OnLinkFrame;

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        if (_sweepTask != null)
        {
            try
            {
                await _sweepTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }

            _sweepTask = null;
        }

        await _link.StopAsync(cancellationToken).ConfigureAwait(false);
        await PersistDirectoryAsync(CancellationToken.None).ConfigureAwait(false);
        _logger?.LogInformation("Meshatastic transport stopped");
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Payload must be one ShortP2P datagram (first byte 0x30 / 0x01 / 0x04 / 0x02).
    ///     Destination kind must be 5 (one MeshId; 0xFFFFFFFF = broadcast).
    ///     Large datagrams are chunked with CRC; chunks are retried per Send-Retry.
    /// </remarks>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, TransportAddress destination,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("Transport is not started.");
        if (payload.Length == 0)
            throw new ArgumentException("Payload is empty.", nameof(payload));
        if (Array.IndexOf(ChatFrameBytes, payload.Span[0]) < 0)
            throw new ArgumentException(
                $"Not a ShortP2P frame (first byte 0x{payload.Span[0]:X2}). Invite 0x30, handshake 0x01, session setup 0x04, cipher 0x02 expected.",
                nameof(payload));

        var meshId = MeshatasticTransportAddress.FromTransportAddress(destination);
        if (!meshId.TryToNodeNumber(out var node))
            throw new ArgumentException("Only 4-byte (node number) MeshIds are supported by this link.", nameof(destination));

        await SendDatagramAsync(node, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Host hook: after the sender of a cipher frame is known, bind it (REQ-DIR-3, step 4).</summary>
    public void Remember(byte[] networkId, MeshId meshId)
    {
        Directory.Remember(networkId, meshId, MeshSubject.Peer, confirmed: true, DateTime.UtcNow);
        _ = PersistDirectoryAsync(CancellationToken.None);
    }

    /// <summary>Nodes the board reported in its node database. Empty when the link has no catalog.</summary>
    public IReadOnlyList<MeshtasticRadioNode> RadioNodes =>
        _link is IMeshNodeCatalog catalog ? catalog.Nodes : [];

    /// <summary>Ask the board to resend its node database (want_config) and wait until it finishes.</summary>
    public ValueTask RefreshRadioNodesAsync(CancellationToken cancellationToken = default)
    {
        if (_link is IMeshNodeCatalog catalog)
            return catalog.RefreshAsync(cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>Inbound presence ping (frame 0x31): from node, to node, raw frame.</summary>
    public event Action<uint, uint, byte[]>? PresenceReceived;

    /// <summary>Send one already-framed datagram (presence 0x31, directory, retry). Not a chat frame.</summary>
    public Task SendOpaqueAsync(uint node, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        return SendDatagramAsync(node, payload, cancellationToken);
    }

    /// <summary>Broadcast the directory so other TorgLink radios can answer a scan.</summary>
    public async Task AnnounceDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var localId = _options.LocalNetworkId;
        if (!_started || localId == null)
            return;
        var snapshot = MeshDirectorySnapshotCodec.Encode(localId, Directory.Snapshot());
        await SendDatagramAsync(BroadcastNode, snapshot, cancellationToken).ConfigureAwait(false);
    }

    // ---- datagram send path ----

    private void EnsureTxAllowed()
    {
        if (_options.Jurisdiction.IsUnset)
            throw new InvalidOperationException(
                "Region is UNSET: no TX until a jurisdiction profile is chosen (REQ-RF-2).");
    }

    private async Task SendDatagramAsync(uint node, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _txWindow.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (payload.Length <= _options.MaxRadioPayloadBytes)
            {
                await SendFrameWithRetryAsync(node, payload, cancellationToken).ConfigureAwait(false);
                return;
            }

            var id = ChunkCodec.NewId();
            var crc = Crc32.Compute(payload.Span);
            var chunkDataSize = _options.MaxRadioPayloadBytes - ChunkCodec.HeaderLength;
            var total = (ushort)((payload.Length + chunkDataSize - 1) / chunkDataSize);
            var frame = new byte[_options.MaxRadioPayloadBytes];

            for (ushort number = 1; number <= total; number++)
            {
                var offset = (number - 1) * chunkDataSize;
                var take = Math.Min(chunkDataSize, payload.Length - offset);
                var written = ChunkCodec.WriteChunk(frame, id, number, total, crc, payload.Span.Slice(offset, take));
                await SendFrameWithRetryAsync(node, frame.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }

            _outbox[OutboxKey(node, id)] = new OutboxEntry(payload.ToArray(), node, DateTime.UtcNow);
            _logger?.LogTrace("TX {Bytes} bytes to {Node:X8} as {Total} chunks (id {Id})", payload.Length, node, total,
                id);
        }
        finally
        {
            _txWindow.Release();
        }
    }

    private async Task SendFrameWithRetryAsync(uint node, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        var max = _options.Retry.SendMaxRetries;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _link.SendAsync(node, frame, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < max)
            {
                var delay = _options.Retry.SendDelay(attempt);
                _logger?.LogWarning(ex, "TX to {Node:X8} failed (attempt {Attempt}); retrying in {Delay}", node,
                    attempt + 1, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // ---- receive path ----

    private void OnLinkFrame(MeshLinkFrame frame)
    {
        var bytes = frame.Payload;
        if (bytes.Length == 0)
            return;

        var first = bytes[0];
        if (ChunkCodec.IsLetter(first))
        {
            OnChunkFrame(frame.From, bytes);
            return;
        }

        switch (first)
        {
            case MeshDirectorySnapshotCodec.FrameByte:
                OnSnapshotFrame(bytes);
                return;
            case RetryRequestCodec.FrameByte:
                OnRetryRequestFrame(frame.From, bytes);
                return;
            case 0x31:
                PresenceReceived?.Invoke(frame.From, frame.To, bytes);
                return;
            case 0x30:
            case 0x01:
            case 0x04:
            case 0x02:
                ProcessDatagram(bytes, frame.From);
                return;
            default:
                _logger?.LogWarning("RX unknown frame byte 0x{First:X2} from {From:X8}; dropped", first, frame.From);
                return;
        }
    }

    private void OnChunkFrame(uint from, byte[] bytes)
    {
        if (!ChunkCodec.TryParse(bytes, out var chunk))
        {
            _logger?.LogWarning("RX malformed chunk from {From:X8}; dropped", from);
            return;
        }

        var result = _reassembler.Add(MeshId.FromNodeNumber(from), chunk, DateTime.UtcNow, out var datagram);
        switch (result)
        {
            case ChunkAddResult.Completed:
                ProcessDatagram(datagram!, from);
                break;
            case ChunkAddResult.CrcMismatch:
                RequestResend(from, chunk.Id, RetryRequestCodec.ReasonCrcMismatch);
                break;
            case ChunkAddResult.Inconsistent:
            case ChunkAddResult.UnknownCrcType:
                RequestResend(from, chunk.Id, RetryRequestCodec.ReasonInconsistentCrc);
                break;
            case ChunkAddResult.NeedMore:
            case ChunkAddResult.Duplicate:
            default:
                break;
        }
    }

    private void ProcessDatagram(byte[] datagram, uint fromNode)
    {
        if (datagram.Length > 0 && datagram[0] == 0x31)
        {
            PresenceReceived?.Invoke(fromNode, 0, datagram);
            return;
        }

        var from = MeshId.FromNodeNumber(fromNode);
        if (FrameNetworkId.TryExtract(datagram, out var networkId))
        {
            Directory.Remember(networkId, from, MeshSubject.Peer, confirmed: true, DateTime.UtcNow);
            _ = PersistDirectoryAsync(CancellationToken.None);
        }

        var frame = datagram.Length > 0 ? datagram[0] : (byte)0;
        _logger?.LogInformation(
            "RX chat frame 0x{Frame:X2} ({Bytes} bytes) from {From}",
            frame, datagram.Length, from.ToHex());
        if (!_channel.Writer.TryWrite(new TransportReceiveMessage(datagram,
                MeshatasticTransportAddress.ToTransportAddress(from))))
            _logger?.LogWarning("RX chat frame 0x{Frame:X2} from {From} dropped: inbound channel full", frame,
                from.ToHex());
    }

    private void OnSnapshotFrame(byte[] bytes)
    {
        if (!MeshDirectorySnapshotCodec.TryParse(bytes, out _, out var bindings))
        {
            _logger?.LogWarning("RX malformed directory snapshot; dropped");
            return;
        }

        Directory.MergeSnapshot(bindings, DateTime.UtcNow);
        _ = PersistDirectoryAsync(CancellationToken.None);
    }

    // ---- retry requests (0x33) ----

    private void RequestResend(uint fromNode, string compressedId, byte reason)
    {
        var key = RetryKey(fromNode, compressedId);
        var count = _resendCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
        if (count > _options.Retry.RxMaxRetries)
        {
            _logger?.LogWarning("Message {Id} from {From:X8}: giving up after {Max} resend requests", compressedId,
                fromNode, _options.Retry.RxMaxRetries);
            _resendCounts.TryRemove(key, out _);
            return;
        }

        var delay = _options.Retry.RxDelay(count - 1);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            var request = RetryRequestCodec.Encode(MeshId.FromNodeNumber(fromNode), compressedId, reason, (byte)count);
            try
            {
                await SendDatagramAsync(fromNode, request, CancellationToken.None).ConfigureAwait(false);
                _logger?.LogInformation("Asked {From:X8} to resend {Id} (reason {Reason}, retry {Retry})", fromNode,
                    compressedId, reason, count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to send resend request for {Id} to {From:X8}", compressedId, fromNode);
            }
        });
    }

    private void OnRetryRequestFrame(uint fromNode, byte[] bytes)
    {
        if (!RetryRequestCodec.TryParse(bytes, out var txMeshId, out var id, out var reason, out _))
            return;

        if (LocalMeshId == null || !txMeshId.Equals(LocalMeshId))
            return; // not addressed to us

        // The requester is missing our message; resend the whole message from chunk 1
        // at the next TX window, with a new compressed id (REQ-RETRY-2).
        OutboxEntry? entry = null;
        foreach (var pair in _outbox)
            if (pair.Key.EndsWith(":" + id, StringComparison.Ordinal))
            {
                entry = pair.Value;
                break;
            }

        if (entry == null)
        {
            _logger?.LogWarning("Resend requested for unknown message {Id} (reason {Reason})", id, reason);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SendDatagramAsync(fromNode, entry.Datagram, CancellationToken.None).ConfigureAwait(false);
                _logger?.LogInformation("Resent message ({Bytes} bytes) to {To:X8} on request", entry.Datagram.Length,
                    fromNode);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Resend to {To:X8} failed", fromNode);
            }
        });
    }

    // ---- stalled assemblies sweep ----

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.RxSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                foreach (var stalled in _reassembler.GetStalled(now, _options.Retry.RxTimeout))
                {
                    if (!stalled.From.TryToNodeNumber(out var fromNode))
                    {
                        _reassembler.Drop(stalled.From, stalled.Id);
                        continue;
                    }

                    var retry = _reassembler.NoteRetry(stalled.From, stalled.Id);
                    if (retry > _options.Retry.RxMaxRetries)
                    {
                        _reassembler.Drop(stalled.From, stalled.Id);
                        _logger?.LogWarning("Dropped stalled message {Id} from {From} after {Max} retries", stalled.Id,
                            stalled.From.ToHex(), _options.Retry.RxMaxRetries);
                        continue;
                    }

                    RequestResend(fromNode, stalled.Id, RetryRequestCodec.ReasonChunkTimeout);
                }

                PurgeOutbox(now);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void PurgeOutbox(DateTime now)
    {
        foreach (var pair in _outbox)
            if (now - pair.Value.SentUtc > _options.OutboxRetention)
                _outbox.TryRemove(pair.Key, out _);

        foreach (var pair in _resendCounts)
            _resendCounts.TryRemove(pair.Key, out _); // per-message budget restarts with a fresh message
    }

    // ---- directory snapshot sharing (0x32) ----

    private void OnDirectoryChanged()
    {
        QueueDirectorySnapshot();
    }

    private void QueueDirectorySnapshot()
    {
        if (!_started || _options.LocalNetworkId == null)
            return;
        if (System.Threading.Interlocked.Exchange(ref _snapshotPending, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false); // coalesce bursts
            System.Threading.Interlocked.Exchange(ref _snapshotPending, 0);
            try
            {
                await SendDirectorySnapshotAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to share the directory snapshot");
            }
        });
    }

    private async Task SendDirectorySnapshotAsync()
    {
        var localId = _options.LocalNetworkId;
        if (localId == null)
            return;

        var remotes = Directory.GetActiveRemoteBindings();
        if (remotes.Count == 0)
        {
            // No unicast target known: broadcast only our own bindings (REQ-DIR-4).
            var selfSnapshot = MeshDirectorySnapshotCodec.Encode(localId, Directory.GetSelfBindings());
            await SendDatagramAsync(BroadcastNode, selfSnapshot, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var payload = MeshDirectorySnapshotCodec.Encode(localId, Directory.Snapshot());
        foreach (var binding in remotes)
            if (new MeshId(binding.MeshId).TryToNodeNumber(out var node))
                await SendDatagramAsync(node, payload, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistDirectoryAsync(CancellationToken cancellationToken)
    {
        if (_options.DirectoryStore == null)
            return;
        try
        {
            await _options.DirectoryStore.SaveAsync(Directory.Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist the mesh directory");
        }
    }

    // ---- helpers ----

    private static string OutboxKey(uint node, string id)
    {
        return node.ToString("X8") + ":" + id;
    }

    private static string RetryKey(uint node, string id)
    {
        return node.ToString("X8") + ":" + id;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_ownsLink)
            await _link.DisposeAsync().ConfigureAwait(false);
        _txWindow.Dispose();
        _channel.Writer.TryComplete();
    }

    private sealed record OutboxEntry(byte[] Datagram, uint DestNode, DateTime SentUtc);
}
