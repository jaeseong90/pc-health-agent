using System.Buffers.Binary;
using PcAgent.Analysis;
using PcAgent.EventLogs;
using PcAgent.Hosting;
using PcAgent.Inventory;

namespace PcAgent.Tests;

/// <summary>디스크 건강(SMART), 관리자 전용 정보 캐시, 수집 진행 상황 테스트.</summary>
public class HealthTests
{
    private static AnalysisResult Analyze(InventoryInfo inv) => UpgradeAdvisor.Analyze(new AnalysisInput
    {
        Inventory = inv, Metrics = TestData.WorkdayMetrics(5), Events = [], Logs = [], Config = new AgentConfig(), TimeZone = TimeZoneInfo.Utc,
    });

    // ---------------- SMART 해석 ----------------

    [Fact]
    public void Nvme_health_log_is_parsed()
    {
        var log = new byte[512];
        log[0] = 0x04;                                                  // 신뢰성 저하
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), 318);  // 318K = 45°C
        log[3] = 100;
        log[4] = 10;
        log[5] = 2;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48), 27_148_438);   // × 512,000 바이트 ≈ 13.9TB
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), 1014);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144), 24);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(160), 3);

        var h = new DiskHealth();
        DiskHealthReader.ParseNvmeHealthLog(log, h);

        Assert.Equal(45, h.TemperatureC);
        Assert.Equal(2, h.PercentageUsed);
        Assert.Equal(100, h.AvailableSpare);
        Assert.Equal(13.9, h.DataWrittenTb);
        Assert.Equal(1014, h.PowerOnHours);
        Assert.Equal(24, h.UnsafeShutdowns);
        Assert.Equal(3, h.MediaErrors);
        Assert.Equal("Critical", h.Level);   // 신뢰성 저하 비트
        Assert.Contains("신뢰성 저하 경고", h.Problems());
    }

    private static void Attribute(byte[] data, int index, byte id, long raw)
    {
        int o = 2 + index * 12;
        data[o] = id;
        for (int b = 0; b < 6; b++) data[o + 5 + b] = (byte)(raw >> (8 * b));
    }

    [Fact]
    public void Ata_smart_attributes_are_parsed()
    {
        var data = new byte[512];
        Attribute(data, 0, 5, 8);                      // 재할당 섹터
        Attribute(data, 1, 9, 12345);                  // 사용 시간
        Attribute(data, 2, 194, 0x0028_0010_0023);     // 온도: 최하위 바이트 35°C (나머지는 최저/최고)
        Attribute(data, 3, 197, 2);                    // 불안정 섹터
        Attribute(data, 4, 198, 0);

        var h = new DiskHealth { FailurePredicted = false };
        DiskHealthReader.ParseAtaAttributes(data, h);

        Assert.Equal(8, h.ReallocatedSectors);
        Assert.Equal(12345, h.PowerOnHours);
        Assert.Equal(35, h.TemperatureC);
        Assert.Equal(2, h.PendingSectors);
        Assert.Equal(0, h.UncorrectableSectors);
        Assert.Equal("Critical", h.Level);
        Assert.Contains("불안정 섹터 2개", h.Problems());
        Assert.Contains("재할당 섹터 8개", h.Problems());
    }

    [Theory]
    [InlineData(null, null, null, "Unknown")]
    [InlineData(false, null, null, "Ok")]
    [InlineData(null, 3, null, "Ok")]
    [InlineData(null, 85, null, "Warning")]
    [InlineData(null, 100, null, "Critical")]
    [InlineData(true, null, null, "Critical")]
    [InlineData(false, null, 4L, "Warning")]       // 재할당 섹터
    public void Health_level_classification(bool? failure, int? used, long? reallocated, string expected) =>
        Assert.Equal(expected, new DiskHealth { FailurePredicted = failure, PercentageUsed = used, ReallocatedSectors = reallocated }.Level);

    // ---------------- 판정 규칙 ----------------

    [Fact]
    public void Failing_ssd_recommends_disk_replacement()
    {
        var inv = TestData.Inventory();
        inv.Disks[0].Health = new DiskHealth { PercentageUsed = 104, AvailableSpare = 100 };
        var r = Analyze(inv);

        var rec = Assert.Single(r.Recommendations, x => x.Code == "DISK_REPLACE");
        Assert.Equal("High", rec.Priority);
        Assert.Contains("시스템 디스크", rec.Detail);
        Assert.Contains("SSD 수명 104% 사용", rec.Detail);
        Assert.Equal("Upgrade", r.Verdict);
    }

    [Fact]
    public void Failing_system_hdd_raises_ssd_replacement_instead_of_separate_disk_item()
    {
        var inv = TestData.Inventory(media: "HDD");
        inv.Disks[0].Health = new DiskHealth { FailurePredicted = true };
        var r = Analyze(inv);

        var rec = Assert.Single(r.Recommendations, x => x.Code == "SSD_REPLACE");
        Assert.Equal("High", rec.Priority);
        Assert.Contains("고장을 예측", rec.Detail);
        Assert.DoesNotContain(r.Recommendations, x => x.Code == "DISK_REPLACE");
    }

    [Fact]
    public void Worn_disk_gets_check_and_healthy_disk_gets_nothing()
    {
        var worn = TestData.Inventory();
        worn.Disks[0].Health = new DiskHealth { PercentageUsed = 85, FailurePredicted = false };
        Assert.Equal("Medium", Assert.Single(Analyze(worn).Recommendations, x => x.Code == "DISK_CHECK").Priority);

        var healthy = TestData.Inventory();
        healthy.Disks[0].Health = new DiskHealth { PercentageUsed = 3, FailurePredicted = false, PowerOnHours = 900 };
        var r = Analyze(healthy);
        Assert.DoesNotContain(r.Recommendations, x => x.Code.StartsWith("DISK_", StringComparison.Ordinal));
        Assert.Equal("Ok", r.Verdict);
    }

    // ---------------- 관리자 전용 정보 캐시 ----------------

    private static PrivilegedCacheInfo Cache() => new()
    {
        UpdatedUtc = TestData.Monday.AddDays(3),
        BootEvents =
        [
            new EventLogItem { Category = "Boot", TimeUtc = TestData.Monday.AddDays(1), Value = 31000 },
            new EventLogItem { Category = "Boot", TimeUtc = TestData.Monday.AddDays(-40), Value = 29000 },   // 내보내기 기간 밖
        ],
        Disks = [new CachedDiskHealth { Number = 0, Model = "Disk", Health = new DiskHealth { PercentageUsed = 5 } }],
    };

    [Fact]
    public void Cache_fills_boot_times_and_disk_health_missing_from_user_export()
    {
        var inv = TestData.Inventory();
        var logs = new List<EventLogItem> { new() { Category = "AppCrash", TimeUtc = TestData.Monday.AddDays(2) } };
        var warnings = new List<string> { $"이벤트 로그({EventLogCollector.BootChannel}) 접근 권한 없음 — 관리자 권한으로 내보내면 포함됩니다.", "다른 경고" };

        PrivilegedCache.ApplyTo(Cache(), inv, logs, warnings, fromUtc: TestData.Monday);

        Assert.Equal(["Boot", "AppCrash"], logs.Select(l => l.Category));   // 기간 안의 부팅 기록만, 시간순
        Assert.Equal(["다른 경고"], warnings);
        Assert.Equal(5, inv.Disks[0].Health!.PercentageUsed);
    }

    [Fact]
    public void Cache_does_not_override_values_read_directly()
    {
        var inv = TestData.Inventory();
        inv.Disks[0].Health = new DiskHealth { PercentageUsed = 40 };
        inv.Disks.Add(new DiskDrive { Number = 1, Model = "Other", MediaType = "SSD" });   // 캐시에 없는 디스크
        var logs = new List<EventLogItem> { new() { Category = "Boot", TimeUtc = TestData.Monday.AddDays(2), Value = 20000 } };

        PrivilegedCache.ApplyTo(Cache(), inv, logs, [], fromUtc: TestData.Monday);

        Assert.Single(logs);                                   // 관리자 권한으로 직접 읽은 부팅 기록 유지
        Assert.Equal(40, inv.Disks[0].Health!.PercentageUsed);
        Assert.Null(inv.Disks[1].Health);
        PrivilegedCache.ApplyTo(null, inv, logs, [], TestData.Monday);   // 캐시 없음 — 아무것도 안 함
    }

    // ---------------- 수집 진행 ----------------

    [Fact]
    public void Progress_counts_days_and_hours_against_minimum()
    {
        var p = CollectionProgress.Compute(TestData.WorkdayMetrics(2), new AgentConfig(), TimeZoneInfo.Utc);
        Assert.Equal(2, p.Days);
        Assert.Equal(14, p.ObservedHours);
        Assert.Equal(14, p.WorkHours);
        Assert.True(p.Sufficient);
        Assert.Contains("판정 가능", p.Describe());

        var early = CollectionProgress.Compute(TestData.WorkdayMetrics(1).Take(120), new AgentConfig(), TimeZoneInfo.Utc);
        Assert.False(early.Sufficient);
        Assert.Contains("6시간 더 필요", early.Describe());
    }
}
