namespace PcAgent.Collection;

public readonly record struct SampleThresholds(double CpuHigh, double MemAvailLow, double CommitHigh, double DiskBusy)
{
    public static SampleThresholds From(AgentConfig c) =>
        new(c.CpuHighPercent, c.MemoryAvailableLowPercent, c.CommitHighPercent, c.DiskActiveHighPercent);
}

/// <summary>
/// 5초 샘플을 1분 단위로 집계합니다. 분이 바뀌는 순간 이전 분의 결과를 반환합니다.
/// </summary>
public sealed class MinuteAggregator(SampleThresholds thresholds, int intervalSeconds)
{
    private bool _has;
    private DateTimeOffset _minute;
    private int _n;
    private double _cpuSum, _cpuMax, _availSum, _availMin, _commitSum, _commitMax;
    private double _hardSum, _diskSum, _diskMax, _queueSum;
    private int _hardN, _diskN, _queueN;
    private int _cpuHigh, _memPressure, _diskBusy;

    public MetricMinute? Add(in SystemSample s)
    {
        var minute = Truncate(s.TimestampUtc);
        MetricMinute? completed = null;
        if (_has && minute != _minute)
        {
            completed = Build();
            Reset();
        }
        if (!_has)
        {
            _has = true;
            _minute = minute;
            _availMin = double.MaxValue;
        }

        _n++;
        _cpuSum += s.CpuPercent;
        _cpuMax = Math.Max(_cpuMax, s.CpuPercent);
        _availSum += s.MemAvailPercent;
        _availMin = Math.Min(_availMin, s.MemAvailPercent);
        _commitSum += s.CommitPercent;
        _commitMax = Math.Max(_commitMax, s.CommitPercent);
        if (s.HardFaultsPerSec is { } hf) { _hardSum += hf; _hardN++; }
        if (s.DiskActivePercent is { } d)
        {
            _diskSum += d; _diskN++;
            _diskMax = Math.Max(_diskMax, d);
            if (d >= thresholds.DiskBusy) _diskBusy++;
        }
        if (s.DiskQueueLength is { } q) { _queueSum += q; _queueN++; }
        if (s.CpuPercent >= thresholds.CpuHigh) _cpuHigh++;
        if (s.MemAvailPercent <= thresholds.MemAvailLow || s.CommitPercent >= thresholds.CommitHigh) _memPressure++;

        return completed;
    }

    public MetricMinute? Flush()
    {
        if (!_has || _n == 0) return null;
        var m = Build();
        Reset();
        return m;
    }

    private MetricMinute Build() => new()
    {
        MinuteUtc = _minute,
        Samples = _n,
        CoveredSeconds = _n * intervalSeconds,
        CpuAvg = R(_cpuSum / _n),
        CpuMax = R(_cpuMax),
        MemAvailAvg = R(_availSum / _n),
        MemAvailMin = R(_availMin),
        CommitAvg = R(_commitSum / _n),
        CommitMax = R(_commitMax),
        HardFaultsAvg = _hardN > 0 ? R(_hardSum / _hardN) : null,
        DiskActiveAvg = _diskN > 0 ? R(_diskSum / _diskN) : null,
        DiskActiveMax = _diskN > 0 ? R(_diskMax) : null,
        DiskQueueAvg = _queueN > 0 ? Math.Round(_queueSum / _queueN, 2) : null,
        CpuHighSeconds = _cpuHigh * intervalSeconds,
        MemPressureSeconds = _memPressure * intervalSeconds,
        DiskBusySeconds = _diskBusy * intervalSeconds,
    };

    private void Reset()
    {
        _has = false;
        _n = 0;
        _cpuSum = _cpuMax = _availSum = _commitSum = _commitMax = 0;
        _availMin = double.MaxValue;
        _hardSum = _diskSum = _diskMax = _queueSum = 0;
        _hardN = _diskN = _queueN = 0;
        _cpuHigh = _memPressure = _diskBusy = 0;
    }

    private static DateTimeOffset Truncate(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

    private static double R(double v) => Math.Round(v, 1);
}
