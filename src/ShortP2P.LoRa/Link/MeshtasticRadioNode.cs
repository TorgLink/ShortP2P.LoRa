namespace ShortP2P.LoRa.Link;

/// <summary>One node from the Meshtastic board node database (FromRadio.node_info).</summary>
public sealed record MeshtasticRadioNode(uint NodeNumber, string LongName, string ShortName, string UserId)
{
    /// <summary>Meshtastic user id ("!aabbccdd") or the node number in that form.</summary>
    public string NodeId =>
        string.IsNullOrWhiteSpace(UserId) ? $"!{NodeNumber:x8}" : UserId.Trim();

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LongName))
                return LongName.Trim();
            if (!string.IsNullOrWhiteSpace(ShortName))
                return ShortName.Trim();
            return NodeId;
        }
    }
}

/// <summary>Link that can list and refresh the board's Meshtastic node database.</summary>
public interface IMeshNodeCatalog
{
    IReadOnlyList<MeshtasticRadioNode> Nodes { get; }

    ValueTask RefreshAsync(CancellationToken cancellationToken = default);
}
