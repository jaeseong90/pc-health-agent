namespace PcAgent.Inventory;

/// <summary>PC 하드웨어/OS 사양. 내보내기 시점에 수집합니다(백그라운드 상시 수집 안 함).</summary>
public sealed class InventoryInfo
{
    public DateTimeOffset CollectedUtc { get; set; }
    public string Hostname { get; set; } = "";
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }

    public string? OsName { get; set; }
    public string? OsEdition { get; set; }
    public string? OsDisplayVersion { get; set; }
    public int OsBuild { get; set; }
    public int OsUbr { get; set; }
    public bool IsWindows11 { get; set; }

    public string? CpuName { get; set; }
    public int CpuCores { get; set; }
    public int CpuLogicalProcessors { get; set; }
    public int? CpuMaxMhz { get; set; }

    public long RamInstalledMb { get; set; }
    public long RamVisibleMb { get; set; }
    public int? RamSlotsTotal { get; set; }
    public int? RamSlotsUsed { get; set; }
    public List<MemoryModule> RamModules { get; set; } = [];

    /// <summary>모든 모듈이 온보드(납땜)로 보이면 true — 증설 불가 가능성.</summary>
    public bool RamSoldered => RamModules.Count > 0 && RamModules.All(m => SmbiosParser.IsSoldered(m.FormFactor));

    /// <summary>표시용 슬롯 정보: "2/4", "온보드", null.</summary>
    public string? RamSlotsLabel => RamSoldered ? "온보드" : RamSlotsTotal is { } t ? $"{RamSlotsUsed}/{t}" : null;

    public List<DiskDrive> Disks { get; set; } = [];
    public List<VolumeInfo> Volumes { get; set; } = [];
    public List<string> Gpus { get; set; } = [];

    public string? BiosVendor { get; set; }
    public string? BiosVersion { get; set; }
    public string? BiosDate { get; set; }
    /// <summary>BIOS 날짜 기준 추정 연식. BIOS 업데이트 시 실제보다 짧게 나옵니다.</summary>
    public double? EstimatedAgeYears { get; set; }

    /// <summary>"2.0", "1.2", "none" 또는 null(확인 불가).</summary>
    public string? TpmVersion { get; set; }
    public bool? IsUefi { get; set; }
    public bool? SecureBootEnabled { get; set; }

    /// <summary>노트북 배터리(데스크톱은 비어 있음).</summary>
    public List<BatteryInfo> Batteries { get; set; } = [];

    /// <summary>배터리 수명(%) = 완충 용량 합 ÷ 설계 용량 합. 배터리가 없거나 값이 비정상이면 null.</summary>
    public double? BatteryHealthPercent
    {
        get
        {
            var valid = Batteries.Where(b => b.HealthPercent is not null).ToList();
            return valid.Count == 0 ? null
                : Math.Round(valid.Sum(b => (double)b.FullChargedCapacity) * 100 / valid.Sum(b => (double)b.DesignedCapacity), 1);
        }
    }

    public double UptimeHours { get; set; }
}

public sealed class BatteryInfo
{
    /// <summary>배터리 화학 종류(LION, LiP 등).</summary>
    public string? Chemistry { get; set; }
    /// <summary>설계 용량. Relative 가 false 면 mWh.</summary>
    public long DesignedCapacity { get; set; }
    /// <summary>현재 완충 용량. 노화할수록 줄어듭니다.</summary>
    public long FullChargedCapacity { get; set; }
    /// <summary>true 면 용량 단위가 mWh 가 아닌 상대값(비율 계산은 가능).</summary>
    public bool Relative { get; set; }
    public int? CycleCount { get; set; }

    /// <summary>완충 ÷ 설계(%). 설계 용량이 없거나 완충이 설계의 1.5배를 넘는 등 비정상 값이면 null.</summary>
    public double? HealthPercent =>
        DesignedCapacity > 0 && FullChargedCapacity > 0 && FullChargedCapacity <= DesignedCapacity * 3 / 2
            ? Math.Round(FullChargedCapacity * 100.0 / DesignedCapacity, 1)
            : null;
}

public sealed class MemoryModule
{
    public string? Locator { get; set; }
    public long SizeMb { get; set; }
    public string? Type { get; set; }
    public string? FormFactor { get; set; }
    public int? SpeedMts { get; set; }
    public int? ConfiguredSpeedMts { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
}

public sealed class DiskDrive
{
    public int Number { get; set; }
    public string? Model { get; set; }
    public string BusType { get; set; } = "Unknown";
    /// <summary>"SSD", "HDD", "Unknown".</summary>
    public string MediaType { get; set; } = "Unknown";
    public double SizeGb { get; set; }
    public bool IsSystem { get; set; }
    public bool IsRemovable { get; set; }
    /// <summary>SMART 건강 상태. 확인할 수 없으면 null.</summary>
    public DiskHealth? Health { get; set; }
}

public sealed class VolumeInfo
{
    public string Name { get; set; } = "";
    public double SizeGb { get; set; }
    public double FreeGb { get; set; }
    public bool IsSystem { get; set; }
}
