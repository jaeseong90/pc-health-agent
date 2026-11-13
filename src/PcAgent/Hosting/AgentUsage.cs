using System.Diagnostics;

namespace PcAgent.Hosting;

/// <summary>에이전트 자체 자원 사용량(가벼움 확인용). 서비스가 주기적으로 기록합니다.</summary>
public sealed class AgentUsageInfo
{
    public string Version { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public double CpuSeconds { get; set; }
    public double PrivateMb { get; set; }
    public double WorkingSetMb { get; set; }
    public double PeakWorkingSetMb { get; set; }
    public int LogicalProcessors { get; set; }

    /// <summary>가동 시간 대비 평균 CPU 사용률(%, 전체 코어 기준). 가동 10분 미만이면 null.</summary>
    public double? CpuAvgPercent
    {
        get
        {
            double seconds = (UpdatedUtc - StartedUtc).TotalSeconds;
            return seconds < 600 || LogicalProcessors <= 0 ? null : Math.Round(CpuSeconds * 100 / (seconds * LogicalProcessors), 4);
        }
    }
}

/// <summary>
/// 일반 사용자는 SYSTEM 서비스 프로세스의 CPU 시간을 직접 읽을 수 없으므로(메모리는 가능)
/// 서비스가 자기 사용량을 데이터 폴더에 남기고 status/export 가 읽습니다.
/// </summary>
internal static class AgentUsage
{
    public const string FileName = "agent-usage.json";

    public static AgentUsageInfo Capture()
    {
        using var p = Process.GetCurrentProcess();
        return new AgentUsageInfo
        {
            Version = AppInfo.Version,
            StartedUtc = p.StartTime.ToUniversalTime(),
            UpdatedUtc = DateTimeOffset.UtcNow,
            CpuSeconds = Math.Round(p.TotalProcessorTime.TotalSeconds, 2),
            PrivateMb = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1),
            WorkingSetMb = Math.Round(p.WorkingSet64 / 1048576.0, 1),
            PeakWorkingSetMb = Math.Round(p.PeakWorkingSet64 / 1048576.0, 1),
            LogicalProcessors = Environment.ProcessorCount,
        };
    }

    public static void Write(string root)
    {
        var path = Path.Combine(root, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Json.Serialize(Capture(), AgentJsonContext.Default.AgentUsageInfo));
        File.Move(temp, path, overwrite: true);
    }

    public static AgentUsageInfo? Read(string root)
    {
        try
        {
            var path = Path.Combine(root, FileName);
            return File.Exists(path) ? Json.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.AgentUsageInfo) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>실행 중인 서비스 프로세스(세션 0)의 현재 메모리(MB). 일반 사용자 권한으로도 읽힘.</summary>
    public static double? ServicePrivateMb()
    {
        try
        {
            double? result = null;
            foreach (var p in Process.GetProcessesByName("PcAgent"))
            {
                using (p)
                {
                    if (result is null && p.SessionId == 0 && p.Id != Environment.ProcessId)
                        result = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1);
                }
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
