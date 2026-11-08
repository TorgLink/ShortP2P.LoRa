namespace ShortP2P.LoRa.Reliability;

/// <summary>
///     Send-Retry / Receive-Retry settings (REQ-CFG-3). Backoff is exponential:
///     interval(retry) = base * 2^retry, so 1 s, 2 s, 4 s on defaults.
/// </summary>
public sealed record RetryConfig
{
    public TimeSpan SendRetryIntervalBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Send attempts before <c>SendAsync</c> fails. Default 3.</summary>
    public int SendMaxRetries { get; init; } = 3;

    public TimeSpan RxRetryIntervalBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     No next chunk for this long → ask the sender to resend. Default 180 s: under the 1%
    ///     duty-cycle budget a multi-chunk datagram can sit between radio frames for well over
    ///     half a minute (presence/keepalive and other chat share the same airtime window), so a
    ///     30 s timeout falsely aborts reassembly and floods 0x33 resends.
    /// </summary>
    public TimeSpan RxTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>Receive-side resend requests before the message is dropped. Default 3.</summary>
    public int RxMaxRetries { get; init; } = 3;

    public TimeSpan SendDelay(int retry)
    {
        return Scale(SendRetryIntervalBase, retry);
    }

    public TimeSpan RxDelay(int retry)
    {
        return Scale(RxRetryIntervalBase, retry);
    }

    private static TimeSpan Scale(TimeSpan baseInterval, int retry)
    {
        var shift = Math.Clamp(retry, 0, 20);
        var ticks = baseInterval.Ticks << shift;
        return ticks < baseInterval.Ticks ? baseInterval : TimeSpan.FromTicks(ticks);
    }
}
