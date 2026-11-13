using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PcAgent.Interop;

namespace PcAgent.Inventory;

/// <summary>
/// 하드웨어/OS 정보 수집. WMI 를 쓰지 않고 레지스트리·SMBIOS·디스크 IOCTL·TPM API 로 읽습니다.
/// 대부분 관리자 권한 없이 동작합니다.
/// </summary>
internal static unsafe class InventoryCollector
{
    private const double GiB = 1024d * 1024 * 1024;

    public static InventoryInfo Collect(List<string> warnings)
    {
        var inv = new InventoryInfo
        {
            CollectedUtc = DateTimeOffset.UtcNow,
            Hostname = Environment.MachineName,
            CpuLogicalProcessors = Environment.ProcessorCount,
            UptimeHours = Math.Round(Environment.TickCount64 / 3_600_000.0, 1),
        };

        Try(warnings, "OS", () => ReadOs(inv));
        Try(warnings, "CPU", () => ReadCpu(inv));
        Try(warnings, "SMBIOS", () => ReadSmbios(inv));
        Try(warnings, "메모리", () => ReadMemory(inv));
        Try(warnings, "디스크", () => ReadDisks(inv));
        Try(warnings, "볼륨", () => ReadVolumes(inv));
        Try(warnings, "GPU", () => ReadGpus(inv));
        Try(warnings, "보안(TPM/UEFI)", () => ReadSecurity(inv));
        Try(warnings, "배터리", () => ReadBatteries(inv));
        return inv;
    }

    /// <summary>배터리 장치별 설계/완충 용량과 충전 횟수(관리자 권한 불필요). UPS 등 단시간용 배터리는 제외.</summary>
    private static void ReadBatteries(InventoryInfo inv)
    {
        foreach (var path in DeviceInterfaces(Native.GUID_DEVICE_BATTERY))
        {
            using var h = Native.CreateFile(path, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
            if (h.IsInvalid) continue;

            uint wait = 0, tag = 0;
            if (!Native.DeviceIoControl(h, Native.IOCTL_BATTERY_QUERY_TAG, &wait, sizeof(uint), &tag, sizeof(uint), out _, 0) || tag == 0)
                continue;   // 배터리가 빠져 있음

            var query = new Native.BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0 };
            Native.BATTERY_INFORMATION info;
            if (!Native.DeviceIoControl(h, Native.IOCTL_BATTERY_QUERY_INFORMATION,
                    &query, (uint)sizeof(Native.BATTERY_QUERY_INFORMATION), &info, (uint)sizeof(Native.BATTERY_INFORMATION), out _, 0))
                continue;
            if ((info.Capabilities & Native.BATTERY_SYSTEM_BATTERY) == 0 || (info.Capabilities & Native.BATTERY_IS_SHORT_TERM) != 0)
                continue;

            inv.Batteries.Add(new BatteryInfo
            {
                Chemistry = Encoding.ASCII.GetString(new ReadOnlySpan<byte>(info.Chemistry, 4)).TrimEnd('\0', ' ') is { Length: > 0 } chem ? chem : null,
                DesignedCapacity = info.DesignedCapacity,
                FullChargedCapacity = info.FullChargedCapacity,
                Relative = (info.Capabilities & Native.BATTERY_CAPACITY_RELATIVE) != 0,
                CycleCount = info.CycleCount > 0 ? (int)info.CycleCount : null,
            });
        }
    }

    /// <summary>장치 인터페이스 경로 목록(CfgMgr32).</summary>
    private static List<string> DeviceInterfaces(Guid interfaceClass)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Native.CM_Get_Device_Interface_List_Size(out uint length, interfaceClass, null, 0) != Native.CR_SUCCESS || length <= 1)
                return [];
            var buffer = new char[length];
            uint cr;
            fixed (char* p = buffer) cr = Native.CM_Get_Device_Interface_List(interfaceClass, null, p, length, 0);
            if (cr == Native.CR_BUFFER_SMALL) continue;   // 그 사이 장치가 추가됨
            if (cr != Native.CR_SUCCESS) return [];
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return [];
    }

    private static void Try(List<string> warnings, string area, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            warnings.Add($"{area} 정보 수집 실패: {ex.Message}");
        }
    }

    private static void ReadOs(InventoryInfo inv)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (k is null) return;
        int.TryParse(k.GetValue("CurrentBuildNumber") as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int build);
        inv.OsBuild = build;
        inv.OsUbr = k.GetValue("UBR") is int ubr ? ubr : 0;
        inv.IsWindows11 = build >= 22000;
        inv.OsEdition = k.GetValue("EditionID") as string;
        inv.OsDisplayVersion = (k.GetValue("DisplayVersion") ?? k.GetValue("ReleaseId")) as string;
        var product = k.GetValue("ProductName") as string;
        // Windows 11 도 레지스트리 ProductName 은 "Windows 10 ..." 으로 남아 있음
        if (product is not null && inv.IsWindows11) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        inv.OsName = product;
    }

    private static void ReadCpu(InventoryInfo inv)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        if (k is null) return;
        inv.CpuName = (k.GetValue("ProcessorNameString") as string)?.Trim();
        if (k.GetValue("~MHz") is int mhz && mhz > 0) inv.CpuMaxMhz = mhz;
    }

    private static void ReadSmbios(InventoryInfo inv)
    {
        uint size = Native.GetSystemFirmwareTable(Native.RSMB, 0, null, 0);
        if (size == 0) throw new InvalidOperationException("SMBIOS 테이블 없음");
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            size = Native.GetSystemFirmwareTable(Native.RSMB, 0, p, size);
        }
        var s = SmbiosParser.Parse(buffer.AsSpan(0, (int)Math.Min(size, (uint)buffer.Length)));

        inv.Manufacturer = s.SystemManufacturer;
        inv.Model = s.SystemProduct;
        inv.SerialNumber = s.SystemSerial;
        inv.BiosVendor = s.BiosVendor;
        inv.BiosVersion = s.BiosVersion;
        inv.CpuName ??= s.CpuName;
        if (s.CpuCores > 0) inv.CpuCores = s.CpuCores;
        if (s.CpuMaxMhz > 0) inv.CpuMaxMhz = s.CpuMaxMhz;
        inv.RamModules = s.Modules;
        inv.RamSlotsUsed = s.Modules.Count;
        int slots = Math.Max(s.MemorySlots, s.Modules.Count + s.EmptySlotEntries);
        inv.RamSlotsTotal = slots > 0 ? slots : null;

        string[] formats = ["MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(s.BiosReleaseDate, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var biosDate))
        {
            inv.BiosDate = biosDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            inv.EstimatedAgeYears = Math.Round((DateTime.Today - biosDate).TotalDays / 365.25, 1);
        }
    }

    private static void ReadMemory(InventoryInfo inv)
    {
        if (Native.GetPhysicallyInstalledSystemMemory(out ulong kb)) inv.RamInstalledMb = (long)(kb / 1024);
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
        if (Native.GlobalMemoryStatusEx(ref ms)) inv.RamVisibleMb = (long)(ms.ullTotalPhys / (1024 * 1024));
        if (inv.RamInstalledMb == 0) inv.RamInstalledMb = inv.RamModules.Sum(m => m.SizeMb);
        if (inv.RamInstalledMb == 0) inv.RamInstalledMb = inv.RamVisibleMb;
    }

    private static void ReadDisks(InventoryInfo inv) => inv.Disks.AddRange(EnumerateDisks());

    /// <summary>물리 디스크 목록(사양 + 건강 상태). 서비스의 관리자 전용 정보 기록에서도 사용합니다.</summary>
    internal static List<DiskDrive> EnumerateDisks()
    {
        var disks = new List<DiskDrive>();
        int? systemDisk = GetSystemDiskNumber();
        byte* geo = stackalloc byte[256];
        for (int i = 0; i < 32; i++)
        {
            // 접근 권한 0 으로 열면 관리자 권한 없이 속성 조회 가능
            using var h = Native.CreateFile($@"\\.\PhysicalDrive{i}", 0,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
            if (h.IsInvalid) continue;

            var disk = new DiskDrive { Number = i, IsSystem = i == systemDisk };
            QueryDescriptor(h, disk);
            bool? seekPenalty = QuerySeekPenalty(h);
            disk.MediaType = disk.BusType == "NVMe" ? "SSD" : seekPenalty switch
            {
                true => "HDD",
                false => "SSD",
                null => "Unknown",
            };

            if (Native.DeviceIoControl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, geo, 256, out uint ret, 0) && ret >= 32)
                disk.SizeGb = Math.Round(*(long*)(geo + 24) / GiB, 1);

            if (!disk.IsRemovable && disk.BusType is not ("USB" or "Virtual" or "FileBackedVirtual" or "Spaces" or "SD" or "MMC"))
            {
                try { disk.Health = DiskHealthReader.Read(i, disk.BusType); } catch { /* 건강 정보는 선택 항목 */ }
            }

            disks.Add(disk);
        }
        return disks;
    }

    private static void QueryDescriptor(SafeFileHandle h, DiskDrive disk)
    {
        var query = new Native.STORAGE_PROPERTY_QUERY { PropertyId = 0, QueryType = 0 };
        const int size = 1024;
        byte* buf = stackalloc byte[size];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)sizeof(Native.STORAGE_PROPERTY_QUERY),
                buf, size, out uint ret, 0) || ret < 36)
            return;

        var span = new ReadOnlySpan<byte>(buf, (int)ret);
        disk.IsRemovable = span[10] != 0;
        int vendorOffset = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        int productOffset = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
        int busType = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
        var model = $"{ReadAnsi(span, vendorOffset)} {ReadAnsi(span, productOffset)}".Trim();
        disk.Model = model.Length > 0 ? model : null;
        disk.BusType = busType switch
        {
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            7 => "USB",
            8 => "RAID",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            14 => "Virtual",
            15 => "FileBackedVirtual",
            16 => "Spaces",
            17 => "NVMe",
            18 => "SCM",
            19 => "UFS",
            _ => "Unknown",
        };
    }

    private static bool? QuerySeekPenalty(SafeFileHandle h)
    {
        var query = new Native.STORAGE_PROPERTY_QUERY { PropertyId = 7, QueryType = 0 };
        byte* buf = stackalloc byte[16];
        if (Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)sizeof(Native.STORAGE_PROPERTY_QUERY),
                buf, 16, out uint ret, 0) && ret >= 9)
            return buf[8] != 0;
        return null;
    }

    private static int? GetSystemDiskNumber()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        using var h = Native.CreateFile($@"\\.\{root}", 0,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;
        byte* buf = stackalloc byte[256];
        if (Native.DeviceIoControl(h, Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, 256, out uint ret, 0)
            && ret >= 16 && *(uint*)buf > 0)
            return (int)*(uint*)(buf + 8);
        return null;
    }

    private static string ReadAnsi(ReadOnlySpan<byte> span, int offset)
    {
        if (offset <= 0 || offset >= span.Length) return "";
        var rest = span[offset..];
        int len = rest.IndexOf((byte)0);
        if (len < 0) len = rest.Length;
        return Encoding.Latin1.GetString(rest[..len]).Trim();
    }

    private static void ReadVolumes(InventoryInfo inv)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                inv.Volumes.Add(new VolumeInfo
                {
                    Name = d.Name.TrimEnd('\\'),
                    SizeGb = Math.Round(d.TotalSize / GiB, 1),
                    FreeGb = Math.Round(d.TotalFreeSpace / GiB, 1),
                    IsSystem = string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch
            {
                // 준비 안 된 드라이브
            }
        }
    }

    private static void ReadGpus(InventoryInfo inv)
    {
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return;
        foreach (var name in cls.GetSubKeyNames())
        {
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
            try
            {
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc) && !inv.Gpus.Contains(desc.Trim()))
                    inv.Gpus.Add(desc.Trim());
            }
            catch
            {
                // 접근 불가 키
            }
        }
    }

    private static void ReadSecurity(InventoryInfo inv)
    {
        if (Native.GetFirmwareType(out int firmware)) inv.IsUefi = firmware == 2;

        using (var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
        {
            if (sb?.GetValue("UEFISecureBootEnabled") is int v) inv.SecureBootEnabled = v == 1;
            else if (inv.IsUefi == false) inv.SecureBootEnabled = false;
        }

        try
        {
            uint r = Native.Tbsi_GetDeviceInfo((uint)sizeof(Native.TPM_DEVICE_INFO), out var tpm);
            if (r == 0) inv.TpmVersion = tpm.tpmVersion switch { 1 => "1.2", 2 => "2.0", _ => null };
            else if (r == Native.TBS_E_TPM_NOT_FOUND) inv.TpmVersion = "none";
        }
        catch (DllNotFoundException)
        {
            // tbs.dll 없음
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
