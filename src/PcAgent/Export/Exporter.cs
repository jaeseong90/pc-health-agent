using System.IO.Compression;
using System.Text;
using PcAgent.Analysis;
using PcAgent.Collection;
using PcAgent.EventLogs;
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
        var a = s.Analysis;
        var inv = s.Inventory;
        var sysDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        var sysVol = inv.Volumes.FirstOrDefault(v => v.IsSystem);
        csv.Row(SummaryHeader);
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
            a.CpuOffenders.FirstOrDefault()?.Name);
    }

    private static void WriteMetrics(CsvWriter csv, List<MetricMinute> metrics, TimeZoneInfo tz)
    {
        csv.Row("local_time", "utc_time", "samples", "covered_sec", "cpu_avg", "cpu_max", "mem_avail_avg", "mem_avail_min",
            "commit_avg", "commit_max", "hard_faults_avg", "disk_active_avg", "disk_active_max", "disk_queue_avg",
            "cpu_high_sec", "mem_pressure_sec", "disk_busy_sec");
        foreach (var m in metrics)
        {
            csv.Row(Local(m.MinuteUtc, tz).ToString("yyyy-MM-dd HH:mm"), m.MinuteUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                m.Samples, m.CoveredSeconds, m.CpuAvg, m.CpuMax, m.MemAvailAvg, m.MemAvailMin, m.CommitAvg, m.CommitMax,
                m.HardFaultsAvg, m.DiskActiveAvg, m.DiskActiveMax, m.DiskQueueAvg,
                m.CpuHighSeconds, m.MemPressureSeconds, m.DiskBusySeconds);
        }
    }

    private static void WriteEvents(CsvWriter csv, List<PressureRecord> events, TimeZoneInfo tz)
    {
        csv.Row("event_id", "kind", "started_local", "ended_local", "duration_sec", "peak", "cpu_pct", "mem_avail_pct",
            "commit_pct", "disk_active_pct", "snapshot_skipped", "top1", "top2", "top3");
        foreach (var e in events)
        {
            var top = RankProcesses(e).Take(3).Select(p => p.Name).ToList();
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
            foreach (var p in RankProcesses(e))
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

    /// <summary>이벤트 유형별 기준(CPU%·메모리·I/O)으로 정렬한 프로세스 목록.</summary>
    internal static IEnumerable<ProcessUsage> RankProcesses(PressureRecord e) =>
        e.Processes is null ? [] : e.Kind switch
        {
            PressureKind.Memory => e.Processes.OrderByDescending(p => p.PrivateBytes),
            PressureKind.Disk => e.Processes.OrderByDescending(p => p.IoBytesPerSec),
            _ => e.Processes.OrderByDescending(p => p.CpuPercent),
        };

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

        report.html            사람이 보는 진단 리포트 (브라우저로 열기)
        summary.csv            PC 1대 요약 1행 — 여러 PC 결과를 합칠 때 사용 (PcAgent.exe merge)
        summary.json           요약 전체(사양, 판정, 통계)
        metrics_minute.csv     1분 단위 CPU/메모리/디스크 지표
        pressure_events.csv    자원 부족(CPU 포화, 메모리 부족, 디스크 포화) 이벤트
        pressure_processes.csv 자원 부족 시점의 상위 프로세스
        event_logs.csv         블루스크린, 비정상 종료, 앱 크래시, 부팅 시간 등 Windows 이벤트

        수집하지 않는 정보: 창 제목, 문서/파일 이름, 웹 주소(URL), 키 입력, 화면, 명령줄 인수
        경로의 사용자 계정 폴더는 %USERPROFILE% 로, 마스킹 대상 프로세스명은 [masked-xxxxxx] 로 저장됩니다.
        """;
}
