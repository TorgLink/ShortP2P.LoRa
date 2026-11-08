using ShortP2P.LoRa.Link;

namespace ShortP2P.LoRa;

/// <summary>
///     Immutable snapshot of the Meshtastic device state (published by the gate radio thread).
///     Safe to read from any thread: it never touches the port or shared mutable state.
/// </summary>
public sealed record MeshtasticLinkState(
    bool IsOpen,
    uint? LocalNodeNumber,
    IReadOnlyList<MeshtasticRadioNode> Nodes)
{
    public static readonly MeshtasticLinkState Closed = new(false, null, []);
}
