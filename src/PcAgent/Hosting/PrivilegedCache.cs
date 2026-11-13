using PcAgent.EventLogs;
using PcAgent.Inventory;
using PcAgent.Privacy;

namespace PcAgent.Hosting;

/// <summary>관리자 권한이 있어야 읽히는 정보. 서비스(SYSTEM)가 주기적으로 기록합니다.</summary>
public sealed class PrivilegedCacheInfo
{
    public DateTimeOffset UpdatedUtc { get; set; }
    /// <summary>부팅 시간 로그(Diagnostics-Performance 100) — 일반 사용자는 읽을 수 없음.</summary>
    public List<EventLogItem> BootEvents { get; set; } = [];
    /// <summary>디스크 SMART — 장치에 따라 일반 사용자는 읽을 수 없음.</summary>
    public List<CachedDiskHealth> Disks { get; set; } = [];
}

public sealed class CachedDiskHealth
{
    public int Number { get; set; }
    public string? Model { get; set; }
    public DiskHealth Health { get; set; } = new();
}

/// <summary>
/// 직원은 결과를 일반 권한(메뉴의 '결과 내보내기')으로 내보내므로, 관리자 전용 정보는 서비스가 6시간마다
/// 데이터 폴더에 남겨 두고 내보내기에서 빠진 부분만 채웁니다. 이벤트 로그 1회 조회와 디스크 IOCTL 몇 번이라 부담은 무시할 수준입니다.
/// </summary>
internal static class PrivilegedCache
{
    public const string FileName = "privileged-cache.json";
    private static readonly TimeSpan BootHistory = TimeSpan.FromDays(90);

    public static void Write(string root, ProcessMasker masker)
    {
        var warnings = new List<string>();
        var info = new PrivilegedCacheInfo
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            BootEvents = EventLogCollector.Collect(DateTimeOffset.UtcNow - BootHistory, masker, warnings, bootOnly: true),
            Disks = InventoryCollector.EnumerateDisks()
                .Where(d => d.Health is not null)
                .Select(d => new CachedDiskHealth { Number = d.Number, Model = d.Model, Health = d.Health! })
                .ToList(),
        };
        var path = Path.Combine(root, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Json.Serialize(info, AgentJsonContext.Default.PrivilegedCacheInfo));
        File.Move(temp, path, overwrite: true);
    }

    public static PrivilegedCacheInfo? Read(string root)
    {
        try
        {
            var path = Path.Combine(root, FileName);
            return File.Exists(path) ? Json.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.PrivilegedCacheInfo) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 내보내기에서 빠진 것만 채웁니다. 부팅 로그를 직접 읽지 못했으면(일반 권한) 기록된 부팅 시간을,
    /// 건강 상태를 읽지 못한 디스크에는 같은 번호·모델의 기록된 값을 씁니다. 직접 읽은 값이 있으면 그대로 둡니다.
    /// </summary>
    public static void ApplyTo(PrivilegedCacheInfo? cache, InventoryInfo inventory, List<EventLogItem> logs, List<string> warnings, DateTimeOffset fromUtc)
    {
        if (cache is null) return;

        if (!logs.Any(l => l.Category == "Boot"))
        {
            var boots = cache.BootEvents.Where(b => b.Category == "Boot" && b.TimeUtc >= fromUtc).ToList();
            if (boots.Count > 0)
            {
                logs.AddRange(boots);
                logs.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
                warnings.RemoveAll(w => w.Contains(EventLogCollector.BootChannel, StringComparison.Ordinal));
            }
        }

        foreach (var disk in inventory.Disks.Where(d => d.Health is null))
        {
            var cached = cache.Disks.FirstOrDefault(c => c.Number == disk.Number && c.Model == disk.Model);
            if (cached is not null) disk.Health = cached.Health;
        }
    }
}
