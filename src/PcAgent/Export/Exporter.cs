using System.IO.Compression;
using System.Text;
using PcAgent.Analysis;
using PcAgent.Collection;
using PcAgent.EventLogs;
using PcAgent.Hosting;
using PcAgent.Inventory;
using PcAgent.Privacy;
using PcAgent.Storage;

namespace PcAgent.Export;

public sealed class ExportSummary
{
    public string AgentVersion { get; set; } = "";
    public DateTimeOffset GeneratedUtc { get; set; }
    public int PeriodDays { get; set; }
    public DateTimeOffset FromUtc { get; set; }
    public DateTimeOffset ToUtc { get; set; }
    public string TimeZone { get; set; } = "";
    public InventoryInfo Inventory { get; set; } = new();
    public AnalysisResult Analysis { get; set; } = new();
    /// <summary>에이전트 자체 자원 사용량(서비스 기록). 없으면 null.</summary>
    public AgentUsageInfo? AgentUsage { get; set; }
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// 로컬 데이터를 zip 으로 내보냅니다. 서버 전송 기능은 없습니다.
/// </summary>
internal static class Exporter
{
    internal static readonly string[] SummaryHeader =
    [
        "PC명", "내보낸시각", "기간(일)", "판정", "점수", "관측시간(h)", "판정기준시간(h)",
        "CPU평균(%)", "CPU포화비율(%)", "메모리부족비율(%)", "디스크포화비율(%)",
        "CPU이벤트", "메모리이벤트", "디스크이벤트", "권장조치",
        "제조사", "모델", "시리얼", "CPU", "코어", "RAM(GB)", "RAM슬롯(사용/전체)", "시스템디스크", "시스템드라이브여유(GB)",
        "OS", "OS빌드", "Win11", "TPM", "BIOS날짜", "추정연식(년)",
        "블루스크린", "비정상종료", "앱크래시", "평균부팅(초)", "메모리부족주원인", "CPU포화주원인",
        "배터리수명(%)", "에이전트버전", "에이전트CPU(%)", "에이전트메모리(MB)",
        "디스크상태", "SSD수명사용(%)", "디스크사용시간(h)",
        // v0.5.0 (이전 버전이 내보낸 파일은 측정값이 없는 칸을 비워 둠)
        "메모리부족(분/8시간)", "메모리여유20%이하(%)", "필요메모리상위5%(GB)", "디스크응답상위5%(ms)", "CPU속도_부하시(%)", "측정일수", "결론",
    ];

    public static string Run(AgentPaths paths, AgentConfig config, int days, string outDir, Action<string> progress)
    {
        var toUtc = DateTimeOffset.UtcNow;
        var fromUtc = toUtc.AddDays(-days);
        var tz = TimeZoneInfo.Local;
        var warnings = new List<string>();

        if (!Directory.Exists(paths.DataDir))
            warnings.Add($"데이터 폴더가 없습니다: {paths.DataDir} — 에이전트가 설치·실행 중인지 확인하세요.");

        progress("수집 데이터 읽는 중...");
        var store = new DataStore(paths.DataDir);
        var metrics = store.ReadMetrics(fromUtc, toUtc);
        var events = store.ReadPressureEvents(fromUtc, toUtc);
        var masker = ProcessMasker.FromConfig(config);
        Remask(events, masker);

        progress("하드웨어 정보 수집 중...");
        var inventory = InventoryCollector.Collect(warnings);

        progress("이벤트 로그 조회 중...");
        var logs = EventLogCollector.Collect(fromUtc, masker, warnings);
        // 일반 권한으로 내보내면 못 읽는 부팅 시간·디스크 SMART 는 서비스가 기록해 둔 값으로 채움
        PrivilegedCache.ApplyTo(PrivilegedCache.Read(paths.Root), inventory, logs, warnings, fromUtc);

        progress("분석 중...");
        var analysis = UpgradeAdvisor.Analyze(new AnalysisInput
        {
            Inventory = inventory,
            Metrics = metrics,
            Events = events,
            Logs = logs,
            Config = config,
            TimeZone = tz,
        });

        var summary = new ExportSummary
        {
            AgentVersion = AppInfo.Version,
            GeneratedUtc = toUtc,
            PeriodDays = days,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            TimeZone = tz.Id,
            Inventory = inventory,
            Analysis = analysis,
            AgentUsage = AgentUsage.Read(paths.Root),
            Warnings = warnings,
        };

        Directory.CreateDirectory(outDir);
        var file = Path.Combine(outDir, $"PcHealth_{SafeName(inventory.Hostname)}_{DateTime.Now:yyyyMMdd_HHmm}.zip");
        if (File.Exists(file)) File.Delete(file);

        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            Entry(zip, "report.html", false, w => HtmlReport.Write(w, summary, events, logs, tz));
            Entry(zip, "summary.csv", true, w => WriteSummary(new CsvWriter(w), summary, tz));
            Entry(zip, "summary.json", false, w => w.Write(Json.Serialize(summary, AgentJsonContext.Default.ExportSummary, indented: true)));
            Entry(zip, "metrics_minute.csv", true, w => WriteMetrics(new CsvWriter(w), metrics, tz));
            Entry(zip, "pressure_events.csv", true, w => WriteEvents(new CsvWriter(w), events, tz));
            Entry(zip, "pressure_processes.csv", true, w => WriteProcesses(new CsvWriter(w), events, tz));
            Entry(zip, "event_logs.csv", true, w => WriteLogs(new CsvWriter(w), logs, tz));
            Entry(zip, "README.txt", true, w => w.Write(ReadmeText));
        }
        return file;
    }

    /// <summary>예전에 저장된 데이터에도 현재 마스킹 설정을 적용합니다.</summary>
    private static void Remask(List<PressureRecord> events, ProcessMasker masker)
    {
        foreach (var e in events)
        {
            if (e.Processes is null) continue;
            foreach (var p in e.Processes)
            {
                if (ProcessMasker.IsMaskedToken(p.Name)) continue;
                var (name, path, masked) = masker.Apply(p.Name, p.Path);
                p.Name = name;
                p.Path = path;
                if (masked) p.Publisher = null;
            }
        }
    }

    private static void Entry(ZipArchive zip, string name, bool bom, Action<TextWriter> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(bom));
        write(writer);
    }

    private static void WriteSummary(CsvWriter csv, ExportSummary s, TimeZoneInfo tz)
    {
        csv.Row(SummaryHeader);
        WriteSummaryRow(csv, s, tz);
    }

    /// <summary>PC 1대 요약 행(SummaryHeader 순서). merge 에서도 사용합니다.</summary>
    internal static void WriteSummaryRow(CsvWriter csv, ExportSummary s, TimeZoneInfo tz)
    {
        var a = s.Analysis;
        var inv = s.Inventory;
        var sysDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        var sysVol = inv.Volumes.FirstOrDefault(v => v.IsSystem);
        bool hasSummary = a.Headline.Length > 0;   // v0.5.0 이후가 내보낸 파일
        csv.Row(
            inv.Hostname,
            Local(s.GeneratedUtc, tz).ToString("yyyy-MM-dd HH:mm"),
            s.PeriodDays,
            a.VerdictLabel,
            a.Score,
            a.Stats.ObservedHours,
            a.Stats.BasisHours,
            a.Stats.CpuAvg,
            a.Stats.CpuHighPct,
            a.Stats.MemPressurePct,
            a.Stats.DiskBusyPct,
            a.Stats.CpuEvents,
            a.Stats.MemoryEvents,
            a.Stats.DiskEvents,
            string.Join("; ", a.Recommendations.Select(r => $"[{PriorityLabel(r.Priority)}] {r.Title}")),
            inv.Manufacturer,
            inv.Model,
            inv.SerialNumber,
            inv.CpuName,
            inv.CpuCores,
            Math.Round(inv.RamInstalledMb / 1024.0, 1),
            inv.RamSlotsLabel,
            sysDisk is null ? null : $"{sysDisk.MediaType} {sysDisk.SizeGb:0}GB ({sysDisk.BusType})",
            sysVol?.FreeGb,
            inv.OsName,
            $"{inv.OsBuild}.{inv.OsUbr}",
            Win11Label(a.Win11.Status),
            inv.TpmVersion ?? "확인불가",
            inv.BiosDate,
            inv.EstimatedAgeYears,
            a.Stability.BugChecks,
            a.Stability.UnexpectedShutdowns,
            a.Stability.AppCrashes,
            a.Stability.BootAvgSec,
            a.MemoryOffenders.FirstOrDefault()?.Name,
            a.CpuOffenders.FirstOrDefault()?.Name,
            inv.BatteryHealthPercent,
            s.AgentVersion,
            s.AgentUsage?.CpuAvgPercent?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),   // 0.00x% 단위라 기본 2자리면 0 으로 보임
            s.AgentUsage?.PrivateMb,
            sysDisk?.Health is { } dh ? DiskHealth.LevelLabel(dh.Level) : "확인 불가",
            sysDisk?.Health?.PercentageUsed,
            sysDisk?.Health?.PowerOnHours,
            UsageStats.MinutesPer8h(a.Stats.MemPressurePct),
            hasSummary ? a.Stats.MemTight20Pct : (double?)null,
            a.Stats.CommitGbP95,
            a.Stats.DiskLatencyP95Ms,
            a.Stats.CpuPerfUnderLoad,
            hasSummary ? a.Stats.BasisDays : (int?)null,
            a.Headline);
    }

    private static void WriteMetrics(CsvWriter csv, List<MetricMinute> metrics, TimeZoneInfo tz)
    {
        csv.Row("local_time", "utc_time", "samples", "covered_sec", "cpu_avg", "cpu_max", "mem_avail_avg", "mem_avail_min",
            "commit_avg", "commit_max", "hard_faults_avg", "disk_active_avg", "disk_active_max", "disk_queue_avg",
            "cpu_high_sec", "mem_pressure_sec", "disk_busy_sec",
            "commit_gb_max", "cpu_perf_avg", "disk_latency_ms_avg", "disk_latency_ms_max", "battery_sec");
        foreach (var m in metrics)
        {
            csv.Row(Local(m.MinuteUtc, tz).ToString("yyyy-MM-dd HH:mm"), m.MinuteUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                m.Samples, m.CoveredSeconds, m.CpuAvg, m.CpuMax, m.MemAvailAvg, m.MemAvailMin, m.CommitAvg, m.CommitMax,
                m.HardFaultsAvg, m.DiskActiveAvg, m.DiskActiveMax, m.DiskQueueAvg,
                m.CpuHighSeconds, m.MemPressureSeconds, m.DiskBusySeconds,
                m.CommitGbMax, m.CpuPerfAvg, m.DiskLatencyAvg, m.DiskLatencyMax, m.BatterySeconds);
        }
    }

    private static void WriteEvents(CsvWriter csv, List<PressureRecord> events, TimeZoneInfo tz)
    {
        csv.Row("event_id", "kind", "started_local", "ended_local", "duration_sec", "peak", "cpu_pct", "mem_avail_pct",
            "commit_pct", "disk_active_pct", "snapshot_skipped", "top1", "top2", "top3");
        foreach (var e in events)
        {
            var top = e.RankedByName().Take(3).Select(p => p.Name).ToList();
            csv.Row(e.Id.ToString("N")[..12], KindLabel(e.Kind),
                Local(e.StartedUtc, tz).ToString("yyyy-MM-dd HH:mm:ss"),
                e.EndedUtc is { } end ? Local(end, tz).ToString("yyyy-MM-dd HH:mm:ss") : null,
                e.EndedUtc is { } end2 ? (int)(end2 - e.StartedUtc).TotalSeconds : null,
                e.Peak, e.CpuPercent, e.MemAvailPercent, e.CommitPercent, e.DiskActivePercent, e.SnapshotSkipped,
                top.ElementAtOrDefault(0), top.ElementAtOrDefault(1), top.ElementAtOrDefault(2));
        }
    }

    private static void WriteProcesses(CsvWriter csv, List<PressureRecord> events, TimeZoneInfo tz)
    {
        csv.Row("event_id", "kind", "started_local", "rank", "name", "publisher", "path", "instances",
            "cpu_pct", "private_mb", "working_set_mb", "io_kbps");
        foreach (var e in events)
        {
            int rank = 0;
            foreach (var p in e.RankedProcesses())
            {
                csv.Row(e.Id.ToString("N")[..12], KindLabel(e.Kind), Local(e.StartedUtc, tz).ToString("yyyy-MM-dd HH:mm:ss"),
                    ++rank, p.Name, p.Publisher, p.Path, p.Instances, p.CpuPercent,
                    Math.Round(p.PrivateBytes / 1048576.0), Math.Round(p.WorkingSetBytes / 1048576.0), Math.Round(p.IoBytesPerSec / 1024.0));
            }
        }
    }

    private static void WriteLogs(CsvWriter csv, List<EventLogItem> logs, TimeZoneInfo tz)
    {
        csv.Row("local_time", "category", "category_label", "provider", "event_id", "level", "detail", "value");
        foreach (var l in logs)
        {
            csv.Row(Local(l.TimeUtc, tz).ToString("yyyy-MM-dd HH:mm:ss"), l.Category, LogCategoryLabel(l.Category),
                l.Provider, l.EventId, l.Level, l.Detail, l.Value);
        }
    }

    internal static DateTimeOffset Local(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz);

    internal static string KindLabel(PressureKind kind) => kind switch
    {
        PressureKind.Cpu => "CPU 포화",
        PressureKind.Memory => "메모리 부족",
        PressureKind.Disk => "디스크 포화",
        _ => kind.ToString(),
    };

    internal static string PriorityLabel(string priority) => priority switch
    {
        "High" => "높음",
        "Medium" => "보통",
        _ => "낮음",
    };

    internal static string Win11Label(string status) => status switch
    {
        "AlreadyWindows11" => "사용 중",
        "Ready" => "업그레이드 가능",
        "NeedsConfig" => "설정 변경 후 가능",
        "NotReady" => "미지원",
        _ => "확인 필요",
    };

    internal static string LogCategoryLabel(string category) => category switch
    {
        "AppCrash" => "앱 비정상 종료",
        "AppHang" => "앱 응답 없음",
        "BugCheck" => "블루스크린",
        "UnexpectedShutdown" => "비정상 종료(전원)",
        "LowMemory" => "메모리 부족 경고",
        "Boot" => "부팅",
        _ => category,
    };

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private const string ReadmeText = """
        PC Health Agent 내보내기 파일

        report.html            사람이 보는 진단 리포트 (브라우저로 열기). 맨 위 '요약'이 제출용 1장 — 인쇄하면 첫 장에 들어갑니다
        summary.csv            PC 1대 요약 1행 — 여러 PC 결과를 합칠 때 사용 (PcAgent.exe merge)
        summary.json           요약 전체(사양, 판정, 통계)
        metrics_minute.csv     1분 단위 CPU/메모리/디스크 지표 (커밋 용량·CPU 속도·디스크 응답 시간·배터리 사용 포함)
        pressure_events.csv    자원 부족(CPU 포화, 메모리 부족, 디스크 포화) 이벤트
        pressure_processes.csv 자원 부족 시점의 상위 프로세스
        event_logs.csv         블루스크린, 비정상 종료, 앱 크래시, 부팅 시간 등 Windows 이벤트

        수집하지 않는 정보: 창 제목, 문서/파일 이름, 웹 주소(URL), 키 입력, 화면, 명령줄 인수
        경로의 사용자 계정 폴더는 %USERPROFILE% 로, 마스킹 대상 프로세스명은 [masked-xxxxxx] 로 저장됩니다.
        """;
}
