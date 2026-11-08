using System.Collections.Concurrent;
using System.IO.Ports;
using System.IO;
using Microsoft.Extensions.Logging;
using ShortP2P.LoRa.Link;

namespace ShortP2P.LoRa;

/// <summary>
///     Strict process-wide singleton for ALL Meshtastic serial work (REQ-GATE-1).
///     It is the only component allowed to touch a COM port: every port byte is read and written on a
///     single dedicated radio thread. Callers from any thread marshal through the gate command queue;
///     device state is published only as immutable <see cref="MeshtasticLinkState" /> snapshots.
///     Port access from any other thread is forbidden and enforced by <see cref="MeshtasticSerialSession" />.
/// </summary>
public sealed class MeshtasticGate
{
    private const int RadioPollMs = 5;
    private const int StartTimeoutSeconds = 15;
    private const int RefreshTimeoutSeconds = 10;

    private static readonly MeshtasticGate InstanceField = new();

    /// <summary>The only gate instance in the process.</summary>
    public static MeshtasticGate Instance => InstanceField;

    private readonly BlockingCollection<Action> _commands = new(new ConcurrentQueue<Action>());
    private readonly Thread _radioThread;

    // Radio-thread-owned state. Never touched from other threads.
    private MeshtasticSerialSession? _session;
    private object? _sessionToken;
    private TaskCompletionSource<bool>? _nodeNumberWait;

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

    internal Task SendAsync(object token, uint to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var task = EnqueueRadio<object?>(() =>
        {
            ActiveSession(token).Send(to, payload);
            return null;
        });
        return task.WaitAsync(cancellationToken);
    }

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

    private MeshtasticSerialSession ActiveSession(object token) =>
        ReferenceEquals(_sessionToken, token)
            ? _session ?? throw new IOException("Meshtastic serial link is not started.")
            : throw new IOException("Meshtastic serial link is not started.");

    private void CloseSessionCore(string reason)
    {
        var session = _session;
        if (session == null)
            return;

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
}
