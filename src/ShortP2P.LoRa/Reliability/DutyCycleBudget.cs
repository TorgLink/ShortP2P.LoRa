namespace ShortP2P.LoRa.Reliability;

/// <summary>
///     REQ-RF-2 default product profile: TX 10% / RX 90% of estimated on-air time over a rolling
///     one-hour window (Meshtastic EU semantics). When the budget is spent, TX defers until the
///     window slides; the remainder of the window is RX / listen. Not thread-safe by itself:
///     the caller serializes access (the transport TX window).
/// </summary>
public sealed class DutyCycleBudget
{
    /// <summary>Conservative on-air estimate per payload byte (≈ SF11 @ 250 kHz ≈ 90 payload bytes/s).</summary>
    public const double DefaultAirtimePerByteMs = 11.0;

    private readonly TimeSpan _window;
    private readonly double _budgetSeconds;
    private readonly double _airtimePerByteMs;
    private readonly Queue<(DateTime Utc, double AirtimeSeconds)> _tx = new();
    private double _spentSeconds;

    public DutyCycleBudget(
        double dutyCyclePercent,
        TimeSpan? window = null,
        double airtimePerByteMs = DefaultAirtimePerByteMs)
    {
        if (dutyCyclePercent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(dutyCyclePercent));
        if (airtimePerByteMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(airtimePerByteMs));

        _window = window ?? TimeSpan.FromHours(1);
        _budgetSeconds = _window.TotalSeconds * dutyCyclePercent / 100.0;
        _airtimePerByteMs = airtimePerByteMs;
    }

    /// <summary>Total estimated TX seconds recorded inside the current window.</summary>
    public double SpentSeconds
    {
        get
        {
            PurgeExpired(DateTime.UtcNow);
            return _spentSeconds;
        }
    }

    /// <summary>Total TX seconds allowed by the window and duty cycle percent.</summary>
    public double BudgetSeconds => _budgetSeconds;

    /// <summary>
    ///     How long TX must wait before this frame fits the budget. Zero when the budget already
    ///     allows it. A frame that alone exceeds the entire budget is allowed immediately (a single
    ///     frame can never be deferred into the past).
    /// </summary>
    public TimeSpan WaitTimeFor(int frameBytes, DateTime utcNow)
    {
        PurgeExpired(utcNow);
        var need = frameBytes * _airtimePerByteMs / 1000.0;
        if (_spentSeconds + need <= _budgetSeconds)
            return TimeSpan.Zero;
        if (_tx.Count == 0)
            return TimeSpan.Zero;

        var wait = _tx.Peek().Utc + _window - utcNow;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>Record an actually transmitted frame (estimated on-air time) in the window.</summary>
    public void RecordTx(int frameBytes, DateTime utcNow)
    {
        var airtime = frameBytes * _airtimePerByteMs / 1000.0;
        _tx.Enqueue((utcNow, airtime));
        _spentSeconds += airtime;
    }

    private void PurgeExpired(DateTime utcNow)
    {
        while (_tx.Count > 0 && utcNow - _tx.Peek().Utc >= _window)
            _spentSeconds -= _tx.Dequeue().AirtimeSeconds;
    }
}
