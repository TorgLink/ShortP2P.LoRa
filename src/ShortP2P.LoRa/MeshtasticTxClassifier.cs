using ShortP2P.LoRa.Chunking;
using ShortP2P.LoRa.Directory;
using ShortP2P.LoRa.Reliability;

namespace ShortP2P.LoRa;

/// <summary>
///     Classifies outbound Meshtastic datagrams into <see cref="MeshtasticTxPriority" /> (REQ-TX-PRIO-1).
///     <para>
///     Rules (size = full datagram length in bytes as passed to the transport):
///     <list type="number">
///       <item>
///         <description>
///           Control / signaling (priority 1): invite 0x30, handshake 0x01, session setup 0x04,
///           presence 0x31, directory snapshot 0x32 (discovery), retry request 0x33.
///           Chunk radio frames inherit the parent datagram's priority at enqueue time; if a lone
///           chunk is classified without a hint it is treated as control so mid-datagram parts
///           are not outranked by presence at the gate.
///         </description>
///       </item>
///       <item>
///         <description>Text length ≤ 200 → priority 2 (exactly 200 → 2).</description>
///       </item>
///       <item>
///         <description>Text length &gt; 200 and &lt; 1000 → priority 3.</description>
///       </item>
///       <item>
///         <description>Text length ≥ 1000 → priority 4 (exactly 1000 → 4).</description>
///       </item>
///       <item>
///         <description>Binary length ≤ 5 KiB (5120) → priority 5.</description>
///       </item>
///       <item>
///         <description>Binary length &gt; 5 KiB → priority 6.</description>
///       </item>
///     </list>
///     Application cipher 0x02 without an explicit <see cref="MeshtasticTxContentKind" /> is
///     sized as text (priorities 2–4). Pass <see cref="MeshtasticTxContentKind.Binary" /> for
///     image / file / transfer payloads.
///     </para>
/// </summary>
public static class MeshtasticTxClassifier
{
    /// <summary>Text short/medium boundary: lengths ≤ this are short text (priority 2).</summary>
    public const int TextShortMaxBytes = 200;

    /// <summary>Text medium/large boundary: lengths ≥ this are large text (priority 4).</summary>
    public const int TextLargeMinBytes = 1000;

    /// <summary>Binary small/large boundary: lengths ≤ this are small binary (priority 5).</summary>
    public const int BinarySmallMaxBytes = 5 * 1024;

    /// <summary>
    ///     Classify a datagram. Optional <paramref name="contentKind" /> applies to non-control
    ///     frames (typically 0x02 cipher); ignored for control/signaling frame bytes.
    /// </summary>
    public static MeshtasticTxPriority Classify(ReadOnlySpan<byte> datagram,
        MeshtasticTxContentKind? contentKind = null)
    {
        if (datagram.Length == 0)
            return MeshtasticTxPriority.LargeBinary;

        var first = datagram[0];
        if (IsControlFrameByte(first) || ChunkCodec.LooksLikeChunk(datagram))
            return MeshtasticTxPriority.Control;

        var kind = contentKind ?? DefaultContentKind(first);
        return ClassifySized(kind, datagram.Length);
    }

    /// <summary>Classify by known application class and payload size (no wire frame).</summary>
    public static MeshtasticTxPriority ClassifySized(MeshtasticTxContentKind contentKind, int sizeBytes)
    {
        if (sizeBytes < 0)
            sizeBytes = 0;

        if (contentKind == MeshtasticTxContentKind.Binary)
        {
            return sizeBytes <= BinarySmallMaxBytes
                ? MeshtasticTxPriority.SmallBinary
                : MeshtasticTxPriority.LargeBinary;
        }

        if (sizeBytes <= TextShortMaxBytes)
            return MeshtasticTxPriority.ShortText;
        if (sizeBytes < TextLargeMinBytes)
            return MeshtasticTxPriority.MediumText;
        return MeshtasticTxPriority.LargeText;
    }

    /// <summary>
    ///     ChatWire kind byte → content class for hosts that still see plaintext before encrypt.
    ///     Text / compressed text / vocab / user-info → Text; image / file / transfer → Binary.
    /// </summary>
    public static MeshtasticTxContentKind ContentKindFromChatWireKind(byte chatWireKind) =>
        chatWireKind switch
        {
            0x02 or 0x03 or 0x04 or 0x05 => MeshtasticTxContentKind.Binary, // image/file/transfer
            _ => MeshtasticTxContentKind.Text
        };

    /// <summary>True for invite / handshake / presence / directory / retry frame bytes.</summary>
    public static bool IsControlFrameByte(byte first) =>
        first is 0x30 or 0x01 or 0x04 or 0x31
            or MeshDirectorySnapshotCodec.FrameByte
            or RetryRequestCodec.FrameByte;

    private static MeshtasticTxContentKind DefaultContentKind(byte first) =>
        // Unknown opaque frames default to binary so large non-chat dumps stay at the bottom.
        first == 0x02 ? MeshtasticTxContentKind.Text : MeshtasticTxContentKind.Binary;
}
