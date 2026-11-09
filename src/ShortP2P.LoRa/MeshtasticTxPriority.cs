namespace ShortP2P.LoRa;

/// <summary>
///     Outbound Meshtastic TX priority (REQ-TX-PRIO-1). Lower numeric value = drained first.
///     Each priority has its own FIFO queue; the scheduler always takes from the highest
///     non-empty queue (1 → 6). Strict priority: continuous high-priority traffic can starve
///     lower queues (no anti-starvation round-robin in this revision).
/// </summary>
public enum MeshtasticTxPriority : byte
{
    /// <summary>Invite / presence / discovery / handshake / directory / retry control.</summary>
    Control = 1,

    /// <summary>Text (or default cipher) payload length ≤ 200 bytes.</summary>
    ShortText = 2,

    /// <summary>Text payload length in (200, 1000) bytes.</summary>
    MediumText = 3,

    /// <summary>Text payload length ≥ 1000 bytes.</summary>
    LargeText = 4,

    /// <summary>Binary payload length ≤ 5 KiB (5120 bytes).</summary>
    SmallBinary = 5,

    /// <summary>Binary payload length &gt; 5 KiB.</summary>
    LargeBinary = 6
}
