namespace PcAgent.Collection;

public readonly record struct SampleThresholds(double CpuHigh, double MemAvailLow, double CommitHigh, double DiskBusy)
{
    public static SampleThresholds From(AgentConfig c) =>
        new(c.CpuHighPercent, c.MemoryAvailableLowPercent, c.CommitHighPercent, c.DiskActiveHighPercent);
}

/// <summary>
/// 5초 샘플을 1분 단위로 집계합니다. 분이 바뀌는 순간 이전 분의 결과를 반환합니다.
/// 시간(초)은 샘플마다 그 시점의 측정 주기만큼 더하므로, 도중에 설정이 바뀌어도 정확합니다.
/// </summary>
public sealed class MinuteAggregator
{
    private SampleThresholds _thresholds;
    private int _intervalSeconds;

    private bool _has;
    private DateTimeOffset _minute;
    private int _n, _covered;
    private double _cpuSum, _cpuMax, _availSum, _availMin, _commitSum, _commitMax;
    private double _hardSum, _diskSum, _diskMax, _queueSum;
    private int _hardN, _diskN, _queueN;
    private int _cpuHighSec, _memPressureSec, _diskBusySec;
    private double _commitGbMax, _perfSum, _latencySum, _latencyMax;
    private int _commitGbN, _perfN, _latencyN, _batteryN, _batterySec;

    public MinuteAggregator(SampleThresholds thresholds, int intervalSeconds)
    {
        _thresholds = thresholds;
        _intervalSeconds = intervalSeconds;
    }

    /// <summary>진행 중인 1분 집계를 유지한 채 기준만 바꿉니다(설정 변경 시).</summary>
    public void Reconfigure(SampleThresholds thresholds, int intervalSeconds)
    {
        _thresholds = thresholds;
        _intervalSeconds = intervalSeconds;
    }

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

        int sec = _intervalSeconds;
        _n++;
        _covered += sec;
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
            if (d >= _thresholds.DiskBusy) _diskBusySec += sec;
        }
        if (s.DiskQueueLength is { } q) { _queueSum += q; _queueN++; }
        if (s.CpuPercent >= _thresholds.CpuHigh) _cpuHighSec += sec;
        if (s.MemAvailPercent <= _thresholds.MemAvailLow || s.CommitPercent >= _thresholds.CommitHigh) _memPressureSec += sec;

        if (s.CommitGb is { } cg) { _commitGbMax = Math.Max(_commitGbMax, cg); _commitGbN++; }
        // CPU 속도는 30초에 한 번만 들어옴(값 자체가 그 구간에서 명령을 실행한 동안의 평균). 한가한 분의 저속은 분석 때 걸러냄
        if (s.CpuPerfPercent is { } perf) { _perfSum += perf; _perfN++; }
        if (s.DiskLatencyMs is { } lat)
        {
            _latencyMax = Math.Max(_latencyMax, lat);
            if (lat > 0) { _latencySum += lat; _latencyN++; }   // I/O 가 없던 샘플(0)은 평균에서 제외
        }
        if (s.OnBattery is { } battery)
        {
            _batteryN++;
            if (battery) _batterySec += sec;
        }

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
        CoveredSeconds = _covered,
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
        CpuHighSeconds = _cpuHighSec,
        MemPressureSeconds = _memPressureSec,
        DiskBusySeconds = _diskBusySec,
        CommitGbMax = _commitGbN > 0 ? Math.Round(_commitGbMax, 2) : null,
        CpuPerfAvg = _perfN > 0 ? R(_perfSum / _perfN) : null,
        DiskLatencyAvg = _latencyN > 0 ? Math.Round(_latencySum / _latencyN, 2) : null,
        DiskLatencyMax = _latencyN > 0 ? Math.Round(_latencyMax, 2) : null,
        BatterySeconds = _batteryN > 0 ? _batterySec : null,
    };

    private void Reset()
    {
        _has = false;
        _n = _covered = 0;
        _cpuSum = _cpuMax = _availSum = _commitSum = _commitMax = 0;
        _availMin = double.MaxValue;
        _hardSum = _diskSum = _diskMax = _queueSum = 0;
        _hardN = _diskN = _queueN = 0;
        _cpuHighSec = _memPressureSec = _diskBusySec = 0;
        _commitGbMax = _perfSum = _latencySum = _latencyMax = 0;
        _commitGbN = _perfN = _latencyN = _batteryN = _batterySec = 0;
    }

    private static DateTimeOffset Truncate(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

    private static double R(double v) => Math.Round(v, 1);
}
