using Microsoft.Extensions.Logging;
using ShortP2P.LoRa.Link;

namespace ShortP2P.LoRa;

/// <summary>
///     Serial link to the Meshtastic board via the strict <see cref="MeshtasticGate" /> singleton:
///     every operation is marshalled to the single radio thread that owns the COM port, and device
///     state is read from immutable snapshots. No port I/O ever happens on the caller thread.
/// </summary>
public sealed class MeshtasticGateLink : IMeshLink, IMeshNodeCatalog, IMeshRadioConfig
{
    private readonly MeshtasticGate _gate;
    private readonly object _token;
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly ILogger? _logger;

    public event Action<MeshLinkFrame>? Received;

    internal MeshtasticGateLink(MeshtasticGate gate, string portName, int baudRate, ILogger? logger)
    {
        _gate = gate;
        _portName = string.IsNullOrWhiteSpace(portName)
            ? throw new ArgumentNullException(nameof(portName))
            : portName;
        _baudRate = baudRate;
        _logger = logger;
        _token = gate.CreateToken();
        _gate.FrameReceived += OnGateFrame;
    }

    /// <summary>Board-reported local node number (immutable snapshot; null while unknown or stopped).</summary>
    public uint? LocalNodeNumber => _gate.StateFor(_token).LocalNodeNumber;

    /// <summary>Snapshot of the board node database as of the last gate update.</summary>
    public IReadOnlyList<MeshtasticRadioNode> Nodes => _gate.StateFor(_token).Nodes;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.OpenSessionAsync(_token, _portName, _baudRate, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask(_gate.CloseSessionAsync(_token, cancellationToken));
    }

    public ValueTask SendAsync(uint to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        SendAsync(to, payload, paceExempt: false, cancellationToken);

    /// <inheritdoc />
    public ValueTask SendAsync(uint to, ReadOnlyMemory<byte> payload, bool paceExempt,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length == 0)
            throw new ArgumentException("Payload is empty.", nameof(payload));
        return new ValueTask(_gate.SendAsync(_token, to, payload, paceExempt, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask WaitForDutyBudgetAsync(int estimatedAirtimeBytes,
        CancellationToken cancellationToken = default)
    {
        if (estimatedAirtimeBytes <= 0)
            return;

        while (true)
        {
            var wait = await _gate.PeekDutyWaitExactAsync(estimatedAirtimeBytes, cancellationToken)
                .ConfigureAwait(false);
            if (wait <= TimeSpan.Zero)
                return;

            _logger?.LogInformation(
                "Duty cycle even pace: waiting {Seconds:F1}s to earn TX budget for {Bytes} airtime-bytes",
                wait.TotalSeconds, estimatedAirtimeBytes);
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ask the board to resend its node database; the refreshed snapshot lands in <see cref="Nodes" />.</summary>
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask(_gate.RefreshAsync(_token, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask ApplyTxPowerDbmAsync(int txPowerDbm, CancellationToken cancellationToken = default)
    {
        return new ValueTask(_gate.ApplyTxPowerAsync(_token, txPowerDbm, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask ApplyModemPresetAsync(int modemPreset, CancellationToken cancellationToken = default)
    {
        return new ValueTask(_gate.ApplyModemPresetAsync(_token, modemPreset, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<LoRaBoardConfigInfo> ReadLoRaConfigAsync(CancellationToken cancellationToken = default)
    {
        return new ValueTask<LoRaBoardConfigInfo>(
            _gate.ReadLoRaConfigAsync(_token, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask ApplyRawLoRaConfigAsync(ReadOnlyMemory<byte> loraConfig,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask(_gate.ApplyRawLoRaConfigAsync(_token, loraConfig, cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        _gate.FrameReceived -= OnGateFrame;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort: the session may already be closed/replaced
        }
    }

    private void OnGateFrame(object token, MeshLinkFrame frame)
    {
        if (ReferenceEquals(token, _token))
            Received?.Invoke(frame);
    }
}
