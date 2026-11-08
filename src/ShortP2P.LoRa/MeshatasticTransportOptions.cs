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

    /// <summary>Selected jurisdiction. Default UNSET — TX is allowed at the 1% default duty cycle (REQ-RF-2).</summary>
    public JurisdictionProfile Jurisdiction { get; init; } = JurisdictionCatalog.Unset;

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
    ///     How long a sent datagram is kept for answering 0x33 resend requests.
    ///     Must cover RxTimeout × (RxMaxRetries+1) under duty-cycle deferral (default ~12 min).
    /// </summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromMinutes(15);

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
