namespace PcAgent.Collection;

public enum PressureKind
{
    Cpu,
    Memory,
    Disk,
}

/// <summary>5초 단위 시스템 샘플.</summary>
public readonly record struct SystemSample(
    DateTimeOffset TimestampUtc,
    double CpuPercent,
    double MemAvailPercent,
    double CommitPercent,
    double? HardFaultsPerSec,
    double? DiskActivePercent,
    double? DiskQueueLength);

/// <summary>1분 집계 지표. data\metrics-yyyyMMdd.csv 에 한 줄씩 저장됩니다.</summary>
public sealed class MetricMinute
{
    public DateTimeOffset MinuteUtc { get; set; }
    public int Samples { get; set; }
    public int CoveredSeconds { get; set; }
    public double CpuAvg { get; set; }
    public double CpuMax { get; set; }
    public double MemAvailAvg { get; set; }
    public double MemAvailMin { get; set; }
    public double CommitAvg { get; set; }
    public double CommitMax { get; set; }
    public double? HardFaultsAvg { get; set; }
    public double? DiskActiveAvg { get; set; }
    public double? DiskActiveMax { get; set; }
    public double? DiskQueueAvg { get; set; }
    /// <summary>CPU 가 임계치 이상이었던 시간(초).</summary>
    public int CpuHighSeconds { get; set; }
    /// <summary>메모리 부족(가용 ≤ 임계 또는 커밋 ≥ 임계) 상태였던 시간(초).</summary>
    public int MemPressureSeconds { get; set; }
    public int DiskBusySeconds { get; set; }
}

/// <summary>
/// 자원 부족 이벤트 기록. 시작 시 "start"(프로세스 스냅샷 포함), 종료 시 "end" 레코드를 같은 Id 로 남깁니다.
/// PC 가 이벤트 도중 꺼져도 시작 기록은 보존됩니다.
/// </summary>
public sealed class PressureRecord
{
    public const string PhaseStart = "start";
    public const string PhaseEnd = "end";

    public Guid Id { get; set; }
    public PressureKind Kind { get; set; }
    public string Phase { get; set; } = PhaseStart;
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    /// <summary>CPU=최대 CPU%, Memory=최대 물리 메모리 사용률%, Disk=최대 디스크 활성%.</summary>
    public double Peak { get; set; }
    public double CpuPercent { get; set; }
    public double MemAvailPercent { get; set; }
    public double CommitPercent { get; set; }
    public double? DiskActivePercent { get; set; }
    public bool SnapshotSkipped { get; set; }
    public List<ProcessUsage>? Processes { get; set; }
}

/// <summary>같은 실행 파일의 인스턴스를 합산한 프로세스 사용량(예: chrome 30개 → 1행).</summary>
public sealed class ProcessUsage
{
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public string? Publisher { get; set; }
    public int Instances { get; set; }
    public double CpuPercent { get; set; }
    public long PrivateBytes { get; set; }
    public long WorkingSetBytes { get; set; }
    public double IoBytesPerSec { get; set; }

    /// <summary>게시자 조회용 원본 경로(저장되지 않음).</summary>
    internal string? RawPath { get; set; }
}
