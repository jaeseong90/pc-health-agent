namespace PcAgent;

/// <summary>
/// 에이전트 설정. %ProgramData%\PcAgent\config.json 이 있으면 덮어쓰고, 없으면 기본값을 사용합니다.
/// </summary>
public sealed class AgentConfig
{
    // ---- 측정 ----
    /// <summary>시스템 지표 샘플링 주기(초). 1분 단위로 집계되어 저장됩니다.</summary>
    public int SampleIntervalSeconds { get; set; } = 5;
    public int RetentionDays { get; set; } = 90;
    public int MaxDataSizeMb { get; set; } = 200;

    // ---- 자원 부족 판정 (진입/해제 기준을 분리해 이벤트 폭주 방지) ----
    public double CpuHighPercent { get; set; } = 90;
    public double CpuRecoverPercent { get; set; } = 70;
    public int CpuSustainSeconds { get; set; } = 30;

    public double MemoryAvailableLowPercent { get; set; } = 10;
    public double MemoryAvailableRecoverPercent { get; set; } = 15;
    public double CommitHighPercent { get; set; } = 90;
    public double CommitRecoverPercent { get; set; } = 85;
    public int MemorySustainSeconds { get; set; } = 30;

    public double DiskActiveHighPercent { get; set; } = 90;
    public double DiskActiveRecoverPercent { get; set; } = 60;
    public int DiskSustainSeconds { get; set; } = 60;

    /// <summary>해제 조건이 이 시간 이상 유지돼야 이벤트가 종료됩니다.</summary>
    public int RecoverSustainSeconds { get; set; } = 30;

    // ---- 자원 부족 시 프로세스 스냅샷 ----
    public int TopProcessCount { get; set; } = 8;
    public int SnapshotCooldownMinutes { get; set; } = 10;
    public int MaxSnapshotsPerDay { get; set; } = 48;

    // ---- 개인정보 ----
    /// <summary>false 면 프로세스 경로를 저장하지 않습니다(이름만 저장).</summary>
    public bool IncludeProcessPaths { get; set; } = true;

    /// <summary>이 패턴(*, ? 와일드카드, 대소문자 무시)에 맞는 프로세스명은 [masked-xxxxxx] 로 저장됩니다.</summary>
    public List<string> MaskProcessPatterns { get; set; } =
    [
        "KakaoTalk*", "Telegram*", "Discord*", "WhatsApp*",
        "Steam*", "EpicGamesLauncher*", "Battle.net*", "RiotClient*", "LeagueClient*",
        "*torrent*",
    ];

    // ---- 리포트 (내보내기 시 판정 기준 시간대) ----
    public int WorkStartHour { get; set; } = 9;
    public int WorkEndHour { get; set; } = 18;
    public bool WorkdaysOnly { get; set; } = true;

    public static AgentConfig Load(string path, Action<string>? warn = null)
    {
        var config = new AgentConfig();
        try
        {
            if (File.Exists(path))
            {
                config = Json.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.AgentConfig) ?? new AgentConfig();
            }
        }
        catch (Exception ex)
        {
            warn?.Invoke($"설정 파일을 읽지 못해 기본값을 사용합니다: {path} ({ex.Message})");
            config = new AgentConfig();
        }
        config.Normalize();
        return config;
    }

    public string ToJson() => Json.Serialize(this, AgentJsonContext.Default.AgentConfig, indented: true);

    /// <summary>잘못된 값을 안전한 범위로 보정합니다.</summary>
    public void Normalize()
    {
        SampleIntervalSeconds = Math.Clamp(SampleIntervalSeconds, 2, 60);
        RetentionDays = Math.Clamp(RetentionDays, 7, 730);
        MaxDataSizeMb = Math.Clamp(MaxDataSizeMb, 10, 4096);

        // 해제 기준이 도달 불가능한 값(음수, 100 초과)이면 이벤트가 영원히 끝나지 않으므로 범위를 강제
        CpuHighPercent = Math.Clamp(CpuHighPercent, 5, 100);
        CpuRecoverPercent = CpuRecoverPercent >= CpuHighPercent ? CpuHighPercent - 5 : Math.Clamp(CpuRecoverPercent, 0, CpuHighPercent - 1);
        MemoryAvailableLowPercent = Math.Clamp(MemoryAvailableLowPercent, 0, 90);
        MemoryAvailableRecoverPercent = MemoryAvailableRecoverPercent <= MemoryAvailableLowPercent
            ? MemoryAvailableLowPercent + 5
            : Math.Clamp(MemoryAvailableRecoverPercent, MemoryAvailableLowPercent + 1, 100);
        CommitHighPercent = Math.Clamp(CommitHighPercent, 5, 100);
        CommitRecoverPercent = CommitRecoverPercent >= CommitHighPercent ? CommitHighPercent - 5 : Math.Clamp(CommitRecoverPercent, 0, CommitHighPercent - 1);
        DiskActiveHighPercent = Math.Clamp(DiskActiveHighPercent, 10, 100);
        DiskActiveRecoverPercent = DiskActiveRecoverPercent >= DiskActiveHighPercent ? DiskActiveHighPercent - 10 : Math.Clamp(DiskActiveRecoverPercent, 0, DiskActiveHighPercent - 1);

        CpuSustainSeconds = Math.Clamp(CpuSustainSeconds, 5, 3600);
        MemorySustainSeconds = Math.Clamp(MemorySustainSeconds, 5, 3600);
        DiskSustainSeconds = Math.Clamp(DiskSustainSeconds, 5, 3600);
        RecoverSustainSeconds = Math.Clamp(RecoverSustainSeconds, 5, 3600);

        TopProcessCount = Math.Clamp(TopProcessCount, 3, 30);
        SnapshotCooldownMinutes = Math.Clamp(SnapshotCooldownMinutes, 1, 1440);
        MaxSnapshotsPerDay = Math.Clamp(MaxSnapshotsPerDay, 0, 500);

        WorkStartHour = Math.Clamp(WorkStartHour, 0, 23);
        WorkEndHour = Math.Clamp(WorkEndHour, WorkStartHour + 1, 24);

        MaskProcessPatterns = (MaskProcessPatterns ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
    }
}
