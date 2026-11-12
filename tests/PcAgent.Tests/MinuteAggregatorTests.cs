using PcAgent.Collection;

namespace PcAgent.Tests;

public class MinuteAggregatorTests
{
    private static readonly SampleThresholds Thresholds = new(CpuHigh: 90, MemAvailLow: 10, CommitHigh: 90, DiskBusy: 90);
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);

    private static SystemSample Sample(DateTimeOffset t, double cpu = 50, double avail = 40, double commit = 60, double? disk = 10) =>
        new(t, cpu, avail, commit, HardFaultsPerSec: 2, DiskActivePercent: disk, DiskQueueLength: 0.1);

    [Fact]
    public void Emits_previous_minute_when_minute_changes()
    {
        var agg = new MinuteAggregator(Thresholds, intervalSeconds: 5);
        for (int i = 0; i < 12; i++)
        {
            double cpu = i < 2 ? 95 : 50;
            double avail = i == 5 ? 8 : 40;
            Assert.Null(agg.Add(Sample(T0.AddSeconds(i * 5), cpu: cpu, avail: avail)));
        }

        var m = agg.Add(Sample(T0.AddMinutes(1)));

        Assert.NotNull(m);
        Assert.Equal(T0, m.MinuteUtc);
        Assert.Equal(12, m.Samples);
        Assert.Equal(60, m.CoveredSeconds);
        Assert.Equal(95, m.CpuMax);
        Assert.Equal(10, m.CpuHighSeconds);      // 2 샘플 × 5초
        Assert.Equal(5, m.MemPressureSeconds);   // 1 샘플 × 5초
        Assert.Equal(8, m.MemAvailMin);
        Assert.Equal(57.5, m.CpuAvg);
    }

    [Fact]
    public void Commit_over_threshold_counts_as_memory_pressure()
    {
        var agg = new MinuteAggregator(Thresholds, 5);
        agg.Add(Sample(T0, commit: 95));
        var m = agg.Flush();
        Assert.Equal(5, m!.MemPressureSeconds);
    }

    [Fact]
    public void Reconfigure_mid_minute_keeps_partial_minute_and_uses_new_interval()
    {
        var agg = new MinuteAggregator(Thresholds, 5);
        agg.Add(Sample(T0, cpu: 95));
        agg.Add(Sample(T0.AddSeconds(5), cpu: 95));

        agg.Reconfigure(Thresholds with { CpuHigh = 99 }, 10);
        agg.Add(Sample(T0.AddSeconds(10), cpu: 95));   // 새 기준(99)에서는 포화 아님

        var m = agg.Flush();
        Assert.Equal(3, m!.Samples);
        Assert.Equal(5 + 5 + 10, m.CoveredSeconds);
        Assert.Equal(10, m.CpuHighSeconds);
    }

    [Fact]
    public void Missing_disk_counter_yields_null_disk_metrics()
    {
        var agg = new MinuteAggregator(Thresholds, 5);
        agg.Add(Sample(T0, disk: null));
        var m = agg.Flush();
        Assert.Null(m!.DiskActiveAvg);
        Assert.Equal(0, m.DiskBusySeconds);
        Assert.Null(agg.Flush());
    }
}
