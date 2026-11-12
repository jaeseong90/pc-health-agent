using PcAgent.Collection;
using PcAgent.Inventory;

namespace PcAgent.Tests;

internal static class TestData
{
    /// <summary>2026-11-09 은 월요일.</summary>
    public static readonly DateTimeOffset Monday = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    public static InventoryInfo Inventory(bool win11 = true, string media = "SSD", long ramMb = 16384, string cpu = "Intel(R) Core(TM) i5-10500 CPU @ 3.10GHz")
    {
        var inv = new InventoryInfo
        {
            Hostname = "TEST-PC",
            Manufacturer = "TestVendor",
            Model = "T1000",
            OsName = win11 ? "Windows 11 Pro" : "Windows 10 Pro",
            OsBuild = win11 ? 26100 : 19045,
            IsWindows11 = win11,
            CpuName = cpu,
            CpuCores = 6,
            CpuLogicalProcessors = 12,
            RamInstalledMb = ramMb,
            RamSlotsTotal = 2,
            RamSlotsUsed = 1,
            TpmVersion = "2.0",
            IsUefi = true,
            SecureBootEnabled = true,
            BiosDate = "2022-01-01",
            EstimatedAgeYears = 3,
        };
        inv.RamModules.Add(new MemoryModule { SizeMb = ramMb, Type = "DDR4", FormFactor = "DIMM" });
        inv.Disks.Add(new DiskDrive { Number = 0, Model = "Disk", BusType = media == "SSD" ? "NVMe" : "SATA", MediaType = media, SizeGb = 476, IsSystem = true });
        inv.Volumes.Add(new VolumeInfo { Name = "C:", SizeGb = 476, FreeGb = 200, IsSystem = true });
        return inv;
    }

    /// <summary>평일 10~17시(UTC), days 일치 1분 지표. pressurePct/cpuHighPct 비율만큼 부족 상태로 채움.</summary>
    public static List<MetricMinute> WorkdayMetrics(int days, double memPressurePct = 0, double cpuHighPct = 0, double diskBusyPct = 0)
    {
        var list = new List<MetricMinute>();
        int added = 0;
        for (var day = Monday; added < days; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            added++;
            for (int minute = 0; minute < 7 * 60; minute++)
            {
                list.Add(new MetricMinute
                {
                    MinuteUtc = day.AddHours(10).AddMinutes(minute),
                    Samples = 12,
                    CoveredSeconds = 60,
                    CpuAvg = 30,
                    CpuMax = 60,
                    MemAvailAvg = 35,
                    MemAvailMin = 30,
                    CommitAvg = 60,
                    CommitMax = 65,
                    CpuHighSeconds = (int)Math.Round(60 * cpuHighPct / 100),
                    MemPressureSeconds = (int)Math.Round(60 * memPressurePct / 100),
                    DiskBusySeconds = (int)Math.Round(60 * diskBusyPct / 100),
                });
            }
        }
        return list;
    }

    public static PressureRecord MemoryEvent(DateTimeOffset start, params (string Name, long PrivateMb)[] processes) => new()
    {
        Id = Guid.NewGuid(),
        Kind = PressureKind.Memory,
        StartedUtc = start,
        EndedUtc = start.AddMinutes(3),
        Peak = 92,
        Processes = processes.Select(p => new ProcessUsage { Name = p.Name, Instances = 1, PrivateBytes = p.PrivateMb * 1048576 }).ToList(),
    };
}
