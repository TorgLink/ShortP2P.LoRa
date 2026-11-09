namespace ShortP2P.LoRa;

/// <summary>
///     Application payload class for TX priority when the wire frame is cipher (0x02) or opaque
///     data that is not a control/signaling frame. ChatWire text / compressed text →
///     <see cref="Text" />; image / file / transfer → <see cref="Binary" />.
///     Cipher frames are encrypted, so the host/client should pass this hint when known;
///     otherwise <see cref="MeshtasticTxClassifier" /> defaults 0x02 to <see cref="Text" /> sized
///     by the full datagram length.
/// </summary>
public enum MeshtasticTxContentKind : byte
{
    Text = 0,
    Binary = 1
}
