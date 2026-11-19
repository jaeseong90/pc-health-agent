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
    double? DiskQueueLength,
    // 커밋 사용량(GB) — 프로그램들이 요구한 메모리 총량. 설치 메모리와 비교해 적정 용량을 판단
    double? CommitGb = null,
    // CPU 속도(정격 대비 %). 터보면 100 초과, 발열·전원 제한이면 100 미만. 30초에 한 번만 측정(그 사이 샘플은 null)
    double? CpuPerfPercent = null,
    // 시스템 디스크의 I/O 1건당 평균 응답 시간(ms). I/O 가 없던 구간은 0
    double? DiskLatencyMs = null,
    // 배터리로 동작 중인지(데스크톱은 false, 알 수 없으면 null)
    bool? OnBattery = null);

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

    // ---- v0.5.0 부터 기록(이전 데이터는 null) ----
    /// <summary>커밋 사용량 최대(GB).</summary>
    public double? CommitGbMax { get; set; }
    /// <summary>CPU 속도(정격 대비 %) — 명령을 실행한 동안의 평균 속도. 한가한 분에는 절전으로 낮게 나오는 것이 정상.</summary>
    public double? CpuPerfAvg { get; set; }
    /// <summary>시스템 디스크 응답 시간(ms). I/O 가 있었던 샘플만 평균.</summary>
    public double? DiskLatencyAvg { get; set; }
    public double? DiskLatencyMax { get; set; }
    /// <summary>배터리로 동작한 시간(초).</summary>
    public int? BatterySeconds { get; set; }
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

    /// <summary>이벤트 유형별 기준(CPU%·메모리·I/O)으로 정렬한 프로세스 목록(기록된 그대로, 경로별).</summary>
    public IEnumerable<ProcessUsage> RankedProcesses() =>
        Processes is null ? [] : Processes.OrderByDescending(Metric);

    /// <summary>
    /// 이름 기준 순위. 스냅샷은 (이름, 경로)별로 기록되므로, 같은 이름이 설치 위치만 달라 두 줄이면 먼저 합칩니다.
    /// 합치지 않으면 한 프로그램이 한 이벤트에서 두 번 세어지고 순위도 갈립니다. 원인 프로그램 집계와 화면 표시는 이 순위를 씁니다.
    /// </summary>
    public IEnumerable<ProcessUsage> RankedByName()
    {
        if (Processes is null) return [];
        return Processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var main = g.MaxBy(Metric)!;
                return g.Count() == 1 ? main : new ProcessUsage
                {
                    Name = main.Name,
                    Path = main.Path,
                    Publisher = main.Publisher,
                    Instances = g.Sum(p => p.Instances),
                    CpuPercent = g.Sum(p => p.CpuPercent),
                    PrivateBytes = g.Sum(p => p.PrivateBytes),
                    WorkingSetBytes = g.Sum(p => p.WorkingSetBytes),
                    IoBytesPerSec = g.Sum(p => p.IoBytesPerSec),
                };
            })
            .OrderByDescending(Metric);
    }

    private double Metric(ProcessUsage p) => Kind switch
    {
        PressureKind.Memory => p.PrivateBytes,
        PressureKind.Disk => p.IoBytesPerSec,
        _ => p.CpuPercent,
    };
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
