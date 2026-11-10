namespace ShortP2P.LoRa.Reliability;

/// <summary>
///     REQ-RF-2 default product profile: TX 10% / RX 90% of estimated on-air time over a rolling
///     one-hour window (Meshtastic EU semantics). Airtime is paced evenly across the window
///     (token bucket at budget/window) so TX cannot dump the whole hour early and then stall.
///     The rolling-hour sum remains the hard regulatory ceiling. Not thread-safe by itself:
///     the caller serializes access (the transport TX window / gate radio thread).
/// </summary>
public sealed class DutyCycleBudget
{
    /// <summary>Conservative on-air estimate per payload byte (≈ SF11 @ 250 kHz ≈ 90 payload bytes/s).</summary>
    public const double DefaultAirtimePerByteMs = 11.0;

    /// <summary>
    ///     Wall-clock seconds of earning kept as burst credit (short chat/handshake trains).
    ///     At TX 10% / 1 h this is 6 s of airtime.
    /// </summary>
    public const double DefaultBurstEarnSeconds = 60.0;

    private readonly TimeSpan _window;
    private readonly double _budgetSeconds;
    private readonly double _airtimePerByteMs;
    private readonly double _paceRate;
    private readonly double _paceCapacity;
    private readonly Queue<(DateTime Utc, double AirtimeSeconds)> _tx = new();
    private double _spentSeconds;
    private double _paceCredit;
    private DateTime _paceAccruedUtc;
    private bool _paceStarted;

    public DutyCycleBudget(
        double dutyCyclePercent,
        TimeSpan? window = null,
        double airtimePerByteMs = DefaultAirtimePerByteMs,
        double? burstEarnSeconds = null)
    {
        if (dutyCyclePercent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(dutyCyclePercent));
        if (airtimePerByteMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(airtimePerByteMs));

        _window = window ?? TimeSpan.FromHours(1);
        if (_window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _budgetSeconds = _window.TotalSeconds * dutyCyclePercent / 100.0;
        _airtimePerByteMs = airtimePerByteMs;
        _paceRate = _budgetSeconds / _window.TotalSeconds;

        var earn = burstEarnSeconds ?? DefaultBurstEarnSeconds;
        if (earn < 0)
            throw new ArgumentOutOfRangeException(nameof(burstEarnSeconds));

        // Burst is earned airtime for a short train; never larger than the hourly budget.
        _paceCapacity = Math.Min(_budgetSeconds, _paceRate * earn);
        _paceCredit = _paceCapacity;
    }

    /// <summary>
    ///     Total estimated TX seconds recorded inside the current window.
    ///     Callers must drive time via <see cref="WaitTimeFor"/> / <see cref="RecordTx"/>;
    ///     this getter does not purge against wall clock on its own.
    /// </summary>
    public double SpentSeconds => _spentSeconds;

    /// <summary>Total TX seconds allowed by the window and duty cycle percent.</summary>
    public double BudgetSeconds => _budgetSeconds;

    /// <summary>Sustained TX airtime seconds allowed per wall-clock second (budget / window).</summary>
    public double PaceRateSecondsPerSecond => _paceRate;

    /// <summary>Max accumulated pace credit (burst), in airtime seconds.</summary>
    public double PaceCapacitySeconds => _paceCapacity;

    /// <summary>
    ///     How long TX must wait before this frame fits both even pacing and the rolling-hour cap.
    ///     Zero when the budget already allows it. A frame that alone exceeds the entire budget is
    ///     allowed immediately when nothing else is in the window (a single frame can never be
    ///     deferred into the past).
    /// </summary>
    public TimeSpan WaitTimeFor(int frameBytes, DateTime utcNow)
    {
        PurgeExpired(utcNow);
        AccruePace(utcNow);
        var need = EstimateAirtimeSeconds(frameBytes);
        if (need <= 0)
            return TimeSpan.Zero;

        // Oversized single frame: never park forever when the window is otherwise empty.
        if (_tx.Count == 0 && need >= _budgetSeconds)
            return TimeSpan.Zero;

        var hardWait = TimeSpan.Zero;
        if (_spentSeconds + need > _budgetSeconds && _tx.Count > 0)
        {
            var untilOldestLeaves = _tx.Peek().Utc + _window - utcNow;
            if (untilOldestLeaves > TimeSpan.Zero)
                hardWait = untilOldestLeaves;
        }

        var paceWait = TimeSpan.Zero;
        if (_paceCredit < need && _paceRate > 0)
            paceWait = TimeSpan.FromSeconds((need - _paceCredit) / _paceRate);

        var wait = hardWait > paceWait ? hardWait : paceWait;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>Record an actually transmitted frame (estimated on-air time) in the window.</summary>
    public void RecordTx(int frameBytes, DateTime utcNow)
    {
        AccruePace(utcNow);
        var airtime = EstimateAirtimeSeconds(frameBytes);
        _tx.Enqueue((utcNow, airtime));
        _spentSeconds += airtime;
        _paceCredit = Math.Max(0, _paceCredit - airtime);
    }

    private double EstimateAirtimeSeconds(int frameBytes) =>
        Math.Max(0, frameBytes) * _airtimePerByteMs / 1000.0;

    private void AccruePace(DateTime utcNow)
    {
        if (!_paceStarted)
        {
            _paceAccruedUtc = utcNow;
            _paceStarted = true;
            return;
        }

        if (utcNow < _paceAccruedUtc)
        {
            _paceAccruedUtc = utcNow;
            return;
        }

        var dt = (utcNow - _paceAccruedUtc).TotalSeconds;
        if (dt > 0)
        {
            _paceCredit = Math.Min(_paceCapacity, _paceCredit + dt * _paceRate);
            _paceAccruedUtc = utcNow;
        }
    }

    private void PurgeExpired(DateTime utcNow)
    {
        while (_tx.Count > 0 && utcNow - _tx.Peek().Utc >= _window)
            _spentSeconds -= _tx.Dequeue().AirtimeSeconds;
    }
}
