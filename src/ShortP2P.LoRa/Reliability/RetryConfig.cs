namespace ShortP2P.LoRa.Reliability;

/// <summary>
///     Send-Retry / Receive-Retry settings (REQ-CFG-3), stretched by the RX duty-cycle share.
///     Base ack/chunk wait is <see cref="BaseTimeout" /> (Tb = 60 s). Under a TX 10% / RX 90%
///     budget the peer may need many listen slots before it can TX a receipt or next chunk, so:
///     <c>Timeout = Tb * (1 + 0.015 * Nrx)</c> with <c>Nrx</c> = RX percent (default 90 → ≈ 141 s).
///     Send/Rx retry interval bases use the same timeout; backoff remains exponential:
///     <c>interval(retry) = Timeout * 2^retry</c>.
///     Large text / binary assemblies (estimated size ≥ <see cref="DefaultLargePayloadMinBytes" />,
///     aligning with TX priorities 4–6) use a fixed <see cref="LargePayloadTimeout" /> (180 s)
///     instead of the duty-scaled value when that fixed wait is longer.
/// </summary>
public sealed record RetryConfig
{
    /// <summary>Tb — base wait for a receipt / next chunk before duty scaling. Default 60 s.</summary>
    public static readonly TimeSpan DefaultBaseTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Overall next-chunk wait for large text / binary reassemblies (TX priorities 4–6).
    ///     Default 180 s.
    /// </summary>
    public static readonly TimeSpan DefaultLargePayloadTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    ///     Estimated reassembled size at which <see cref="LargePayloadTimeout" /> applies.
    ///     Matches the large-text band (≥ 1000 bytes) used by TX priority 4+.
    /// </summary>
    public const int DefaultLargePayloadMinBytes = 1000;

    /// <summary>Stretch coefficient in <c>Timeout = Tb * (1 + k * Nrx)</c>.</summary>
    public const double DefaultDutyStretchPerRxPercent = 0.015;

    /// <summary>Nrx for the product TX 10% / RX 90% profile (REQ-RF-2).</summary>
    public const double DefaultRxDutyPercent = 90;

    private static readonly TimeSpan DefaultDutyTimeout =
        ComputeTimeout(DefaultBaseTimeout, DefaultRxDutyPercent);

    /// <summary>Tb — base ack/chunk timeout before duty scaling.</summary>
    public TimeSpan BaseTimeout { get; init; } = DefaultBaseTimeout;

    /// <summary>
    ///     Nrx — RX share of the airtime budget in percent (100 − TX%). Default 90.
    /// </summary>
    public double RxDutyPercent { get; init; } = DefaultRxDutyPercent;

    /// <summary>k in <c>Timeout = Tb * (1 + k * Nrx)</c>. Default 0.015.</summary>
    public double DutyStretchPerRxPercent { get; init; } = DefaultDutyStretchPerRxPercent;

    /// <summary>
    ///     Effective ack / next-chunk wait after duty scaling for control / short / medium text.
    ///     Defaults to <see cref="ComputeTimeout" />; set explicitly to override (tests).
    /// </summary>
    public TimeSpan RxTimeout { get; init; } = DefaultDutyTimeout;

    /// <summary>
    ///     Overall next-chunk wait for large text / binary chunk assemblies (priorities 4–6).
    ///     Default 180 s; used when longer than <see cref="RxTimeout" />.
    /// </summary>
    public TimeSpan LargePayloadTimeout { get; init; } = DefaultLargePayloadTimeout;

    /// <summary>Base of the send backoff (defaults to the duty-scaled timeout).</summary>
    public TimeSpan SendRetryIntervalBase { get; init; } = DefaultDutyTimeout;

    /// <summary>Send attempts before <c>SendAsync</c> fails. Default 3.</summary>
    public int SendMaxRetries { get; init; } = 3;

    /// <summary>Base of the receive-request backoff (defaults to the duty-scaled timeout).</summary>
    public TimeSpan RxRetryIntervalBase { get; init; } = DefaultDutyTimeout;

    /// <summary>Receive-side resend requests before the message is dropped. Default 3.</summary>
    public int RxMaxRetries { get; init; } = 3;

    /// <summary>
    ///     <c>Timeout = Tb * (1 + stretch * Nrx)</c>.
    ///     Example: Tb=60 s, Nrx=90 → 60 * (1 + 1.35) = 141 s.
    /// </summary>
    public static TimeSpan ComputeTimeout(
        TimeSpan baseTimeout,
        double rxDutyPercent,
        double stretchPerRxPercent = DefaultDutyStretchPerRxPercent)
    {
        if (baseTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseTimeout));
        if (rxDutyPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(rxDutyPercent));
        if (stretchPerRxPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(stretchPerRxPercent));

        var factor = 1.0 + stretchPerRxPercent * rxDutyPercent;
        // Round to whole ms so duty defaults (e.g. Tb=60s, Nrx=90 → 141s) stay exact in TimeSpan.
        return TimeSpan.FromMilliseconds(Math.Round(baseTimeout.TotalMilliseconds * factor));
    }

    /// <summary>Nrx = clamp(100 − TX%, 0..100).</summary>
    public static double RxPercentFromTxDuty(double txDutyPercent) =>
        Math.Clamp(100.0 - txDutyPercent, 0, 100);

    /// <summary>
    ///     Build retry settings from the configured TX duty-cycle percent so timeouts follow the
    ///     channel budget (Tb=60 s unless overridden).
    /// </summary>
    public static RetryConfig FromTxDutyPercent(double txDutyPercent, TimeSpan? baseTimeout = null)
    {
        var tb = baseTimeout ?? DefaultBaseTimeout;
        var nrx = RxPercentFromTxDuty(txDutyPercent);
        var timeout = ComputeTimeout(tb, nrx);
        return new RetryConfig
        {
            BaseTimeout = tb,
            RxDutyPercent = nrx,
            RxTimeout = timeout,
            SendRetryIntervalBase = timeout,
            RxRetryIntervalBase = timeout
        };
    }

    /// <summary>Recompute <see cref="RxTimeout" /> / retry bases from current Tb and Nrx.</summary>
    public RetryConfig WithDutyScaledTimeouts()
    {
        var timeout = ComputeTimeout(BaseTimeout, RxDutyPercent, DutyStretchPerRxPercent);
        return this with
        {
            RxTimeout = timeout,
            SendRetryIntervalBase = timeout,
            RxRetryIntervalBase = timeout
        };
    }

    /// <summary>
    ///     Next-chunk wait for an in-progress reassembly.
    ///     Estimated size ≥ <paramref name="largeMinBytes" /> (default 1000 → large text / binary
    ///     bands, TX priorities 4–6) uses <see cref="LargePayloadTimeout" /> when it exceeds the
    ///     duty-scaled <see cref="RxTimeout" />; smaller assemblies keep <see cref="RxTimeout" />.
    /// </summary>
    public TimeSpan ChunkTimeoutForEstimatedSize(
        int estimatedBytes,
        int largeMinBytes = DefaultLargePayloadMinBytes)
    {
        if (estimatedBytes >= largeMinBytes && LargePayloadTimeout > RxTimeout)
            return LargePayloadTimeout;
        return RxTimeout;
    }

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
