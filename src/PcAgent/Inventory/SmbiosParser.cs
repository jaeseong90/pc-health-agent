using System.Buffers.Binary;
using System.Text;

namespace PcAgent.Inventory;

public sealed class SmbiosInfo
{
    public string? BiosVendor { get; set; }
    public string? BiosVersion { get; set; }
    public string? BiosReleaseDate { get; set; }
    public string? SystemManufacturer { get; set; }
    public string? SystemProduct { get; set; }
    public string? SystemSerial { get; set; }
    public string? CpuName { get; set; }
    public int CpuSockets { get; set; }
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public int CpuMaxMhz { get; set; }
    /// <summary>메모리 슬롯 수(온보드 포함).</summary>
    public int MemorySlots { get; set; }
    /// <summary>장착된 모듈만(빈 슬롯 제외).</summary>
    public List<MemoryModule> Modules { get; } = [];
    public int EmptySlotEntries { get; set; }
}

/// <summary>
/// GetSystemFirmwareTable('RSMB') 결과(SMBIOS 원시 테이블)를 파싱합니다. WMI 없이 BIOS/모델/CPU/메모리 슬롯 정보를 얻습니다.
/// </summary>
public static class SmbiosParser
{
    public static SmbiosInfo Parse(ReadOnlySpan<byte> raw)
    {
        var info = new SmbiosInfo();
        if (raw.Length < 8) return info;

        int tableLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        int end = Math.Min(raw.Length, 8 + tableLength);
        int offset = 8;
        var strings = new List<string>(8);

        while (offset + 4 <= end)
        {
            byte type = raw[offset];
            byte length = raw[offset + 1];
            if (length < 4 || offset + length > end) break;
            var f = raw.Slice(offset, length);

            // 문자열 영역: 포맷 영역 뒤, 이중 null 로 끝남
            strings.Clear();
            int p = offset + length;
            if (p + 1 < end && raw[p] == 0 && raw[p + 1] == 0)
            {
                p += 2;
            }
            else
            {
                while (p < end)
                {
                    int s = p;
                    while (p < end && raw[p] != 0) p++;
                    strings.Add(Encoding.Latin1.GetString(raw[s..p]).Trim());
                    p++;
                    if (p < end && raw[p] == 0)
                    {
                        p++;
                        break;
                    }
                }
            }

            switch (type)
            {
                case 0: // BIOS
                    info.BiosVendor = Str(f, 0x04, strings);
                    info.BiosVersion = Str(f, 0x05, strings);
                    info.BiosReleaseDate = Str(f, 0x08, strings);
                    break;
                case 1: // System
                    info.SystemManufacturer = Str(f, 0x04, strings);
                    info.SystemProduct = Str(f, 0x05, strings);
                    info.SystemSerial = Str(f, 0x07, strings);
                    break;
                case 4: // Processor
                    ParseProcessor(f, strings, info);
                    break;
                case 16: // Physical Memory Array
                    if (f.Length > 0x0E && f[0x05] == 0x03) // Use = System memory
                        info.MemorySlots += BinaryPrimitives.ReadUInt16LittleEndian(f[0x0D..]);
                    break;
                case 17: // Memory Device
                    ParseMemoryDevice(f, strings, info);
                    break;
            }

            if (type == 127) break;
            offset = p;
        }
        return info;
    }

    private static void ParseProcessor(ReadOnlySpan<byte> f, List<string> strings, SmbiosInfo info)
    {
        if (f.Length > 0x18 && (f[0x18] & 0x40) == 0) return; // 소켓 비어 있음
        info.CpuSockets++;
        info.CpuName ??= Str(f, 0x10, strings);
        if (f.Length > 0x15)
            info.CpuMaxMhz = Math.Max(info.CpuMaxMhz, BinaryPrimitives.ReadUInt16LittleEndian(f[0x14..]));
        if (f.Length > 0x23)
        {
            int cores = f[0x23];
            if (cores == 0xFF && f.Length > 0x2B) cores = BinaryPrimitives.ReadUInt16LittleEndian(f[0x2A..]);
            info.CpuCores += cores;
        }
        if (f.Length > 0x25)
        {
            int threads = f[0x25];
            if (threads == 0xFF && f.Length > 0x2F) threads = BinaryPrimitives.ReadUInt16LittleEndian(f[0x2E..]);
            info.CpuThreads += threads;
        }
    }

    private static void ParseMemoryDevice(ReadOnlySpan<byte> f, List<string> strings, SmbiosInfo info)
    {
        if (f.Length < 0x15) return;
        int size = BinaryPrimitives.ReadUInt16LittleEndian(f[0x0C..]);
        if (size == 0)
        {
            info.EmptySlotEntries++;
            return;
        }
        if (size == 0xFFFF) return; // 알 수 없음

        long sizeMb;
        if (size == 0x7FFF && f.Length > 0x1F)
            sizeMb = BinaryPrimitives.ReadUInt32LittleEndian(f[0x1C..]) & 0x7FFFFFFF;
        else if ((size & 0x8000) != 0)
            sizeMb = (size & 0x7FFF) / 1024; // KB 단위
        else
            sizeMb = size;

        var module = new MemoryModule
        {
            SizeMb = sizeMb,
            FormFactor = FormFactorName(f[0x0E]),
            Locator = Str(f, 0x10, strings),
            Type = MemoryTypeName(f[0x12]),
        };
        if (f.Length > 0x16)
        {
            int speed = BinaryPrimitives.ReadUInt16LittleEndian(f[0x15..]);
            if (speed is > 0 and < 0xFFFF) module.SpeedMts = speed;
        }
        if (f.Length > 0x17) module.Manufacturer = Str(f, 0x17, strings);
        if (f.Length > 0x1A) module.PartNumber = Str(f, 0x1A, strings);
        if (f.Length > 0x21)
        {
            int configured = BinaryPrimitives.ReadUInt16LittleEndian(f[0x20..]);
            if (configured is > 0 and < 0xFFFF) module.ConfiguredSpeedMts = configured;
        }
        info.Modules.Add(module);
    }

    private static string? Str(ReadOnlySpan<byte> f, int fieldOffset, List<string> strings)
    {
        if (fieldOffset >= f.Length) return null;
        int index = f[fieldOffset];
        if (index == 0 || index > strings.Count) return null;
        return Clean(strings[index - 1]);
    }

    /// <summary>제조사가 채우지 않은 자리표시 문자열은 null 로.</summary>
    internal static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        string[] junk =
        [
            "To Be Filled By O.E.M.", "To be filled by O.E.M.", "Default string", "System Serial Number",
            "System Product Name", "System manufacturer", "Not Specified", "Not Available", "None", "0", "Unknown",
        ];
        return junk.Any(j => string.Equals(j, s, StringComparison.OrdinalIgnoreCase)) ? null : s;
    }

    private static string FormFactorName(byte v) => v switch
    {
        0x05 => "Chip",
        0x09 => "DIMM",
        0x0B => "Row of chips",
        0x0D => "SODIMM",
        0x0F => "FB-DIMM",
        0x10 => "Die",
        _ => "Other",
    };

    private static string MemoryTypeName(byte v) => v switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => "Other",
    };

    /// <summary>온보드(납땜) 메모리로 추정되는 폼팩터.</summary>
    public static bool IsSoldered(string? formFactor) => formFactor is "Chip" or "Row of chips" or "Die";
}
