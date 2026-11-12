using ShortP2P.LoRa.Directory;
using ShortP2P.LoRa.Jurisdiction;
using ShortP2P.LoRa.Reliability;

namespace ShortP2P.LoRa;

/// <summary>
///     Configuration of the Meshatastic transport: the LoRa board serial link,
///     the selected jurisdiction profile, and the retry settings (REQ-CFG-2, REQ-CFG-3).
///     Board name/model is deliberately not configured (REQ-CFG-2) — the board reports it after connect.
/// </summary>
public sealed record MeshatasticTransportOptions
{
    /// <summary>COM port of the LoRa board, e.g. "COM3" or "/dev/ttyUSB0". Required.</summary>
    public required string ComPort { get; init; }

    public int BaudRate { get; init; } = 115200;

    /// <summary>Selected jurisdiction. Default UNSET — TX is allowed at the 10% default duty cycle (REQ-RF-2).</summary>
    public JurisdictionProfile Jurisdiction { get; init; } = JurisdictionCatalog.Unset;

    /// <summary>
    ///     Desired TX power in dBm for the board (REQ-RF-8). UI range is 5…22; the host clamps to the
    ///     region cap before apply. Default 22.
    /// </summary>
    public int TxPowerDbm { get; init; } = LoRaTxPower.MaxDbm;

    /// <summary>
    ///     When true, <see cref="MeshatasticTransport.StartAsync"/> pushes TxPowerDbm to the board
    ///     (GET full LoRaConfig → patch → SET). Default false: a failed/partial apply previously
    ///     wiped region/channel and silenced mesh RX; host settings UI can still call apply explicitly.
    /// </summary>
    public bool ApplyTxPowerOnStart { get; init; }

    /// <summary>
    ///     Modem preset written on start when <see cref="UseBoardPresets"/> is false. Default LONG_FAST.
    ///     Both peers must use the same preset or they will not hear each other.
    ///     Applied by GET of the full LoRaConfig, then use_preset=true and modem_preset;
    ///     region, channel, tx_power and every other field stay as the board reported them.
    /// </summary>
    public LoRaModemPresetInfo ModemPreset { get; init; } = LoRaModemPresets.Default;

    /// <summary>
    ///     When true, start does not write modem/region/power to the board; the host reads the
    ///     board LoRaConfig and follows its region for the software duty-cycle budget.
    /// </summary>
    public bool UseBoardPresets { get; init; }

    public RetryConfig Retry { get; init; } = new();

    /// <summary>Local ShortP2P NetworkId, 12-byte wire form. Set before start.</summary>
    public byte[]? LocalNetworkId { get; init; }

    /// <summary>Host-registered lookup: which NetworkIds are messenger servers.</summary>
    public Func<byte[], bool>? IsServerNetworkId { get; init; }

    /// <summary>Directory persistence. Default: JSON file under %LocalAppData%.</summary>
    public IMeshDirectoryStore? DirectoryStore { get; init; }

    /// <summary>Max bytes of one Meshtastic data payload this transport emits.
    ///     SMT-1 target: 80 bytes per radio frame (ShortP2P fragmentation handles larger datagrams);
    ///     also safely below the ~222-byte cap most Meshtastic presets enforce (LONG_FAST allows 233).
    /// </summary>
    public int MaxRadioPayloadBytes { get; init; } = 80;

    /// <summary>How often stalled reassemblies are checked. Default 1 s.</summary>
    public TimeSpan RxSweepInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Always pause at least this long between radio chunks of one datagram so the peer can
    ///     hear / ACK mid-train (half-duplex). Default 250 ms.
    /// </summary>
    public TimeSpan ChunkTrainInterChunkGap { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     While an incomplete RX assembly is fresh, pause between TX radio frames for this long
    ///     so half-duplex can hear the peer's remaining chunks. Default 3 s.
    /// </summary>
    public TimeSpan IncompleteRxListenSlot { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     Cap on how long TX yields for incomplete RX before continuing (avoids starvation).
    ///     Default 25 s.
    /// </summary>
    public TimeSpan IncompleteRxYieldMax { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    ///     An incomplete RX assembly is treated as fresh (TX should yield) if a chunk arrived
    ///     within this window. Default 22 s (covers a typical duty-paced peer inter-chunk gap).
    /// </summary>
    public TimeSpan IncompleteRxFreshWindow { get; init; } = TimeSpan.FromSeconds(22);

    /// <summary>
    ///     Absolute safety-net TTL from first send for answering 0x33 resend requests.
    ///     Primary lifetime is event-driven via <see cref="OutboxIdleGrace"/> (activity on serve).
    /// </summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromMinutes(8);

    /// <summary>
    ///     Drop an outbox entry after this much silence since last send or successful 0x33 serve.
    ///     Keeps retention short while still covering an active resend exchange.
    /// </summary>
    public TimeSpan OutboxIdleGrace { get; init; } = TimeSpan.FromMinutes(3);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ComPort))
            throw new InvalidOperationException("LoRa board COM port is required (e.g. COM3).");
        if (MaxRadioPayloadBytes <= Chunking.ChunkCodec.HeaderLength + 1)
            throw new InvalidOperationException("MaxRadioPayloadBytes is too small for chunking.");
        if (LocalNetworkId is { Length: not MeshBinding.NetworkIdLength })
            throw new InvalidOperationException("LocalNetworkId must be 12 bytes (wire form).");
    }
}
