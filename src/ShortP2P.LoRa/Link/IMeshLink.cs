namespace ShortP2P.LoRa.Link;

/// <summary>One raw radio payload delivered by the board link.</summary>
public readonly record struct MeshLinkFrame(uint From, uint To, byte[] Payload);

/// <summary>
///     Physical link to the LoRa board (REQ-TR-1). Implementations: Meshtastic serial link, fakes in tests.
///     Payloads are opaque to the link — chunking / ShortP2P framing live above it.
/// </summary>
public interface IMeshLink : IAsyncDisposable
{
    /// <summary>Local radio node number, known after start (board-reported).</summary>
    uint? LocalNodeNumber { get; }

    event Action<MeshLinkFrame>? Received;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Send one payload to a node (0xFFFFFFFF = broadcast). Throws on TX error → Send-Retry above.</summary>
    ValueTask SendAsync(uint to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Send one radio frame. When <paramref name="paceExempt"/> is true, skip even-pace deferral
    ///     while still honoring the rolling-hour hard airtime cap. Used only after the caller has
    ///     already waited for even-pace credit covering a whole chunk train (burst spend of earned
    ///     airtime). Default implementation ignores the flag (test fakes / links without a duty gate).
    /// </summary>
    ValueTask SendAsync(uint to, ReadOnlyMemory<byte> payload, bool paceExempt,
        CancellationToken cancellationToken = default) =>
        SendAsync(to, payload, cancellationToken);

    /// <summary>
    ///     Wait until even-pace + hard-cap duty budget allows <paramref name="estimatedAirtimeBytes"/>
    ///     of TX (same units as <see cref="SendAsync"/> payload accounting). No-op when the link has
    ///     no duty gate. Callers use this before a multi-chunk train so airtime is earned evenly,
    ///     then spent as one short burst.
    /// </summary>
    ValueTask WaitForDutyBudgetAsync(int estimatedAirtimeBytes,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
