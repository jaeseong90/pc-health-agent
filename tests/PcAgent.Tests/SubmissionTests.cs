using PcAgent.Analysis;
using PcAgent.Collection;
using PcAgent.Export;
using PcAgent.Inventory;
using PcAgent.Storage;

namespace PcAgent.Tests;

/// <summary>v0.5.0: 상세 수집 지표, 시간 환산·분포 통계, 제출용 요약(결론·근거·점검표).</summary>
public class SubmissionTests
{
    private static readonly SampleThresholds Thresholds = new(CpuHigh: 90, MemAvailLow: 10, CommitHigh: 90, DiskBusy: 90);
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);

    private static AnalysisResult Analyze(InventoryInfo inv, List<MetricMinute>? metrics = null, List<PressureRecord>? events = null) =>
        UpgradeAdvisor.Analyze(new AnalysisInput
        {
            Inventory = inv,
            Metrics = metrics ?? TestData.WorkdayMetrics(5),
            Events = events ?? [],
            Logs = [],
            Config = new AgentConfig(),
            TimeZone = TimeZoneInfo.Utc,
        });

    private static string Status(AnalysisResult r, string item) => r.Checklist.Single(c => c.Item == item).Status;

    // ---------------- 수집 ----------------

    [Fact]
    public void Aggregator_summarizes_detail_metrics()
    {
        var agg = new MinuteAggregator(Thresholds, 5);
        agg.Add(new SystemSample(T0, 10, 40, 60, 2, 10, 0.1, CommitGb: 9.5, CpuPerfPercent: 90, DiskLatencyMs: 0, OnBattery: true));
        agg.Add(new SystemSample(T0.AddSeconds(5), 90, 40, 60, 2, 10, 0.1, CommitGb: 11.25, DiskLatencyMs: 4, OnBattery: false));   // CPU 속도는 30초에 한 번만 들어옴
        agg.Add(new SystemSample(T0.AddSeconds(10), 0, 40, 60, 2, 10, 0.1, CommitGb: 10, CpuPerfPercent: 110, DiskLatencyMs: 8, OnBattery: true));

        var m = agg.Flush()!;

        Assert.Equal(11.25, m.CommitGbMax);
        Assert.Equal(100, m.CpuPerfAvg);        // 측정된 샘플만 평균
        Assert.Equal(6, m.DiskLatencyAvg);      // I/O 가 없던 샘플(0ms)은 평균에서 제외
        Assert.Equal(8, m.DiskLatencyMax);
        Assert.Equal(10, m.BatterySeconds);     // 배터리 샘플 2개 × 5초
    }

    [Fact]
    public void Aggregator_leaves_detail_metrics_empty_when_not_measured()
    {
        var agg = new MinuteAggregator(Thresholds, 5);
        agg.Add(new SystemSample(T0, 10, 40, 60, 2, 10, 0.1));
        agg.Add(new SystemSample(T0.AddSeconds(5), 10, 40, 60, 2, 10, 0.1, DiskLatencyMs: 0));

        var m = agg.Flush()!;

        Assert.Null(m.CommitGbMax);
        Assert.Null(m.CpuPerfAvg);
        Assert.Null(m.DiskLatencyAvg);   // I/O 가 한 번도 없었으면 응답 시간도 없음
        Assert.Null(m.BatterySeconds);
    }

    [Fact]
    public void DataStore_reads_lines_written_by_older_versions_and_round_trips_new_columns()
    {
        // v0.4.0 이 쓴 줄(16열)
        Assert.True(DataStore.TryParseMetric("2026-11-12T08:14:00Z,6,30,5.1,8.8,21.2,20.6,79.8,80,11,1.7,2.5,0.02,0,5,0", out var old));
        Assert.Equal(5, old.MemPressureSeconds);
        Assert.Null(old.CommitGbMax);
        Assert.Null(old.BatterySeconds);

        var dir = Path.Combine(Path.GetTempPath(), "pcagent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DataStore(dir);
            var t = DateTimeOffset.UtcNow.AddMinutes(-10);
            var minute = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);
            store.AppendMetric(new MetricMinute
            {
                MinuteUtc = minute, Samples = 12, CoveredSeconds = 60,
                CommitGbMax = 12.34, CpuPerfAvg = 98.5, DiskLatencyAvg = 0.42, DiskLatencyMax = 3.1, BatterySeconds = 30,
            });
            store.AppendMetric(new MetricMinute { MinuteUtc = minute.AddMinutes(1), Samples = 12, CoveredSeconds = 60 });   // 측정값 없음

            var read = store.ReadMetrics(minute.AddHours(-1), DateTimeOffset.UtcNow);

            Assert.Equal(2, read.Count);
            Assert.Equal(12.34, read[0].CommitGbMax);
            Assert.Equal(98.5, read[0].CpuPerfAvg);
            Assert.Equal(0.42, read[0].DiskLatencyAvg);
            Assert.Equal(3.1, read[0].DiskLatencyMax);
            Assert.Equal(30, read[0].BatterySeconds);
            Assert.Null(read[1].CommitGbMax);
            Assert.Null(read[1].BatterySeconds);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- 판정 ----------------

    [Fact]
    public void Memory_watch_on_soldered_ram_does_not_suggest_adding_memory()
    {
        var soldered = TestData.Inventory();
        soldered.RamModules[0].FormFactor = "Row of chips";

        var rec = Assert.Single(Analyze(soldered, TestData.WorkdayMetrics(5, memPressurePct: 3)).Recommendations, x => x.Code == "RAM_WATCH");
        Assert.Contains("증설할 수 없습니다", rec.Detail);
        Assert.Contains("PC 로 교체", rec.Detail);
        Assert.DoesNotContain("증설을 검토", rec.Detail);

        var slotted = Assert.Single(Analyze(TestData.Inventory(), TestData.WorkdayMetrics(5, memPressurePct: 3)).Recommendations, x => x.Code == "RAM_WATCH");
        Assert.Contains("32GB 증설을 검토", slotted.Detail);
    }

    /// <summary>평일 5일 × 7시간. 첫날 60분은 메모리 부족(하드 폴트 300/s), 둘째 날 105분은 여유 18%(부족은 아님).</summary>
    private static List<MetricMinute> OneBadDay()
    {
        var metrics = TestData.WorkdayMetrics(5);
        for (int i = 0; i < metrics.Count; i++)
        {
            var m = metrics[i];
            m.HardFaultsAvg = 5;
            int day = i / 420, minute = i % 420;
            if (day == 0 && minute < 60)
            {
                m.MemPressureSeconds = 60;
                m.MemAvailAvg = 8;
                m.HardFaultsAvg = 300;
            }
            else if (day == 1 && minute < 105)
            {
                m.MemAvailAvg = 18;
            }
        }
        return metrics;
    }

    [Fact]
    public void Pressure_is_converted_to_time_days_and_distribution()
    {
        var r = Analyze(TestData.Inventory(), OneBadDay());
        var st = r.Stats;

        Assert.Equal(2.9, st.MemPressurePct);            // 60분 ÷ 2100분
        Assert.Equal(60, st.MemPressureMinutes);
        Assert.Equal(14, UsageStats.MinutesPer8h(st.MemPressurePct));
        Assert.Equal(5, st.BasisDays);
        Assert.Equal(1, st.MemPressureDays);
        Assert.Equal("2026-11-09", st.MemWorstDay);
        Assert.Equal(60, st.MemWorstDayMinutes);
        Assert.Equal(7.9, st.MemTight20Pct);             // (60 + 105)분
        Assert.Equal(2.9, st.MemTight15Pct);
        Assert.Equal(300, st.HardFaultsPressureMedian);
        Assert.Equal(5, st.HardFaultsCalmMedian);
        Assert.Equal(60, UpgradeAdvisor.HardFaultRatio(st));

        var rec = Assert.Single(r.Recommendations, x => x.Code == "RAM_WATCH");
        Assert.Contains("하루 약 14분", rec.Detail);
        Assert.Contains("평소의 약 60배", rec.Detail);

        Assert.Contains(r.Evidence, e => e.StartsWith("메모리 부족:") && e.Contains("하루 약 14분") && e.Contains("5일 가운데 1일") && e.Contains("(11/9)은 60분"));
        Assert.Contains(r.Evidence, e => e.StartsWith("체감 지연:"));
        Assert.Contains(r.Caveats, c => c.Contains("하루(11/9)에 몰려"));    // 하루에 몰린 결과는 반복성을 뒷받침하지 못함
        Assert.Contains(r.Caveats, c => c.Contains("권장(10일"));            // 측정 기간이 2주 미만
        Assert.Equal("Warn", Status(r, "메모리 여유 (측정)"));
        Assert.StartsWith("교체·증설을 확정하기 전에 확인할 항목이 1건", r.Headline);
    }

    [Fact]
    public void Minutes_per_day_keeps_one_decimal_only_for_small_values()
    {
        Assert.Equal(0, UsageStats.MinutesPer8h(0));
        Assert.Equal(2.4, UsageStats.MinutesPer8h(0.5));
        Assert.Equal(96, UsageStats.MinutesPer8h(20));
    }

    [Fact]
    public void Same_program_recorded_under_two_paths_is_counted_once_per_event()
    {
        const long Mb = 1048576;
        var events = Enumerable.Range(0, 3).Select(i => new PressureRecord
        {
            Id = Guid.NewGuid(),
            Kind = PressureKind.Memory,
            StartedUtc = TestData.Monday.AddHours(11 + i),
            Processes =
            [
                new ProcessUsage { Name = "tool", Path = @"C:\A\tool.exe", Instances = 2, PrivateBytes = 3000 * Mb },
                new ProcessUsage { Name = "tool", Path = @"C:\B\tool.exe", Instances = 1, PrivateBytes = 2000 * Mb },
                new ProcessUsage { Name = "other", Instances = 1, PrivateBytes = 4000 * Mb },
            ],
        }).ToList();

        var top = Analyze(TestData.Inventory(), events: events).MemoryOffenders[0];

        Assert.Equal("tool", top.Name);          // 합치면 5000MB 로 1위(따로 세면 other 가 1위)
        Assert.Equal(3, top.Events);             // 이벤트 수를 넘지 않음
        Assert.Equal(3, top.Rank1);
        Assert.Equal(5000, top.AvgPrivateMb);
        Assert.Equal(@"C:\A\tool.exe", top.Path);
    }

    [Fact]
    public void Detail_metrics_add_findings_and_stay_silent_for_older_data()
    {
        var metrics = TestData.WorkdayMetrics(5);
        foreach (var m in metrics)
        {
            m.CommitGbMax = 20;        // 설치 16GB 초과
            m.CpuPerfAvg = 55;         // CPU 30% 사용 중 속도 55%
            m.DiskLatencyAvg = 80;     // SSD 인데 80ms
            m.BatterySeconds = 30;
        }
        var inv = TestData.Inventory();
        inv.Batteries.Add(new BatteryInfo { DesignedCapacity = 50000, FullChargedCapacity = 45000 });

        var r = Analyze(inv, metrics);

        Assert.Equal(20, r.Stats.CommitGbP95);
        Assert.Equal(55, r.Stats.CpuPerfUnderLoad);
        Assert.Equal(80, r.Stats.DiskLatencyP95Ms);
        Assert.Equal(50, r.Stats.OnBatteryPct);
        Assert.Contains(r.Findings, f => f.Contains("커밋") && f.Contains("20GB"));
        Assert.Contains(r.Findings, f => f.Contains("정격의 55%") && f.Contains("배터리로 쓴 시간이 50%"));
        Assert.Contains(r.Findings, f => f.Contains("SSD 인데 디스크 응답 시간"));
        Assert.Equal("Warn", Status(r, "CPU 여유 (측정)"));
        Assert.Contains(r.Evidence, e => e.StartsWith("필요 메모리:") && e.Contains("125%"));

        // 이전 버전이 수집한 데이터(새 지표 없음)로는 관련 통계·안내가 나오지 않음
        var old = Analyze(TestData.Inventory());
        Assert.Null(old.Stats.CommitGbP95);
        Assert.Null(old.Stats.CpuPerfUnderLoad);
        Assert.Null(old.Stats.DiskLatencyAvgMs);
        Assert.Null(old.Stats.OnBatteryPct);
        Assert.Empty(old.Findings);
    }

    // ---------------- 제출용 요약 ----------------

    [Fact]
    public void Healthy_pc_summary_says_no_problem()
    {
        var r = Analyze(TestData.Inventory());

        Assert.Equal("현재 사양으로 업무에 쓰는 데 문제가 없습니다.", r.Headline);
        Assert.Contains("거의 없었고", Assert.Single(r.Evidence));
        Assert.All(r.Checklist.Where(c => c.Item != "저장장치 상태"), c => Assert.Equal("Ok", c.Status));
        Assert.Equal("Unknown", Status(r, "저장장치 상태"));   // SMART 를 읽지 못한 경우
        Assert.DoesNotContain(r.Checklist, c => c.Item == "배터리");   // 데스크톱
    }

    [Fact]
    public void Old_windows10_hdd_pc_summary_lists_facts_first()
    {
        var inv = TestData.Inventory(win11: false, media: "HDD", cpu: "Intel(R) Core(TM) i5-6500 CPU @ 3.20GHz");
        inv.EstimatedAgeYears = 8;

        var r = Analyze(inv);

        Assert.StartsWith("PC 교체를 권장합니다.", r.Headline);
        Assert.Equal("Bad", Status(r, "Windows 11"));
        Assert.Equal("Bad", Status(r, "저장장치 종류"));
        Assert.Equal("Warn", Status(r, "사용 연수"));
        Assert.Equal("Ok", Status(r, "메모리 여유 (측정)"));
        Assert.Contains(r.Evidence, e => e.StartsWith("Windows 11:") && e.Contains("미지원 CPU"));
        Assert.Contains(r.Evidence, e => e.StartsWith("저장장치:"));
        Assert.Contains(r.Evidence, e => e.StartsWith("사용 연수: 약 8년"));
    }

    [Fact]
    public void Summary_without_enough_data_marks_measured_items_unknown()
    {
        var r = Analyze(TestData.Inventory(), metrics: []);

        Assert.StartsWith("측정 기간이 짧아", r.Headline);
        Assert.Equal("Unknown", Status(r, "메모리 여유 (측정)"));
        Assert.Equal("Unknown", Status(r, "CPU 여유 (측정)"));
        Assert.Contains("성능 항목은 판정하지 않았습니다", Assert.Single(r.Caveats));
    }

    [Fact]
    public void Soldered_ram_is_called_out_only_when_memory_is_tight()
    {
        var inv = TestData.Inventory();
        inv.RamModules[0].FormFactor = "Row of chips";

        Assert.DoesNotContain(Analyze(inv).Evidence, e => e.StartsWith("증설 가능 여부:"));
        Assert.Contains(Analyze(inv, OneBadDay()).Evidence, e => e.StartsWith("증설 가능 여부:") && e.Contains("증설할 수 없습니다"));
        Assert.Contains("증설 불가", Analyze(inv).Checklist.Single(c => c.Item == "메모리 용량").Note);
    }

    [Fact]
    public void Many_core_pc_gets_a_caveat_about_single_program_slowness()
    {
        var many = TestData.Inventory();               // 12 논리 프로세서
        Assert.Contains(Analyze(many).Caveats, c => c.Contains("논리 프로세서가 12개") && c.Contains("약 8%"));

        var few = TestData.Inventory();
        few.CpuLogicalProcessors = 4;
        Assert.DoesNotContain(Analyze(few).Caveats, c => c.Contains("논리 프로세서"));
    }

    [Fact]
    public void Report_starts_with_summary_and_skips_it_for_results_from_older_versions()
    {
        var inv = TestData.Inventory();
        inv.Hostname = "PC-<x>";
        var summary = new ExportSummary
        {
            AgentVersion = "0.5.0",
            GeneratedUtc = TestData.Monday.AddDays(7),
            FromUtc = TestData.Monday,
            ToUtc = TestData.Monday.AddDays(7),
            TimeZone = "UTC",
            Inventory = inv,
            Analysis = Analyze(inv, OneBadDay()),
        };
        var html = new StringWriter();
        HtmlReport.Write(html, summary, [], [], TimeZoneInfo.Utc);
        string text = html.ToString();

        Assert.Contains("<section class=\"summary\">", text);
        Assert.Contains("판단 근거", text);
        Assert.Contains("점검표", text);
        Assert.Contains("하루 약 14분", text);
        Assert.Contains("PC-&lt;x&gt;", text);   // 요약도 HTML 인코딩
        Assert.True(text.IndexOf("<section class=\"summary\">", StringComparison.Ordinal) < text.IndexOf("<h2>권장 조치</h2>", StringComparison.Ordinal));

        // v0.4.0 이하가 만든 결과(요약 항목 없음)를 취합 등에서 다시 그릴 때
        var old = new ExportSummary { Inventory = inv, Analysis = new AnalysisResult { Verdict = "Ok", VerdictLabel = "양호" } };
        var oldHtml = new StringWriter();
        HtmlReport.Write(oldHtml, old, [], [], TimeZoneInfo.Utc);
        Assert.DoesNotContain("<section class=\"summary\">", oldHtml.ToString());
    }
}
