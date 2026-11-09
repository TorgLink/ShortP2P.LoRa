using System.Collections.Concurrent;

namespace ShortP2P.LoRa;

/// <summary>
///     Six separate FIFO TX queues, one per <see cref="MeshtasticTxPriority" />.
///     <see cref="TryDequeue" /> always drains a higher priority before a lower one.
///     Thread-safe for concurrent enqueue from senders and single-consumer dequeue by the TX pump.
/// </summary>
internal sealed class MeshtasticTxPriorityQueues
{
    private readonly ConcurrentQueue<MeshtasticTxWorkItem>[] _queues;

    public MeshtasticTxPriorityQueues()
    {
        _queues = new ConcurrentQueue<MeshtasticTxWorkItem>[6];
        for (var i = 0; i < _queues.Length; i++)
            _queues[i] = new ConcurrentQueue<MeshtasticTxWorkItem>();
    }

    public void Enqueue(MeshtasticTxWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _queues[IndexOf(item.Priority)].Enqueue(item);
    }

    /// <summary>Dequeue the next work item from the highest non-empty priority queue (1 → 6).</summary>
    public bool TryDequeue(out MeshtasticTxWorkItem item)
    {
        for (var i = 0; i < _queues.Length; i++)
        {
            if (_queues[i].TryDequeue(out item!))
                return true;
        }

        item = null!;
        return false;
    }

    public bool IsEmpty
    {
        get
        {
            foreach (var q in _queues)
                if (!q.IsEmpty)
                    return false;
            return true;
        }
    }

    private static int IndexOf(MeshtasticTxPriority priority)
    {
        var p = (int)priority;
        if (p is < 1 or > 6)
            return 5; // LargeBinary fallback
        return p - 1;
    }
}
