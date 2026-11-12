using PcAgent.Analysis;
using PcAgent.Collection;
using PcAgent.EventLogs;
using PcAgent.Inventory;

namespace PcAgent.Tests;

public class UpgradeAdvisorTests
{
    private static AnalysisResult Analyze(InventoryInfo inv, List<MetricMinute>? metrics = null,
        List<PressureRecord>? events = null, List<EventLogItem>? logs = null) =>
        UpgradeAdvisor.Analyze(new AnalysisInput
        {
            Inventory = inv,
            Metrics = metrics ?? TestData.WorkdayMetrics(5),
            Events = events ?? [],
            Logs = logs ?? [],
            Config = new AgentConfig(),
            TimeZone = TimeZoneInfo.Utc,
        });

    [Fact]
    public void Healthy_pc_is_ok()
    {
        var r = Analyze(TestData.Inventory());
        Assert.Equal("Ok", r.Verdict);
        Assert.Empty(r.Recommendations);
        Assert.Equal(35, r.Stats.BasisHours);
    }

    [Fact]
    public void Healthy_pc_without_data_is_insufficient()
    {
        var r = Analyze(TestData.Inventory(), metrics: []);
        Assert.Equal("InsufficientData", r.Verdict);
        Assert.False(r.SufficientData);
    }

    [Fact]
    public void Frequent_memory_pressure_recommends_ram_upgrade_with_target()
    {
        var r = Analyze(TestData.Inventory(ramMb: 8192), TestData.WorkdayMetrics(5, memPressurePct: 20));

        var rec = Assert.Single(r.Recommendations, x => x.Code == "RAM_UPGRADE");
        Assert.Equal("High", rec.Priority);
        Assert.Contains("16GB", rec.Detail);
        Assert.Contains("빈 슬롯 1개", rec.Detail);
        Assert.Equal("Upgrade", r.Verdict);
        Assert.Equal(20, r.Stats.MemPressurePct);
    }

    [Fact]
    public void Memory_shortage_on_soldered_ram_recommends_replacement()
    {
        var inv = TestData.Inventory(ramMb: 8192);
        inv.RamModules[0].FormFactor = "Row of chips";
        var r = Analyze(inv, TestData.WorkdayMetrics(5, memPressurePct: 20));

        Assert.DoesNotContain(r.Recommendations, x => x.Code == "RAM_UPGRADE");
        var rec = r.Recommendations.Single(x => x.Code == "PC_REPLACE");
        Assert.Contains("온보드", rec.Detail);
        Assert.Contains("16GB", rec.Detail);
        Assert.Equal("Replace", r.Verdict);
    }

    [Fact]
    public void Pressure_outside_working_hours_is_not_counted()
    {
        var metrics = TestData.WorkdayMetrics(5);
        // 토요일 하루 종일 메모리 부족
        for (int m = 0; m < 600; m++)
            metrics.Add(new MetricMinute { MinuteUtc = TestData.Monday.AddDays(5).AddHours(8).AddMinutes(m), Samples = 12, CoveredSeconds = 60, MemPressureSeconds = 60 });

        var r = Analyze(TestData.Inventory(), metrics);
        Assert.Equal(0, r.Stats.MemPressurePct);
        Assert.Equal("Ok", r.Verdict);
    }

    [Fact]
    public void Hdd_system_disk_recommends_ssd_even_without_data()
    {
        var r = Analyze(TestData.Inventory(media: "HDD"), metrics: []);
        var rec = Assert.Single(r.Recommendations, x => x.Code == "SSD_REPLACE");
        Assert.Equal("Medium", rec.Priority);

        var busy = Analyze(TestData.Inventory(media: "HDD"), TestData.WorkdayMetrics(5, diskBusyPct: 10));
        Assert.Equal("High", busy.Recommendations.Single(x => x.Code == "SSD_REPLACE").Priority);
    }

    [Fact]
    public void Windows10_on_unsupported_cpu_recommends_replacement()
    {
        var r = Analyze(TestData.Inventory(win11: false, cpu: "Intel(R) Core(TM) i5-6500 CPU @ 3.20GHz"));
        Assert.Equal("Replace", r.Verdict);
        Assert.Contains("미지원 CPU", r.Recommendations.Single(x => x.Code == "PC_REPLACE").Detail);
    }

    [Fact]
    public void Windows10_on_supported_hardware_recommends_os_upgrade()
    {
        var r = Analyze(TestData.Inventory(win11: false));
        Assert.Single(r.Recommendations, x => x.Code == "OS_UPGRADE");
        Assert.Equal("Watch", r.Verdict);
    }

    [Fact]
    public void Cpu_saturation_on_old_pc_recommends_replacement()
    {
        var inv = TestData.Inventory();
        inv.EstimatedAgeYears = 7;
        var r = Analyze(inv, TestData.WorkdayMetrics(5, cpuHighPct: 25));
        Assert.Equal("Replace", r.Verdict);
    }

    [Fact]
    public void Single_dominant_process_in_memory_events_recommends_software_review()
    {
        var events = Enumerable.Range(0, 5)
            .Select(i => TestData.MemoryEvent(TestData.Monday.AddHours(11 + i), ("LeakyApp", 6000), ("chrome", 1500), ("Teams", 900)))
            .ToList();
        var r = Analyze(TestData.Inventory(), TestData.WorkdayMetrics(5, memPressurePct: 3), events);

        Assert.Equal("LeakyApp", r.MemoryOffenders[0].Name);
        Assert.Equal(5, r.MemoryOffenders[0].Rank1);
        Assert.Contains(r.Recommendations, x => x.Code == "SOFTWARE_REVIEW" && x.Title.Contains("LeakyApp"));
    }

    [Fact]
    public void Repeated_bugchecks_recommend_stability_check()
    {
        var logs = Enumerable.Range(0, 2)
            .Select(i => new EventLogItem { Category = "BugCheck", TimeUtc = TestData.Monday.AddDays(i) })
            .ToList();
        var r = Analyze(TestData.Inventory(), logs: logs);
        Assert.Equal("High", r.Recommendations.Single(x => x.Code == "STABILITY_CHECK").Priority);
    }

    [Fact]
    public void Low_free_space_recommends_cleanup()
    {
        var inv = TestData.Inventory();
        inv.Volumes[0].FreeGb = 8;
        var r = Analyze(inv);
        Assert.Single(r.Recommendations, x => x.Code == "DISK_CLEANUP");
    }

    [Fact]
    public void Percentile_interpolates()
    {
        Assert.Equal(5.5, UpgradeAdvisor.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 3);
        Assert.Equal(0, UpgradeAdvisor.Percentile([], 95));
    }
}
