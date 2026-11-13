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
    public void Software_review_needs_enough_observation_like_other_performance_rules()
    {
        // 관측 6시간(최소 8시간 미만) — "성능 기반 판정 생략" 안내와 모순되는 프로그램 점검 권고가 나오면 안 됨
        var metrics = TestData.WorkdayMetrics(1).Take(6 * 60).ToList();
        var events = Enumerable.Range(0, 5)
            .Select(i => TestData.MemoryEvent(TestData.Monday.AddHours(10).AddMinutes(i * 30), ("LeakyApp", 6000), ("chrome", 1500)))
            .ToList();
        var r = Analyze(TestData.Inventory(), metrics, events);

        Assert.False(r.SufficientData);
        Assert.DoesNotContain(r.Recommendations, x => x.Code == "SOFTWARE_REVIEW");
        Assert.Equal("LeakyApp", r.MemoryOffenders[0].Name);   // 원인 프로그램 목록은 그대로 보여 줌
        Assert.Equal("InsufficientData", r.Verdict);
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
    public void Few_work_hours_fall_back_to_all_hours_instead_of_insufficient()
    {
        // 업무시간 6시간(평일 1일 10~16시) + 주말 40시간, 모두 메모리 부족 25%
        var metrics = new List<MetricMinute>();
        void AddRange(DateTimeOffset start, int minutes)
        {
            for (int m = 0; m < minutes; m++)
                metrics.Add(new MetricMinute { MinuteUtc = start.AddMinutes(m), Samples = 12, CoveredSeconds = 60, MemPressureSeconds = 15 });
        }
        AddRange(TestData.Monday.AddHours(10), 6 * 60);
        AddRange(TestData.Monday.AddDays(5), 20 * 60);   // 토요일
        AddRange(TestData.Monday.AddDays(6), 20 * 60);   // 일요일

        var r = Analyze(TestData.Inventory(ramMb: 8192), metrics);

        Assert.True(r.SufficientData);
        Assert.Equal("전체 시간 기준", r.Basis);
        Assert.Contains(r.Recommendations, x => x.Code == "RAM_UPGRADE");
    }

    [Fact]
    public void Dominance_counts_only_events_with_process_snapshots()
    {
        var events = new List<PressureRecord>();
        for (int i = 0; i < 4; i++)
            events.Add(TestData.MemoryEvent(TestData.Monday.AddHours(11).AddMinutes(i * 15), ("LeakyApp", 6000), ("chrome", 1500), ("Teams", 900)));
        for (int i = 0; i < 6; i++)
        {
            var skipped = TestData.MemoryEvent(TestData.Monday.AddHours(13).AddMinutes(i * 5));
            skipped.Processes = null;
            skipped.SnapshotSkipped = true;   // 쿨다운으로 스냅샷 생략
            events.Add(skipped);
        }

        var r = Analyze(TestData.Inventory(), TestData.WorkdayMetrics(5, memPressurePct: 3), events);

        var rec = Assert.Single(r.Recommendations, x => x.Code == "SOFTWARE_REVIEW");
        Assert.Contains("4건 중 4건", rec.Detail);
    }

    [Fact]
    public void Memory_and_cpu_software_reviews_for_different_programs_are_separate()
    {
        var events = Enumerable.Range(0, 4)
            .Select(i => TestData.MemoryEvent(TestData.Monday.AddHours(11 + i), ("LeakyApp", 6000), ("chrome", 1500)))
            .ToList();
        events.AddRange(Enumerable.Range(0, 4).Select(i => new PressureRecord
        {
            Id = Guid.NewGuid(),
            Kind = PressureKind.Cpu,
            StartedUtc = TestData.Monday.AddDays(1).AddHours(11 + i),
            Processes = [new ProcessUsage { Name = "chrome", CpuPercent = 60 }, new ProcessUsage { Name = "LeakyApp", CpuPercent = 5 }],
        }));

        var r = Analyze(TestData.Inventory(), TestData.WorkdayMetrics(5, memPressurePct: 3, cpuHighPct: 5), events);

        var reviews = r.Recommendations.Where(x => x.Code == "SOFTWARE_REVIEW").Select(x => x.Title).ToList();
        Assert.Contains("프로그램 점검: LeakyApp", reviews);
        Assert.Contains("프로그램 점검: chrome", reviews);
    }

    [Fact]
    public void Ltsc_edition_is_not_forced_to_windows11()
    {
        var inv = TestData.Inventory(win11: false, cpu: "Intel(R) Core(TM) i5-7500 CPU @ 3.40GHz");
        inv.OsEdition = "EnterpriseS";
        var r = Analyze(inv);

        Assert.DoesNotContain(r.Recommendations, x => x.Code is "PC_REPLACE" or "OS_UPGRADE");
        Assert.Contains(r.Findings, f => f.Contains("LTSC"));
        Assert.Equal("Ok", r.Verdict);
    }

    [Fact]
    public void Legacy_boot_windows10_gets_config_upgrade_not_replacement()
    {
        var inv = TestData.Inventory(win11: false);
        inv.IsUefi = false;
        var r = Analyze(inv);

        var rec = Assert.Single(r.Recommendations);
        Assert.Equal("OS_UPGRADE", rec.Code);
        Assert.Contains("설정 변경", rec.Title);
        Assert.Equal("Watch", r.Verdict);
    }

    [Fact]
    public void Percentile_interpolates()
    {
        Assert.Equal(5.5, UpgradeAdvisor.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 3);
        Assert.Equal(0, UpgradeAdvisor.Percentile([], 95));
    }
}
