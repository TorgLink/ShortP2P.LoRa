namespace ShortP2P.LoRa;

/// <summary>One outbound datagram waiting in a priority queue.</summary>
internal sealed class MeshtasticTxWorkItem
{
    public MeshtasticTxWorkItem(
        uint node,
        byte[] datagram,
        MeshtasticTxPriority priority,
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken)
    {
        Node = node;
        Datagram = datagram ?? throw new ArgumentNullException(nameof(datagram));
        Priority = priority;
        Completion = completion ?? throw new ArgumentNullException(nameof(completion));
        CancellationToken = cancellationToken;
    }

    public uint Node { get; }
    public byte[] Datagram { get; }
    public MeshtasticTxPriority Priority { get; }
    public TaskCompletionSource<bool> Completion { get; }
    public CancellationToken CancellationToken { get; }
}
