using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using PcAgent.Interop;

namespace PcAgent.Inventory;

/// <summary>디스크 건강 상태(SMART). 값이 없으면 null — 확인할 수 없는 항목은 비워 둡니다.</summary>
public sealed class DiskHealth
{
    /// <summary>디스크 자체의 고장 예측(SMART) 결과.</summary>
    public bool? FailurePredicted { get; set; }
    /// <summary>NVMe 치명 경고 비트(0: 예비 공간 부족, 1: 온도, 2: 신뢰성 저하, 3: 읽기 전용, 4: 백업 실패).</summary>
    public int? CriticalWarning { get; set; }
    /// <summary>NVMe 수명 사용률(%). 제조사 보증 수명 대비, 100 을 넘을 수 있음.</summary>
    public int? PercentageUsed { get; set; }
    public int? AvailableSpare { get; set; }
    public int? AvailableSpareThreshold { get; set; }
    public long? MediaErrors { get; set; }
    /// <summary>ATA SMART 5 / 197 / 198.</summary>
    public long? ReallocatedSectors { get; set; }
    public long? PendingSectors { get; set; }
    public long? UncorrectableSectors { get; set; }
    /// <summary>전원이 켜져 있던 누적 시간(실사용 시간).</summary>
    public long? PowerOnHours { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public int? TemperatureC { get; set; }
    public double? DataWrittenTb { get; set; }

    /// <summary>Critical(고장 위험) / Warning(주의) / Ok(정상) / Unknown(확인 불가).</summary>
    public string Level =>
        FailurePredicted == true || (CriticalWarning & 0x0D) is > 0 || PercentageUsed >= 100 || PendingSectors > 0 || UncorrectableSectors > 0 ? "Critical"
        : ReallocatedSectors > 0 || MediaErrors > 0 || PercentageUsed >= 80 ? "Warning"
        : FailurePredicted is not null || PercentageUsed is not null ? "Ok"
        : "Unknown";

    public static string LevelLabel(string level) => level switch
    {
        "Critical" => "고장 위험",
        "Warning" => "주의",
        "Ok" => "정상",
        _ => "확인 불가",
    };

    /// <summary>이상 징후 설명(정상이면 빈 목록).</summary>
    public List<string> Problems()
    {
        var list = new List<string>();
        if (FailurePredicted == true) list.Add("디스크가 스스로 고장을 예측함(SMART)");
        if ((CriticalWarning & 0x01) is > 0) list.Add($"예비 공간 부족({AvailableSpare}%)");
        if ((CriticalWarning & 0x04) is > 0) list.Add("신뢰성 저하 경고");
        if ((CriticalWarning & 0x08) is > 0) list.Add("읽기 전용으로 전환됨");
        if (PercentageUsed >= 80) list.Add($"SSD 수명 {PercentageUsed}% 사용");
        if (PendingSectors > 0) list.Add($"불안정 섹터 {PendingSectors}개");
        if (UncorrectableSectors > 0) list.Add($"복구 불가 섹터 {UncorrectableSectors}개");
        if (ReallocatedSectors > 0) list.Add($"재할당 섹터 {ReallocatedSectors}개");
        if (MediaErrors > 0) list.Add($"미디어 오류 {MediaErrors}회");
        return list;
    }
}

/// <summary>
/// 디스크 건강 상태 조회. 고장 예측(IOCTL_STORAGE_PREDICT_FAILURE)과 ATA SMART 속성은 일반 권한으로도 읽히는 경우가 많고,
/// NVMe 건강 로그는 장치에 따라 관리자 권한이 필요해 서비스(SYSTEM)가 주기적으로 읽어 둡니다(PrivilegedCache).
/// </summary>
internal static unsafe class DiskHealthReader
{
    private const int QueryHeaderSize = 8;      // STORAGE_PROPERTY_QUERY 의 AdditionalParameters 까지
    private const int ProtocolDataSize = 40;    // STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int HealthLogSize = 512;      // NVME_HEALTH_INFO_LOG

    public static DiskHealth? Read(int diskNumber, string busType)
    {
        var health = new DiskHealth();
        bool nvme = busType == "NVMe";
        using (var h = Open(diskNumber, 0))
        {
            if (!h.IsInvalid) Query(h, nvme, health);
        }
        if (health.FailurePredicted is null || (nvme && health.PercentageUsed is null))
        {
            // 접근 권한 0 으로 안 되는 항목은 읽기·쓰기로 다시 시도(관리자·서비스 권한에서만 열림)
            using var h = Open(diskNumber, Native.GENERIC_READ | Native.GENERIC_WRITE);
            if (!h.IsInvalid) Query(h, nvme, health);
        }
        return health.Level == "Unknown" && health.PowerOnHours is null ? null : health;
    }

    private static SafeFileHandle Open(int diskNumber, uint access) =>
        Native.CreateFile($@"\\.\PhysicalDrive{diskNumber}", access, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, 0, Native.OPEN_EXISTING, 0, 0);

    private static void Query(SafeFileHandle h, bool nvme, DiskHealth health)
    {
        if (nvme && health.PercentageUsed is null) ReadNvmeHealthLog(h, health);
        if (health.FailurePredicted is null) ReadPredictFailure(h, health, parseAta: !nvme);
    }

    private static void ReadPredictFailure(SafeFileHandle h, DiskHealth health, bool parseAta)
    {
        const int size = 4 + 512;   // STORAGE_PREDICT_FAILURE
        byte* buf = stackalloc byte[size];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_PREDICT_FAILURE, null, 0, buf, size, out uint ret, 0) || ret < 4)
            return;
        health.FailurePredicted = *(uint*)buf != 0;
        if (parseAta && ret >= size) ParseAtaAttributes(new ReadOnlySpan<byte>(buf + 4, 512), health);
    }

    /// <summary>ATA SMART 데이터: 2바이트 버전 뒤 12바이트 × 30개 속성(id, flags, 현재값, 최저값, raw 6바이트, 예약).</summary>
    internal static void ParseAtaAttributes(ReadOnlySpan<byte> data, DiskHealth health)
    {
        for (int i = 0; i < 30; i++)
        {
            var e = data.Slice(2 + i * 12, 12);
            if (e[0] == 0) continue;
            long raw = 0;
            for (int b = 5; b >= 0; b--) raw = raw << 8 | e[5 + b];
            switch (e[0])
            {
                case 5: health.ReallocatedSectors = raw; break;
                case 9 when (raw & 0xFFFFFFFF) is > 0 and < 200_000: health.PowerOnHours = raw & 0xFFFFFFFF; break;
                case 194 when (raw & 0xFF) is > 0 and < 120: health.TemperatureC = (int)(raw & 0xFF); break;
                case 197: health.PendingSectors = raw; break;
                case 198: health.UncorrectableSectors = raw; break;
            }
        }
    }

    private static void ReadNvmeHealthLog(SafeFileHandle h, DiskHealth health)
    {
        const int total = QueryHeaderSize + ProtocolDataSize + HealthLogSize;
        byte* buf = stackalloc byte[total];
        new Span<byte>(buf, total).Clear();
        *(int*)buf = 50;                         // StorageDeviceProtocolSpecificProperty
        *(int*)(buf + 4) = 0;                    // PropertyStandardQuery
        uint* spec = (uint*)(buf + QueryHeaderSize);
        spec[0] = 3;                             // ProtocolTypeNvme
        spec[1] = 2;                             // NVMeDataTypeLogPage
        spec[2] = 2;                             // NVME_LOG_PAGE_HEALTH_INFO
        spec[4] = ProtocolDataSize;              // ProtocolDataOffset
        spec[5] = HealthLogSize;                 // ProtocolDataLength
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, buf, total, buf, total, out uint ret, 0))
            return;

        // 결과: STORAGE_PROTOCOL_DATA_DESCRIPTOR(Version, Size, ProtocolSpecificData) 뒤에 로그
        if (*(uint*)buf != 48 || *(uint*)(buf + 4) != 48) return;
        uint* outSpec = (uint*)(buf + 8);
        uint offset = outSpec[4], length = outSpec[5];
        if (offset < ProtocolDataSize || length < HealthLogSize || 8 + offset + HealthLogSize > ret) return;
        ParseNvmeHealthLog(new ReadOnlySpan<byte>(buf + 8 + offset, HealthLogSize), health);
    }

    /// <summary>NVMe SMART/Health 로그 페이지(02h) 해석.</summary>
    internal static void ParseNvmeHealthLog(ReadOnlySpan<byte> log, DiskHealth health)
    {
        health.CriticalWarning = log[0];
        int kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        if (kelvin > 273) health.TemperatureC = kelvin - 273;
        health.AvailableSpare = log[3];
        health.AvailableSpareThreshold = log[4];
        health.PercentageUsed = log[5];
        health.DataWrittenTb = Math.Round(Low64(log, 48) * 512_000d / 1e12, 1);   // 단위: 512바이트 × 1000
        health.PowerOnHours = (long)Low64(log, 128);
        health.UnsafeShutdowns = (long)Low64(log, 144);
        health.MediaErrors = (long)Low64(log, 160);
    }

    // 128비트 카운터의 하위 64비트(실사용 범위에서 충분)
    private static ulong Low64(ReadOnlySpan<byte> log, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(log[offset..]);
}
