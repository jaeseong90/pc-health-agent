using PcAgent.Collection;
using PcAgent.EventLogs;
using PcAgent.Inventory;

namespace PcAgent.Analysis;

public sealed class AnalysisInput
{
    public required InventoryInfo Inventory { get; init; }
    public required IReadOnlyList<MetricMinute> Metrics { get; init; }
    public required IReadOnlyList<PressureRecord> Events { get; init; }
    public required IReadOnlyList<EventLogItem> Logs { get; init; }
    public required AgentConfig Config { get; init; }
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
}

public sealed class AnalysisResult
{
    /// <summary>Ok, Watch, Upgrade, Replace, InsufficientData</summary>
    public string Verdict { get; set; } = "";
    public string VerdictLabel { get; set; } = "";
    public int Score { get; set; }
    public string Basis { get; set; } = "";
    public bool SufficientData { get; set; }
    public UsageStats Stats { get; set; } = new();
    public List<Recommendation> Recommendations { get; set; } = [];
    public List<string> Findings { get; set; } = [];

    // ---- 제출용 요약 (v0.5.0, 이전 버전의 summary.json 에는 없음) ----
    /// <summary>결론 한 줄(비전문가용).</summary>
    public string Headline { get; set; } = "";
    /// <summary>판단 근거를 시간·횟수로 풀어 쓴 문장들.</summary>
    public List<string> Evidence { get; set; } = [];
    /// <summary>항목별 점검표(사실 → 정상/주의/문제).</summary>
    public List<CheckItem> Checklist { get; set; } = [];
    /// <summary>결과를 읽을 때 주의할 점(측정 기간 부족 등).</summary>
    public List<string> Caveats { get; set; } = [];
    public Win11Readiness Win11 { get; set; } = new();
    public List<Offender> CpuOffenders { get; set; } = [];
    public List<Offender> MemoryOffenders { get; set; } = [];
    public List<Offender> DiskOffenders { get; set; } = [];
    public List<DailyStat> Daily { get; set; } = [];
    public StabilityStats Stability { get; set; } = new();
}

public sealed class UsageStats
{
    public double ObservedHours { get; set; }
    public double BasisHours { get; set; }
    public double CpuAvg { get; set; }
    public double CpuP95 { get; set; }
    public double CpuHighPct { get; set; }
    public double MemAvailAvg { get; set; }
    public double MemAvailP5 { get; set; }
    public double CommitP95 { get; set; }
    public double MemPressurePct { get; set; }
    public double? DiskActiveAvg { get; set; }
    public double DiskBusyPct { get; set; }
    public double? HardFaultsAvg { get; set; }
    public double? HardFaultsP95 { get; set; }
    public int CpuEvents { get; set; }
    public int MemoryEvents { get; set; }
    public int DiskEvents { get; set; }

    // ---- 시간 환산·분포 (v0.5.0) ----
    /// <summary>판정 기준 시간 중 1시간 이상 관측된 날 수.</summary>
    public int BasisDays { get; set; }
    /// <summary>메모리 부족이 5분 이상이었던 날 수.</summary>
    public int MemPressureDays { get; set; }
    /// <summary>메모리 부족이 가장 길었던 날(yyyy-MM-dd)과 그날의 부족 시간(분).</summary>
    public string? MemWorstDay { get; set; }
    public double MemWorstDayMinutes { get; set; }
    /// <summary>판정 기준 시간 전체의 메모리 부족 시간(분).</summary>
    public double MemPressureMinutes { get; set; }
    /// <summary>가용 메모리가 20% / 15% 이하였던 시간 비율(%). 부족 기준(기본 10%) 직전의 '빠듯한' 구간.</summary>
    public double MemTight20Pct { get; set; }
    public double MemTight15Pct { get; set; }
    /// <summary>하드 페이지 폴트 중앙값(회/초): 메모리 부족이던 분 / 아니던 분. 부족이 실제 지연으로 이어지는지 확인용.</summary>
    public double? HardFaultsPressureMedian { get; set; }
    public double? HardFaultsCalmMedian { get; set; }
    /// <summary>프로그램들이 요구한 메모리(커밋, GB). 설치 메모리와 비교해 적정 용량을 판단.</summary>
    public double? CommitGbP95 { get; set; }
    public double? CommitGbMax { get; set; }
    /// <summary>CPU 를 20% 이상 쓰던 시간의 CPU 속도(정격 대비 %). 낮으면 발열·전원 설정으로 제한된 상태.</summary>
    public double? CpuPerfUnderLoad { get; set; }
    /// <summary>시스템 디스크 응답 시간(ms).</summary>
    public double? DiskLatencyAvgMs { get; set; }
    public double? DiskLatencyP95Ms { get; set; }
    /// <summary>배터리로 사용한 시간 비율(%).</summary>
    public double? OnBatteryPct { get; set; }

    /// <summary>비율(%)을 '8시간 사용 기준 하루 몇 분'으로 환산.</summary>
    public static double MinutesPer8h(double pct) => Math.Round(pct * 4.8, pct * 4.8 < 10 ? 1 : 0);
}

public sealed class CheckItem
{
    public string Item { get; set; } = "";
    /// <summary>Ok, Warn, Bad, Unknown</summary>
    public string Status { get; set; } = "Unknown";
    public string Value { get; set; } = "";
    public string? Note { get; set; }
}

public sealed class Recommendation
{
    /// <summary>RAM_UPGRADE, SSD_REPLACE, PC_REPLACE, OS_UPGRADE, DISK_CLEANUP, SOFTWARE_REVIEW, CPU_REVIEW, STABILITY_CHECK, WIN11_CHECK, RAM_WATCH, BATTERY_REPLACE, DISK_REPLACE, DISK_CHECK</summary>
    public string Code { get; set; } = "";
    /// <summary>High, Medium, Low</summary>
    public string Priority { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class Offender
{
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public string? Publisher { get; set; }
    /// <summary>해당 유형 이벤트에서 상위 5위 안에 든 횟수.</summary>
    public int Events { get; set; }
    public int Rank1 { get; set; }
    public int Top3 { get; set; }
    public double AvgCpu { get; set; }
    public double AvgPrivateMb { get; set; }
    public double AvgIoKBps { get; set; }
}

public sealed class DailyStat
{
    public string Date { get; set; } = "";
    public string Weekday { get; set; } = "";
    public double ObservedHours { get; set; }
    public double CpuAvg { get; set; }
    public double CpuHighPct { get; set; }
    public double MemPressurePct { get; set; }
    public double DiskBusyPct { get; set; }
    public int Events { get; set; }
}

public sealed class StabilityStats
{
    public int AppCrashes { get; set; }
    public int AppHangs { get; set; }
    public int BugChecks { get; set; }
    public int UnexpectedShutdowns { get; set; }
    public int LowMemoryWarnings { get; set; }
    public int BootCount { get; set; }
    public double? BootAvgSec { get; set; }
    public List<NameCount> TopCrashApps { get; set; } = [];
}

public sealed class NameCount
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
}
