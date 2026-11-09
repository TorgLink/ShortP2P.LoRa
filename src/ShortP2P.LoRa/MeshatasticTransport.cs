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
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private readonly MeshtasticTxPriorityQueues _txQueues = new();
    private readonly ConcurrentDictionary<string, OutboxEntry> _outbox = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _resendCounts = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _sweepTask;
    private int _snapshotPending;
    private int _txPumpRunning;
    private int _txInFlight;
    private int _serialReconnectFailures;
    private bool _started;
    /// <summary>UTC deadline while the TX pump yields for incomplete RX (re-enqueue path).</summary>
    private DateTime _incompleteRxYieldDeadline;

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
    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, TransportAddress destination,
        CancellationToken cancellationToken = default) =>
        SendAsync(payload, destination, contentKind: null, cancellationToken);

    /// <summary>
    ///     Same as <see cref="SendAsync(ReadOnlyMemory{byte},TransportAddress,CancellationToken)" /> with an
    ///     optional content-class hint for cipher (0x02) priority (text vs binary size bands).
    /// </summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, TransportAddress destination,
        MeshtasticTxContentKind? contentKind, CancellationToken cancellationToken = default)
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

        await SendDatagramAsync(node, payload, contentKind, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     True while a datagram (including a multi-chunk radio train) is on the air or waiting in
    ///     the priority TX queues. Host presence / discovery should yield to this.
    /// </summary>
    public bool IsTxBusy =>
        Volatile.Read(ref _txInFlight) != 0 || !_txQueues.IsEmpty;

    /// <summary>Wait until TX queues and the current radio train drain, or until <paramref name="timeout"/>.</summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsTxBusy)
        {
            if (DateTime.UtcNow >= deadline)
                return !IsTxBusy;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Send one already-framed datagram (presence 0x31, directory, retry). Not a chat frame.</summary>
    public Task SendOpaqueAsync(uint node, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        SendOpaqueAsync(node, payload, contentKind: null, cancellationToken);

    /// <summary>
    ///     Opaque send with optional content-class hint (used when the frame is not control/signaling).
    /// </summary>
    public Task SendOpaqueAsync(uint node, ReadOnlyMemory<byte> payload, MeshtasticTxContentKind? contentKind,
        CancellationToken cancellationToken = default)
    {
        return SendDatagramAsync(node, payload, contentKind, cancellationToken);
    }

    /// <summary>Broadcast the directory so other TorgLink radios can answer a scan.</summary>
    public async Task AnnounceDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var localId = _options.LocalNetworkId;
        if (!_started || localId == null)
            return;
        var snapshot = MeshDirectorySnapshotCodec.Encode(localId, Directory.Snapshot());
        await SendDatagramAsync(BroadcastNode, snapshot, contentKind: null, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---- datagram send path ----

    /// <summary>
    ///     Enqueue a datagram on the priority TX queues (REQ-TX-PRIO-1). A single pump drains
    ///     higher priorities first; each dequeued datagram still holds <see cref="_txWindow" />
    ///     for all of its radio frames so chunk trains are not interleaved.
    /// </summary>
    private Task SendDatagramAsync(uint node, ReadOnlyMemory<byte> payload,
        MeshtasticTxContentKind? contentKind, CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
            throw new ArgumentException("Payload is empty.", nameof(payload));

        var priority = MeshtasticTxClassifier.Classify(payload.Span, contentKind);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new MeshtasticTxWorkItem(node, payload.ToArray(), priority, done, cancellationToken);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state =>
            {
                var tcs = (TaskCompletionSource<bool>)state!;
                tcs.TrySetCanceled();
            }, done);
        }

        _txQueues.Enqueue(item);
        _logger?.LogTrace(
            "LoRa TX queued priority {Priority}-{PriorityName} frame 0x{Frame:X2} ({Bytes} bytes) → {Node:X8}",
            (int)priority, priority, payload.Span[0], payload.Length, node);
        EnsureTxPump();
        return done.Task;
    }

    private void EnsureTxPump()
    {
        if (Interlocked.CompareExchange(ref _txPumpRunning, 1, 0) != 0)
            return;
        _ = Task.Run(PumpTxQueuesAsync);
    }

    private async Task PumpTxQueuesAsync()
    {
        try
        {
            while (_txQueues.TryDequeue(out var item))
            {
                var first = item.Datagram.Length > 0 ? item.Datagram[0] : (byte)0;
                _logger?.LogTrace(
                    "LoRa TX dequeued priority {Priority}-{PriorityName} frame 0x{Frame:X2} ({Bytes} bytes) → {Node:X8}",
                    (int)item.Priority, item.Priority, first, item.Datagram.Length, item.Node);

                if (item.Completion.Task.IsCompleted)
                    continue;
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.CancellationToken);
                    continue;
                }

                // Half-duplex: defer non-retry TX while a peer chunk train is still arriving so we
                // can hear mid-train chunks / send 0x33. Re-enqueue keeps the pump free for Control.
                if (first != RetryRequestCodec.FrameByte &&
                    ShouldDeferTxForIncompleteRx(DateTime.UtcNow))
                {
                    var slot = _options.IncompleteRxListenSlot;
                    if (slot <= TimeSpan.Zero)
                        slot = TimeSpan.FromMilliseconds(500);
                    _logger?.LogDebug(
                        "Deferring TX 0x{Frame:X2} — incomplete RX fresh; listen {Slot:0.0}s",
                        first, slot.TotalSeconds);
                    try
                    {
                        await Task.Delay(slot, item.CancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce)
                    {
                        item.Completion.TrySetCanceled(oce.CancellationToken);
                        continue;
                    }

                    if (item.Completion.Task.IsCompleted)
                        continue;
                    _txQueues.Enqueue(item);
                    continue;
                }

                if (!_reassembler.HasFreshIncomplete(DateTime.UtcNow, _options.IncompleteRxFreshWindow))
                    _incompleteRxYieldDeadline = default;

                try
                {
                    await SendDatagramNowAsync(item.Node, item.Datagram, item.CancellationToken)
                        .ConfigureAwait(false);
                    item.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException oce)
                {
                    item.Completion.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _txPumpRunning, 0);
            // A concurrent Enqueue may have raced after the last TryDequeue returned false.
            if (!_txQueues.IsEmpty)
                EnsureTxPump();
        }
    }

    private bool ShouldDeferTxForIncompleteRx(DateTime now)
    {
        if (!_reassembler.HasFreshIncomplete(now, _options.IncompleteRxFreshWindow))
        {
            _incompleteRxYieldDeadline = default;
            return false;
        }

        if (_incompleteRxYieldDeadline == default)
        {
            _incompleteRxYieldDeadline = now + _options.IncompleteRxYieldMax;
            return true;
        }

        // Past the cap — send anyway so TX is not starved forever.
        return now < _incompleteRxYieldDeadline;
    }

    private async Task SendDatagramNowAsync(uint node, byte[] payload, CancellationToken cancellationToken)
    {
        await _txWindow.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _txInFlight, 1);
        try
        {
            var first = payload.Length > 0 ? payload[0] : (byte)0;
            if (payload.Length <= _options.MaxRadioPayloadBytes)
            {
                _logger?.LogTrace(
                    "LoRa TX frame 0x{Frame:X2} ({Bytes} bytes) → {Node:X8} (single radio frame): {Hex}",
                    first, payload.Length, node, Convert.ToHexString(payload));
                await SendFrameWithRetryAsync(node, payload, cancellationToken).ConfigureAwait(false);
                return;
            }

            var id = ChunkCodec.NewId();
            var crc = Crc32.Compute(payload);
            var chunkDataSize = _options.MaxRadioPayloadBytes - ChunkCodec.HeaderLength;
            var total = (ushort)((payload.Length + chunkDataSize - 1) / chunkDataSize);
            var frame = new byte[_options.MaxRadioPayloadBytes];

            _logger?.LogInformation(
                "LoRa TX chunk train start 0x{Frame:X2} ({Bytes} bytes) → {Node:X8} as {Total} radio chunks (id {Id})",
                first, payload.Length, node, total, id);
            _logger?.LogTrace(
                "LoRa TX frame 0x{Frame:X2} ({Bytes} bytes) → {Node:X8} as {Total} radio chunks (id {Id}): {Hex}",
                first, payload.Length, node, total, id, Convert.ToHexString(payload));

            // Hold the TX window for the whole train so lower-priority work cannot interleave
            // mid-datagram; between chunks release the window + listen when incomplete RX is fresh
            // so Control (0x33) can slip through and peer mid-train chunks can be heard.
            for (ushort number = 1; number <= total; number++)
            {
                var offset = (number - 1) * chunkDataSize;
                var take = Math.Min(chunkDataSize, payload.Length - offset);
                var written = ChunkCodec.WriteChunk(frame, id, number, total, crc, payload.AsSpan(offset, take));
                _logger?.LogTrace(
                    "LoRa TX chunk {Number}/{Total} ({Bytes} bytes) id {Id} → {Node:X8}: {Hex}",
                    number, total, written, id, node, Convert.ToHexString(frame.AsSpan(0, written)));
                await SendFrameWithRetryAsync(node, frame.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                if (number < total)
                    await ListenBetweenTrainChunksAsync(cancellationToken).ConfigureAwait(false);
            }

            var sentUtc = DateTime.UtcNow;
            _outbox[OutboxKey(node, id)] = new OutboxEntry(payload, node, sentUtc, sentUtc);
            _logger?.LogInformation(
                "LoRa TX chunk train done id {Id} ({Total} chunks, {Bytes} bytes) → {Node:X8}",
                id, total, payload.Length, node);
        }
        finally
        {
            Interlocked.Exchange(ref _txInFlight, 0);
            _txWindow.Release();
        }
    }

    /// <summary>
    ///     Between radio chunks of one datagram: brief listen window for peer RX. The TX window
    ///     stays held so another datagram cannot interleave; duty-cycle gate already spaces TX.
    /// </summary>
    private async Task ListenBetweenTrainChunksAsync(CancellationToken cancellationToken)
    {
        if (!_reassembler.HasFreshIncomplete(DateTime.UtcNow, _options.IncompleteRxFreshWindow))
            return;

        var slot = _options.IncompleteRxListenSlot;
        if (slot <= TimeSpan.Zero)
            return;

        _logger?.LogDebug(
            "Chunk-train listen slot {Slot:0.0}s for incomplete RX",
            slot.TotalSeconds);
        await Task.Delay(slot, cancellationToken).ConfigureAwait(false);
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
            catch (IOException ex) when (IsSerialClosed(ex))
            {
                _logger?.LogWarning(ex,
                    "TX to {Node:X8} hit closed/unavailable serial; entering reconnect loop", node);
                await EnsureSerialConnectedAsync(cancellationToken).ConfigureAwait(false);
                // Reconnected: retry the same frame without burning the ordinary send budget.
                if (attempt > 0)
                    attempt--;
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

    private static bool IsSerialClosed(IOException ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("Serial port is closed", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("serial link is not started", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("serial link is stopped", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("device", StringComparison.OrdinalIgnoreCase) &&
               (msg.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("not available", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("access denied", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Reconnect until the board answers or the transport/send is cancelled.
    ///     Backoff: 5 s for the first 10 failures, then logarithmic up to 30 s.
    /// </summary>
    private async Task EnsureSerialConnectedAsync(CancellationToken cancellationToken)
    {
        await _reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_started)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryReconnectSerialAsync(cancellationToken).ConfigureAwait(false))
                {
                    _serialReconnectFailures = 0;
                    return;
                }

                _serialReconnectFailures++;
                var delay = SerialReconnectPolicy.DelayAfterFailures(_serialReconnectFailures);
                _logger?.LogWarning(
                    "Serial device unavailable (reconnect attempt {Attempt}); next try in {Delay}",
                    _serialReconnectFailures, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            throw new IOException("Meshtastic transport stopped during serial reconnect.");
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    private async Task<bool> TryReconnectSerialAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _link.StopAsync(cancellationToken).ConfigureAwait(false);
            await _link.StartAsync(cancellationToken).ConfigureAwait(false);
            var node = _link.LocalNodeNumber;
            if (node == null)
            {
                _logger?.LogWarning("Serial reconnect opened the port but board did not report a node number");
                return false;
            }

            LocalMeshId = MeshId.FromNodeNumber(node.Value);
            _logger?.LogInformation(
                "Serial reconnected on node {Node} after {Failures} failed attempt(s)",
                LocalMeshId.ToHex(), _serialReconnectFailures);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Serial reconnect attempt failed");
            return false;
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
                _logger?.LogDebug(
                    "LoRa RX chunk {Number}/{Total} id {Id} from {From:X8} ({Bytes} bytes); waiting for remaining chunks",
                    chunk.Number, chunk.Total, chunk.Id, from, bytes.Length);
                _logger?.LogTrace(
                    "LoRa RX chunk {Number}/{Total} id {Id} from {From:X8}: {Hex}",
                    chunk.Number, chunk.Total, chunk.Id, from, Convert.ToHexString(bytes));
                MaybeRequestGapResend(from, chunk.Id);
                break;
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
        _logger?.LogTrace(
            "RX chat frame 0x{Frame:X2} ({Bytes} bytes) from {From}: {Hex}",
            frame, datagram.Length, from.ToHex(), Convert.ToHexString(datagram));
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

    /// <summary>
    ///     When mid-train holes are already visible (especially once the last chunk number arrived),
    ///     request a full resend without waiting for the stall sweep — the missing chunks will not
    ///     reappear spontaneously once the peer finished its train.
    /// </summary>
    private void MaybeRequestGapResend(uint fromNode, string compressedId)
    {
        var from = MeshId.FromNodeNumber(fromNode);
        if (!_reassembler.TryGetProgress(from, compressedId, out var progress))
            return;
        if (!progress.HasGaps || progress.Missing <= 0)
            return;

        // Only when the last chunk number is already here: the peer train is finished and the
        // missing mid-chunks will not arrive spontaneously. Mid-train holes wait for the
        // GapAssemblyTimeout sweep so we do not TX 0x33 over remaining peer chunks.
        if (!progress.HasLastChunk)
            return;

        var now = DateTime.UtcNow;
        // Avoid spamming 0x33 on every late chunk of the same broken assembly.
        var minGap = _options.Retry.GapAssemblyTimeout;
        if (minGap < TimeSpan.FromSeconds(5))
            minGap = TimeSpan.FromSeconds(5);
        if (progress.RetryCount > 0 && now - progress.LastRetryUtc < minGap)
            return;

        _reassembler.NoteRetry(from, compressedId, now);
        _logger?.LogInformation(
            "LoRa RX gaps on id {Id} from {From:X8} (have {Received}/{Total}, last={HasLast}); requesting resend",
            compressedId, fromNode, progress.Received, progress.Total, progress.HasLastChunk);
        RequestResend(fromNode, compressedId, RetryRequestCodec.ReasonChunkTimeout);
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

        // Stall path is paced by SweepLoop (RxDelay); CRC / inconsistent are one-shot per assembly.
        // Do not add a second Task.Delay here — that burned retry budget when callers re-entered early.
        _ = Task.Run(async () =>
        {
            var request = RetryRequestCodec.Encode(MeshId.FromNodeNumber(fromNode), compressedId, reason, (byte)count);
            try
            {
                await SendDatagramAsync(fromNode, request, contentKind: null, CancellationToken.None)
                    .ConfigureAwait(false);
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
            if (pair.Key.EndsWith($":{id}", StringComparison.Ordinal))
            {
                entry = pair.Value;
                break;
            }

        if (entry == null)
        {
            _logger?.LogWarning("Resend requested for unknown message {Id} (reason {Reason})", id, reason);
            return;
        }

        entry.LastActivityUtc = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try
            {
                await SendDatagramAsync(fromNode, entry.Datagram, contentKind: null, CancellationToken.None)
                    .ConfigureAwait(false);
                entry.LastActivityUtc = DateTime.UtcNow;
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
                // Control / short / medium text: duty-scaled RxTimeout (~141 s).
                // Large text / binary (est. ≥ 1000 B, TX priorities 4–6): LargePayloadTimeout (180 s).
                var retryCfg = _options.Retry;
                foreach (var stalled in _reassembler.GetStalled(now,
                             (size, missing, received, hasGaps) =>
                                 retryCfg.ChunkTimeoutForProgress(size, missing, received, hasGaps)))
                {
                    if (!stalled.From.TryToNodeNumber(out var fromNode))
                    {
                        _reassembler.Drop(stalled.From, stalled.Id);
                        _resendCounts.TryRemove(RetryKey(fromNode, stalled.Id), out _);
                        continue;
                    }

                    if (stalled.RetryCount >= retryCfg.RxMaxRetries)
                    {
                        _reassembler.Drop(stalled.From, stalled.Id);
                        _resendCounts.TryRemove(RetryKey(fromNode, stalled.Id), out _);
                        _logger?.LogWarning("Dropped stalled message {Id} from {From} after {Max} retries", stalled.Id,
                            stalled.From.ToHex(), retryCfg.RxMaxRetries);
                        continue;
                    }

                    // Pace stall resends with RxDelay(retry); do not burn budget on every 1s sweep tick.
                    if (stalled.RetryCount > 0 &&
                        now - stalled.LastRetryUtc < retryCfg.RxDelay(stalled.RetryCount - 1))
                        continue;

                    _reassembler.NoteRetry(stalled.From, stalled.Id, now);
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
        // Event-driven lifetime: drop after IdleGrace since last activity, with absolute Retention cap.
        foreach (var pair in _outbox)
        {
            var idle = now - pair.Value.LastActivityUtc > _options.OutboxIdleGrace;
            var expired = now - pair.Value.SentUtc > _options.OutboxRetention;
            if (idle || expired)
                _outbox.TryRemove(pair.Key, out _);
        }
        // Do not wipe _resendCounts here — that reset the RX retry budget every sweep second.
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
            try
            {
                // Yield to an active chunk train / queued chat TX so discovery does not split radio bursts.
                for (var i = 0; i < 50 && IsTxBusy; i++)
                    await Task.Delay(200).ConfigureAwait(false);
                if (IsTxBusy)
                {
                    _logger?.LogInformation("Deferring directory snapshot — TX busy (chunk train / queues)");
                    System.Threading.Interlocked.Exchange(ref _snapshotPending, 0);
                    await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    QueueDirectorySnapshot();
                    return;
                }

                await Task.Delay(200).ConfigureAwait(false); // coalesce bursts
                System.Threading.Interlocked.Exchange(ref _snapshotPending, 0);
                await SendDirectorySnapshotAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Exchange(ref _snapshotPending, 0);
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
            await SendDatagramAsync(BroadcastNode, selfSnapshot, contentKind: null, CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var payload = MeshDirectorySnapshotCodec.Encode(localId, Directory.Snapshot());
        foreach (var binding in remotes)
            if (new MeshId(binding.MeshId).TryToNodeNumber(out var node))
                await SendDatagramAsync(node, payload, contentKind: null, CancellationToken.None)
                    .ConfigureAwait(false);
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
        _reconnectGate.Dispose();
        _channel.Writer.TryComplete();
    }

    private sealed class OutboxEntry(byte[] datagram, uint destNode, DateTime sentUtc, DateTime lastActivityUtc)
    {
        public byte[] Datagram { get; } = datagram;
        public uint DestNode { get; } = destNode;
        public DateTime SentUtc { get; } = sentUtc;
        public DateTime LastActivityUtc { get; set; } = lastActivityUtc;
    }
}
