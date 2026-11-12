using System.Diagnostics;
using PcAgent.Collection;
using PcAgent.Hosting;
using PcAgent.Storage;

namespace PcAgent.Tests;

/// <summary>코드 리뷰·보안 점검에서 발견된 문제의 회귀 테스트.</summary>
public sealed class RobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcagent-test-" + Guid.NewGuid().ToString("N"));

    public RobustnessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 무시 */ }
    }

    private static DateTimeOffset RecentMinute()
    {
        var t = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);
    }

    [Fact]
    public void Locked_file_is_buffered_and_written_after_unlock()
    {
        var store = new DataStore(_dir);
        var minute = RecentMinute();
        store.AppendMetric(new MetricMinute { MinuteUtc = minute, Samples = 12, CoveredSeconds = 60 });
        var file = Directory.GetFiles(_dir, "metrics-*.csv").Single();

        // 엑셀처럼 쓰기를 막고 파일을 연 상태
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.AppendMetric(new MetricMinute { MinuteUtc = minute.AddMinutes(1), Samples = 12, CoveredSeconds = 60 });
            Assert.Equal(1, store.PendingFiles);
        }

        store.AppendMetric(new MetricMinute { MinuteUtc = minute.AddMinutes(2), Samples = 12, CoveredSeconds = 60 });

        Assert.Equal(0, store.PendingFiles);
        var rows = store.ReadMetrics(minute.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(3, rows.Count);
        Assert.Equal([minute, minute.AddMinutes(1), minute.AddMinutes(2)], rows.Select(r => r.MinuteUtc));
    }

    [Fact]
    public void Pressure_records_merge_in_any_order_and_orphan_ends_are_dropped()
    {
        var store = new DataStore(_dir);
        var minute = RecentMinute();
        var id = Guid.NewGuid();

        // 종료 레코드가 먼저 기록된 경우
        store.AppendPressure(new PressureRecord { Id = id, Kind = PressureKind.Cpu, Phase = PressureRecord.PhaseEnd, StartedUtc = minute, EndedUtc = minute.AddMinutes(1), Peak = 99 });
        store.AppendPressure(new PressureRecord { Id = id, Kind = PressureKind.Cpu, StartedUtc = minute, Peak = 95, Processes = [new ProcessUsage { Name = "app" }] });
        // 시작 기록 없이 종료만 있는 레코드
        store.AppendPressure(new PressureRecord { Id = Guid.NewGuid(), Kind = PressureKind.Memory, Phase = PressureRecord.PhaseEnd, StartedUtc = minute, EndedUtc = minute.AddMinutes(2) });

        var e = Assert.Single(store.ReadPressureEvents(minute.AddHours(-1), DateTimeOffset.UtcNow));
        Assert.Equal(id, e.Id);
        Assert.Equal(minute.AddMinutes(1), e.EndedUtc);
        Assert.Equal(99, e.Peak);
        Assert.Equal("app", Assert.Single(e.Processes!).Name);
    }

    [Fact]
    public void Config_normalize_keeps_recover_thresholds_reachable()
    {
        var c = new AgentConfig
        {
            MemoryAvailableLowPercent = 97,
            CpuRecoverPercent = -10,
            CommitRecoverPercent = 150,
            DiskActiveRecoverPercent = -5,
        };
        c.Normalize();

        Assert.InRange(c.MemoryAvailableRecoverPercent, c.MemoryAvailableLowPercent + 1, 100);
        Assert.InRange(c.CpuRecoverPercent, 0, c.CpuHighPercent - 1);
        Assert.InRange(c.CommitRecoverPercent, 0, c.CommitHighPercent - 1);
        Assert.InRange(c.DiskActiveRecoverPercent, 0, c.DiskActiveHighPercent - 1);
    }

    [Fact]
    public void User_created_folder_is_not_trusted()
    {
        // 일반 사용자가 만든 폴더(소유자/쓰기 권한이 사용자) → 서비스가 쓰면 안 됨
        Assert.False(SecureDirectory.IsTrusted(_dir, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Junction_is_not_trusted()
    {
        var target = Path.Combine(_dir, "target");
        var link = Path.Combine(_dir, "link");
        Directory.CreateDirectory(target);
        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        {
            p!.WaitForExit();
        }

        Assert.True(Directory.Exists(link));
        Assert.False(SecureDirectory.IsTrusted(link, out var reason));
        Assert.Contains("링크", reason);
    }

    [Fact]
    public void Secure_folder_security_is_protected_and_users_read_only()
    {
        var security = SecureDirectory.CreateSecurity();
        Assert.True(security.AreAccessRulesProtected);
        var sddl = security.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        Assert.Contains(";;;SY)", sddl);
        Assert.Contains(";;;BA)", sddl);
        Assert.Contains("0x1200a9;;;BU)", sddl);   // ReadAndExecute + Synchronize
    }

    [Theory]
    [InlineData(@"\\attacker\share\tool.exe", false)]
    [InlineData(@"\\?\C:\Windows\notepad.exe", false)]
    [InlineData(@"\\attacker@80\webdav\tool.exe", false)]
    [InlineData("relative\\tool.exe", false)]
    public void Network_and_unusual_paths_are_not_read_for_publisher(string path, bool expected) =>
        Assert.Equal(expected, ProcessSnapshotter.IsLocalFixedDrivePath(path));

    [Fact]
    public void System_drive_path_is_local_fixed()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(ProcessSnapshotter.IsLocalFixedDrivePath(notepad));
    }
}
