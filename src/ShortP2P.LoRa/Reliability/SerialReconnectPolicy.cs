namespace ShortP2P.LoRa.Reliability;

/// <summary>
///     Backoff for Meshtastic serial reconnect when the COM port is closed / the device is unavailable.
///     Attempts 1–10 inclusive: fixed 5 s. After more than 10 failures: logarithmic growth capped at 30 s.
/// </summary>
public static class SerialReconnectPolicy
{
    public static readonly TimeSpan FixedDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    public const int FixedDelayAttempts = 10;

    /// <summary>
    ///     Delay before the next reconnect try after <paramref name="failedAttempts"/> consecutive failures
    ///     (1 = first failure just happened).
    /// </summary>
    public static TimeSpan DelayAfterFailures(int failedAttempts)
    {
        if (failedAttempts <= 0)
            return FixedDelay;
        if (failedAttempts <= FixedDelayAttempts)
            return FixedDelay;

        // attempt 11 → 5*log2(3)≈7.9s; 12→10s; 16→15s; … clamp to [5, 30]
        var seconds = FixedDelay.TotalSeconds * Math.Log2(failedAttempts - (FixedDelayAttempts - 2));
        return TimeSpan.FromSeconds(Math.Clamp(seconds, FixedDelay.TotalSeconds, MaxDelay.TotalSeconds));
    }
}
