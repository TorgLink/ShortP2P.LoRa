using System.Collections.Concurrent;
using System.IO.Ports;
using System.IO;
using Microsoft.Extensions.Logging;
using ShortP2P.LoRa.Link;
using ShortP2P.LoRa.Reliability;

namespace ShortP2P.LoRa;

/// <summary>
///     Strict process-wide singleton for ALL Meshtastic serial work (REQ-GATE-1).
///     It is the only component allowed to touch a COM port: every port byte is read and written on a
///     single dedicated radio thread. Callers from any thread marshal through the gate command queue;
///     device state is published only as immutable <see cref="MeshtasticLinkState" /> snapshots.
///     Port access from any other thread is forbidden and enforced by <see cref="MeshtasticSerialSession" />.
///     The gate also enforces the REQ-RF-2 duty-cycle budget (default TX 10% / RX 90% of estimated airtime
///     over a rolling hour): TX that does not fit the window is deferred, never silently dropped.
/// </summary>
public sealed class MeshtasticGate
{
    private const int RadioPollMs = 5;
    private const int StartTimeoutSeconds = 15;
    private const int RefreshTimeoutSeconds = 10;

    private static readonly MeshtasticGate InstanceField = new();

    /// <summary>The only gate instance in the process.</summary>
    public static MeshtasticGate Instance => InstanceField;

    /// <summary>REQ-RF-2 default product duty cycle: TX 10% (Ntx) / RX 90% (Nrx) of estimated airtime.</summary>
    public const double DefaultDutyCyclePercent = 10.0;

    /// <summary>ToRadio protobuf overhead on top of the payload, for the airtime estimate.</summary>
    private const int RadioOverheadBytes = 16;

    private readonly BlockingCollection<Action> _commands = new(new ConcurrentQueue<Action>());
    private readonly Thread _radioThread;

    // Radio-thread-owned state. Never touched from other threads.
    private MeshtasticSerialSession? _session;
    private object? _sessionToken;
    private TaskCompletionSource<bool>? _nodeNumberWait;
    private DutyCycleBudget _duty = new(DefaultDutyCyclePercent);
    private readonly List<DeferredSend> _deferred = [];

    private volatile MeshtasticLinkState _state = MeshtasticLinkState.Closed;

    /// <summary>Raw board frames with the session token they belong to (raised on the radio thread).</summary>
    internal event Action<object, MeshLinkFrame>? FrameReceived;

    private MeshtasticGate()
    {
        _radioThread = new Thread(RadioLoop)
        {
            IsBackground = true,
            Name = "MeshtasticGate radio"
        };
        _radioThread.Start();
    }

    /// <summary>Create a (not yet started) serial link handled by this gate.</summary>
    public MeshtasticGateLink CreateLink(string portName, int baudRate, ILogger? logger = null) =>
        new(this, portName, baudRate, logger);

    /// <summary>
    ///     Enumerate COM port names on the radio thread (no device I/O, but kept on the gate thread so
    ///     the SerialPort stack is used from exactly one thread). Empty list on failure.
    /// </summary>
    public IReadOnlyList<string> ListPorts()
    {
        try
        {
            var task = EnqueueRadio(() => SerialPort.GetPortNames());
            return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result : [];
        }
        catch
        {
            // Serial stack missing (e.g. Android) or the radio thread did not answer in time.
            return [];
        }
    }

    /// <summary>
    ///     REQ-RF-2 policy applied on the radio thread: TX ≤ <paramref name="dutyCyclePercent" /> of
    ///     estimated on-air time over a rolling one-hour window (Meshtastic EU semantics), remainder RX.
    ///     The host clamps the default 10% to the selected region cap and applies this before starting
    ///     the transport. Replacing the budget resets the window accounting.
    /// </summary>
    public Task ConfigureDutyCycleAsync(double dutyCyclePercent, double airtimePerByteMs,
        CancellationToken cancellationToken = default)
    {
        var task = EnqueueRadio<object?>(() =>
        {
            _duty = new DutyCycleBudget(dutyCyclePercent, airtimePerByteMs: airtimePerByteMs);
            return null;
        });
        return task.WaitAsync(cancellationToken);
    }

    // ---- link operations (all marshal to the radio thread) ----

    internal object CreateToken() => new();

    internal MeshtasticLinkState StateFor(object token) =>
        ReferenceEquals(_sessionToken, token) ? _state : MeshtasticLinkState.Closed;

    internal async Task OpenSessionAsync(object token, string portName, int baudRate, ILogger? logger,
        CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodeAnnounced = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _commands.Add(() =>
        {
            try
            {
                CloseSessionCore("replaced by a new open");
                var session = new MeshtasticSerialSession(portName, baudRate,
                    Thread.CurrentThread, logger);
                session.NodeNumberAnnounced += () => nodeAnnounced.TrySetResult(true);
                session.Received += frame => FrameReceived?.Invoke(token, frame);
                session.OnAirtimeWritten += airtimeBytes => _duty.RecordTx(airtimeBytes, DateTime.UtcNow);
                session.Open();
                _session = session;
                _sessionToken = token;
                _nodeNumberWait = nodeAnnounced;
                PublishCurrentState();
                opened.TrySetResult(true);
            }
            catch (Exception ex)
            {
                opened.TrySetException(ex);
            }
        });

        await opened.Task.ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(StartTimeoutSeconds));
        try
        {
            await nodeAnnounced.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CloseSessionAsync(token, CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new IOException("LoRa board did not report its node number after connect.");
        }
    }

    internal Task CloseSessionAsync(object token, CancellationToken cancellationToken)
    {
        var task = EnqueueRadio<object?>(() =>
        {
            if (ReferenceEquals(_sessionToken, token))
                CloseSessionCore("stop");
            return null;
        });
        return task.WaitAsync(cancellationToken);
    }

    internal Task SendAsync(object token, uint to, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        SendAsync(token, to, payload, paceExempt: false, cancellationToken);

    internal Task SendAsync(object token, uint to, ReadOnlyMemory<byte> payload, bool paceExempt,
        CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
            throw new ArgumentException("Payload is empty.", nameof(payload));

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frame = payload.ToArray();
        _commands.Add(() => TryDeliver(token, to, frame, done, paceExempt));
        return done.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Apply Config.LoRaConfig.tx_power on the open session (REQ-RF-8).</summary>
    internal Task ApplyTxPowerAsync(object token, int txPowerDbm, CancellationToken cancellationToken) =>
        ApplyLoraConfigAsync(
            token,
            "TX power",
            (session, done) => session.BeginApplyTxPower(txPowerDbm, done),
            cancellationToken);

    /// <summary>
    ///     Apply Config.LoRaConfig.region on the open session: GET the full blob, patch region, SET the full blob.
    ///     A zero (UNSET) code is not written.
    /// </summary>
    internal Task ApplyRegionCodeAsync(object token, uint regionCode, CancellationToken cancellationToken)
    {
        if (regionCode == 0)
            return Task.CompletedTask;
        return ApplyLoraConfigAsync(
            token,
            "region",
            (session, done) => session.BeginApplyRegion(regionCode, done),
            cancellationToken);
    }

    private async Task ApplyLoraConfigAsync(
        object token,
        string purpose,
        Action<MeshtasticSerialSession, TaskCompletionSource<bool>> begin,
        CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            try
            {
                var session = ReferenceEquals(_sessionToken, token) ? _session : null;
                if (session == null)
                {
                    done.TrySetResult(false);
                    return;
                }

                begin(session, done);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await done.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Never write a partial LoRaConfig on timeout — that wiped region/channel before.
            _session?.Logger?.LogWarning(
                "{Purpose} apply timed out waiting for get_config_response; board LoRaConfig left unchanged",
                purpose);
            done.TrySetResult(false);
        }
    }

    /// <summary>
    ///     How long TX would wait before a payload of this size fits the duty-cycle budget.
    ///     Used by the host to skip presence keepalives when chat/handshake needs the airtime.
    ///     Adds one radio-overhead estimate (single frame).
    /// </summary>
    public Task<TimeSpan> PeekDutyWaitAsync(int payloadBytes, CancellationToken cancellationToken = default)
    {
        var task = EnqueueRadio(() =>
            _duty.WaitTimeFor(Math.Max(0, payloadBytes) + RadioOverheadBytes, DateTime.UtcNow));
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    ///     How long TX would wait before <paramref name="estimatedAirtimeBytes"/> of estimated
    ///     on-air payload (caller already included per-frame overhead) fits even pacing + hard cap.
    ///     Used to pre-earn budget for a whole chunk train before bursting it.
    /// </summary>
    public Task<TimeSpan> PeekDutyWaitExactAsync(int estimatedAirtimeBytes,
        CancellationToken cancellationToken = default)
    {
        var task = EnqueueRadio(() =>
            _duty.WaitTimeFor(Math.Max(0, estimatedAirtimeBytes), DateTime.UtcNow, enforcePace: true));
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>ToRadio protobuf overhead added per radio frame in duty estimates.</summary>
    internal static int DutyRadioOverheadBytes => RadioOverheadBytes;

    /// <summary>
    ///     Radio thread only. Sends immediately while the duty-cycle budget allows it; otherwise the
    ///     frame is parked in <see cref="_deferred" /> and fires from the loop when the window slides.
    ///     The radio thread never blocks waiting for airtime.
    ///     Deferred frames are ordered by <see cref="MeshtasticTxClassifier" /> (REQ-TX-PRIO-1):
    ///     control before text/binary. Presence that cannot go now is parked
    ///     (<see cref="MeshtasticPresenceQueue"/>), not completed as a drop, and does not cut in
    ///     front of non-presence work already in <see cref="_deferred"/>.
    /// </summary>
    private void TryDeliver(object token, uint to, byte[] payload, TaskCompletionSource<bool> done,
        bool paceExempt = false)
    {
        try
        {
            var session = ReferenceEquals(_sessionToken, token) ? _session : null;
            if (session == null)
            {
                done.TrySetException(new IOException("Meshtastic serial link is not started."));
                return;
            }

            var isPresence = IsPresencePayload(payload);
            var wait = _duty.WaitTimeFor(payload.Length + RadioOverheadBytes, DateTime.UtcNow,
                enforcePace: !paceExempt);
            if (isPresence)
            {
                var nonPresenceWaiting = _deferred.Exists(d => !IsPresencePayload(d.Payload));
                if (wait > TimeSpan.Zero || nonPresenceWaiting)
                {
                    session.Logger?.LogInformation(
                        "Deferring presence TX to {To:X8} — duty {Spent:F1}/{Budget:F1} s, wait {Seconds:F1} s, non-presence queued {Queued}",
                        to, _duty.SpentSeconds, _duty.BudgetSeconds, wait.TotalSeconds, nonPresenceWaiting);
                    DeferPresence(token, to, payload, done, paceExempt, wait);
                    return;
                }
            }
            else if (wait > TimeSpan.Zero)
            {
                session.Logger?.LogInformation(
                    "Duty cycle pace/limit ({Spent:F1}/{Budget:F1} s in the rolling hour); deferring TX of {Bytes} bytes for {Seconds:F1} s",
                    _duty.SpentSeconds, _duty.BudgetSeconds, payload.Length, wait.TotalSeconds);
                _deferred.Add(new DeferredSend(DateTime.UtcNow + wait, token, to, payload, done, paceExempt));
                return;
            }

            // Budget allows this frame. If higher-priority work is already deferred, park behind it
            // so PumpDeferred can drain by MeshtasticTxPriority (control before text/binary).
            // Mid-train pace-exempt chunks stay ahead of other deferred work so the train finishes.
            var incomingPriority = MeshtasticTxClassifier.Classify(payload);
            if (!paceExempt &&
                _deferred.Exists(d => MeshtasticTxClassifier.Classify(d.Payload) < incomingPriority))
            {
                session.Logger?.LogInformation(
                    "Deferring TX priority {Priority}-{PriorityName} behind higher-priority pending radio frame(s)",
                    (int)incomingPriority, incomingPriority);
                _deferred.Add(new DeferredSend(DateTime.UtcNow, token, to, payload, done, paceExempt));
                return;
            }

            if (isPresence)
                CompleteQueuedPresence(to);

            session.Send(to, payload);
            done.TrySetResult(true);
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    /// <summary>Radio thread only: re-check deferred frames whose airtime slot has arrived.</summary>
    private void PumpDeferred()
    {
        if (_deferred.Count == 0)
            return;

        List<DeferredSend>? due = null;
        var now = DateTime.UtcNow;
        for (var i = _deferred.Count - 1; i >= 0; i--)
        {
            if (now >= _deferred[i].DueUtc)
            {
                (due ??= []).Add(_deferred[i]);
                _deferred.RemoveAt(i);
            }
        }

        if (due == null)
            return;

        // Non-presence that is already due goes first so a parked presence cannot cut in front of it.
        // Among the rest, REQ-TX-PRIO-1: control before text/binary.
        due.Sort(static (a, b) =>
        {
            var aPresence = IsPresencePayload(a.Payload);
            var bPresence = IsPresencePayload(b.Payload);
            if (aPresence != bPresence)
                return aPresence ? 1 : -1;
            return MeshtasticTxClassifier.Classify(a.Payload)
                .CompareTo(MeshtasticTxClassifier.Classify(b.Payload));
        });
        foreach (var d in due)
            TryDeliver(d.Token, d.To, d.Payload, d.Done, d.PaceExempt); // may re-defer with a fresh due time
    }

    /// <summary>
    ///     Radio thread only. Keep a single pending presence per destination, behind any
    ///     non-presence frame already deferred. Does not complete <paramref name="done"/> —
    ///     the frame stays queued until a later <see cref="TryDeliver"/> actually sends it.
    ///     An older presence to the same destination is completed so its waiter does not hang;
    ///     the newer frame remains pending.
    /// </summary>
    private void DeferPresence(object token, uint to, byte[] payload, TaskCompletionSource<bool> done,
        bool paceExempt, TimeSpan dutyWait)
    {
        var presenceSends = new List<DeferredSend>();
        var rest = new List<DeferredSend>();
        var nonPresenceDue = new List<DateTime>();
        foreach (var d in _deferred)
        {
            if (IsPresencePayload(d.Payload))
                presenceSends.Add(d);
            else
            {
                rest.Add(d);
                nonPresenceDue.Add(d.DueUtc);
            }
        }

        var slots = new List<MeshtasticPresenceQueue.Slot>(presenceSends.Count);
        foreach (var d in presenceSends)
            slots.Add(new MeshtasticPresenceQueue.Slot(d.DueUtc, d.To, d.Payload));

        var parked = MeshtasticPresenceQueue.Park(
            slots, to, payload, dutyWait, DateTime.UtcNow, nonPresenceDue);

        _deferred.Clear();
        _deferred.AddRange(rest);
        foreach (var slot in parked)
        {
            if (slot.To == to)
            {
                _deferred.Add(new DeferredSend(slot.DueUtc, token, to, slot.Payload, done, paceExempt));
                continue;
            }

            var previous = presenceSends.Find(d => d.To == slot.To);
            if (previous != null)
                _deferred.Add(previous with { DueUtc = slot.DueUtc, Payload = slot.Payload });
        }

        foreach (var previous in presenceSends)
        {
            if (previous.To == to)
                previous.Done.TrySetResult(true);
        }
    }

    /// <summary>Drop an older queued presence to <paramref name="to"/> because a newer one is being sent.</summary>
    private void CompleteQueuedPresence(uint to)
    {
        for (var i = _deferred.Count - 1; i >= 0; i--)
        {
            if (!IsPresencePayload(_deferred[i].Payload) || _deferred[i].To != to)
                continue;
            _deferred[i].Done.TrySetResult(true);
            _deferred.RemoveAt(i);
        }
    }

    private static bool IsPresencePayload(byte[] payload) =>
        payload.Length > 0 && payload[0] == 0x31;

    internal async Task RefreshAsync(object token, CancellationToken cancellationToken)
    {
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            if (!ReferenceEquals(_sessionToken, token) || _session == null)
            {
                refresh.TrySetResult(false); // no active link: same no-op as the old serial link
                return;
            }

            try
            {
                _session.BeginRefresh(refresh);
                PublishCurrentState();
            }
            catch (Exception ex)
            {
                refresh.TrySetException(ex);
            }
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RefreshTimeoutSeconds));
        try
        {
            await refresh.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Partial node list is still usable when the board never sends config_complete_id.
        }
    }

    // ---- radio thread ----

    private void RadioLoop()
    {
        while (true)
        {
            try
            {
                if (_commands.TryTake(out var command, RadioPollMs))
                    command();
                PumpDeferred();
                if (_session is { } session && session.Pump())
                    PublishCurrentState();
            }
            catch
            {
                // The radio thread must never die; individual commands report their own errors.
            }
        }
    }

    private Task<T> EnqueueRadio<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private void CloseSessionCore(string reason)
    {
        var session = _session;
        if (session == null)
            return;

        // Fail pending radio work cleanly before closing the port so callers do not see a raw
        // "Serial port is closed" mid-burst without a stop/replace reason.
        foreach (var d in _deferred)
            d.Done.TrySetException(new IOException($"Meshtastic serial link is stopped ({reason})."));
        _deferred.Clear();

        _session = null;
        _sessionToken = null;
        _nodeNumberWait?.TrySetCanceled();
        _nodeNumberWait = null;
        try
        {
            session.Close();
        }
        catch (Exception ex)
        {
            session.Logger?.LogWarning(ex, "Meshtastic serial session close failed ({Reason})", reason);
        }

        PublishCurrentState();
    }

    private void PublishCurrentState()
    {
        var session = _session;
        _state = session == null
            ? MeshtasticLinkState.Closed
            : new MeshtasticLinkState(true, session.LocalNodeNumber, session.SnapshotNodes());
    }

    /// <summary>A data frame parked until the duty-cycle window allows its airtime (radio thread only).</summary>
    private sealed record DeferredSend(
        DateTime DueUtc,
        object Token,
        uint To,
        byte[] Payload,
        TaskCompletionSource<bool> Done,
        bool PaceExempt = false);
}
