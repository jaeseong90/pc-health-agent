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
