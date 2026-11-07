using System.Collections.Concurrent;
using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace ShortP2P.LoRa.Link;

/// <summary>
///     Meshtastic board over a serial COM port (REQ-TR-1, REQ-CFG-2).
///     Board model/name is not configured: after the port opens, the board reports
///     its node number and firmware identity itself.
/// </summary>
public sealed class MeshtasticSerialLink : IMeshLink, IMeshNodeCatalog
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly ConcurrentDictionary<uint, MeshtasticRadioNode> _nodes = new();

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private long _packetId = Random.Shared.Next(1, int.MaxValue);
    private int _configWaitId;
    private TaskCompletionSource<bool>? _configWait;

    public MeshtasticSerialLink(string portName, int baudRate = 115200, ILogger? logger = null)
    {
        _portName = string.IsNullOrWhiteSpace(portName)
            ? throw new ArgumentNullException(nameof(portName))
            : portName;
        _baudRate = baudRate;
        _logger = logger;
    }

    public uint? LocalNodeNumber { get; private set; }

    public IReadOnlyList<MeshtasticRadioNode> Nodes => _nodes.Values.ToArray();

    public event Action<MeshLinkFrame>? Received;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_port != null)
            return;

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true
        };
        port.Open();
        _port = port;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readTask = Task.Run(() => ReadLoopAsync(port.BaseStream, _cts.Token));

        // Ask the board for its config so it reports my_info (and therefore the node number).
        var want = MeshtasticProto.Frame(MeshtasticProto.BuildToRadioWantConfig(1));
        await WriteRawAsync(want, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation("Meshtastic serial link opened on {Port} at {Baud}", _portName, _baudRate);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (LocalNodeNumber == null && DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        if (_readTask != null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on stop
            }

            _readTask = null;
        }

        if (_port != null)
        {
            if (_port.IsOpen)
                _port.Close();
            _port.Dispose();
            _port = null;
        }

        _logger?.LogInformation("Meshtastic serial link on {Port} stopped", _portName);
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_port is not { IsOpen: true })
            return;

        _nodes.Clear();
        var id = Random.Shared.Next(2, int.MaxValue);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _configWait = tcs;
        Volatile.Write(ref _configWaitId, id);

        var want = MeshtasticProto.Frame(MeshtasticProto.BuildToRadioWantConfig((uint)id));
        await WriteRawAsync(want, cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Partial node list is still usable when the board never sends config_complete_id.
        }
    }

    public async ValueTask SendAsync(uint to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (_port is not { IsOpen: true })
            throw new InvalidOperationException("Meshtastic serial link is not started.");

        var framed = MeshtasticProto.Frame(
            MeshtasticProto.BuildToRadioPacket(to, NextPacketId(), payload.ToArray(), hopLimit: 3));
        await WriteRawAsync(framed, cancellationToken).ConfigureAwait(false);
    }

    private uint NextPacketId()
    {
        return (uint)(System.Threading.Interlocked.Increment(ref _packetId) & int.MaxValue);
    }

    private async ValueTask WriteRawAsync(byte[] framed, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_port is not { IsOpen: true })
                throw new IOException("Serial port is closed.");
            await _port.BaseStream.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var pending = new List<byte>(MeshtasticProto.MaxPacketBytes * 2);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (read <= 0)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                for (var i = 0; i < read; i++)
                    pending.Add(buffer[i]);

                DrainFrames(pending);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void DrainFrames(List<byte> pending)
    {
        while (true)
        {
            var start = -1;
            for (var i = 0; i + 1 < pending.Count; i++)
                if (pending[i] == MeshtasticProto.Start1 && pending[i + 1] == MeshtasticProto.Start2)
                {
                    start = i;
                    break;
                }

            if (start < 0)
            {
                pending.Clear();
                return;
            }

            if (start > 0)
                pending.RemoveRange(0, start);

            if (pending.Count < 4)
                return;

            var length = (pending[2] << 8) | pending[3];
            if (length > MeshtasticProto.MaxPacketBytes)
            {
                pending.RemoveRange(0, 2); // resync
                continue;
            }

            if (pending.Count < 4 + length)
                return;

            var payload = pending.GetRange(4, length).ToArray();
            pending.RemoveRange(0, 4 + length);
            DispatchFromRadio(payload);
        }
    }

    private void DispatchFromRadio(byte[] payload)
    {
        try
        {
            MeshtasticProto.ReadFromRadio(
                payload,
                onPacket: frame =>
                {
                    _logger?.LogDebug("RX from {From:X8} to {To:X8}, {Len} bytes", frame.From, frame.To,
                        frame.Payload.Length);
                    Received?.Invoke(frame);
                },
                onMyNodeNum: node =>
                {
                    LocalNodeNumber = node;
                    _logger?.LogInformation("Board reported local node number {Node:X8}", node);
                },
                onNode: node =>
                {
                    _nodes[node.NodeNumber] = node;
                    _logger?.LogInformation("Meshtastic node {Id} {Name}", node.NodeId, node.DisplayName);
                },
                onConfigComplete: id =>
                {
                    var waitId = Volatile.Read(ref _configWaitId);
                    if (waitId != 0 && id == (uint)waitId)
                        _configWait?.TrySetResult(true);
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse a FromRadio payload ({Len} bytes)", payload.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
