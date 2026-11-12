using System.Runtime.InteropServices;

namespace PcAgent.Interop;

/// <summary>
/// 성능 카운터(PDH). PdhAddEnglishCounter 를 사용하므로 한글 Windows 에서도 영문 카운터 이름이 동작합니다.
/// </summary>
internal static partial class Pdh
{
    internal const uint ERROR_SUCCESS = 0;
    internal const uint PDH_MORE_DATA = 0x800007D2;
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_CSTATUS_VALID_DATA = 0;
    internal const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Explicit)]
    internal struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public nint Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    internal static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    internal static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [LibraryImport("pdh.dll")]
    internal static partial uint PdhCloseQuery(nint query);
}

/// <summary>PDH 쿼리 래퍼. 버퍼를 재사용해 샘플링마다 할당이 생기지 않게 합니다.</summary>
internal sealed unsafe class PdhQuery : IDisposable
{
    private nint _query;
    private nint _arrayBuffer;
    private uint _arrayBufferSize;

    private PdhQuery(nint query) => _query = query;

    public static PdhQuery? TryOpen() =>
        Pdh.PdhOpenQuery(null, 0, out var query) == Pdh.ERROR_SUCCESS ? new PdhQuery(query) : null;

    public nint? TryAdd(string englishPath) =>
        Pdh.PdhAddEnglishCounter(_query, englishPath, 0, out var counter) == Pdh.ERROR_SUCCESS ? counter : null;

    public bool Collect() => _query != 0 && Pdh.PdhCollectQueryData(_query) == Pdh.ERROR_SUCCESS;

    public static double? GetValue(nint counter)
    {
        if (Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_DOUBLE, out _, out var value) != Pdh.ERROR_SUCCESS)
            return null;
        return value.CStatus is Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA ? value.DoubleValue : null;
    }

    /// <summary>
    /// 와일드카드 인스턴스 카운터에서 지정 드라이브(예: "C:")가 포함된 인스턴스 값을 반환합니다. 없으면 _Total.
    /// </summary>
    public double? GetInstanceValue(nint counter, string driveToken)
    {
        uint size = _arrayBufferSize;
        uint status = Pdh.PdhGetFormattedCounterArray(counter, Pdh.PDH_FMT_DOUBLE, ref size, out uint count, _arrayBuffer);
        if (status == Pdh.PDH_MORE_DATA)
        {
            if (_arrayBuffer != 0) Marshal.FreeHGlobal(_arrayBuffer);
            _arrayBufferSize = size + 256;
            _arrayBuffer = Marshal.AllocHGlobal((int)_arrayBufferSize);
            size = _arrayBufferSize;
            status = Pdh.PdhGetFormattedCounterArray(counter, Pdh.PDH_FMT_DOUBLE, ref size, out count, _arrayBuffer);
        }
        if (status != Pdh.ERROR_SUCCESS || count == 0) return null;

        var items = (Pdh.PDH_FMT_COUNTERVALUE_ITEM*)_arrayBuffer;
        double? total = null;
        for (int i = 0; i < count; i++)
        {
            var item = items[i];
            if (item.Value.CStatus is not (Pdh.PDH_CSTATUS_VALID_DATA or Pdh.PDH_CSTATUS_NEW_DATA)) continue;
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)item.Name);
            if (name.SequenceEqual("_Total"))
            {
                total = item.Value.DoubleValue;
                continue;
            }
            // 인스턴스 이름 예: "0 C:", "1 D: E:"
            int idx = name.IndexOf(driveToken, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && name[idx - 1] == ' ')
            {
                return item.Value.DoubleValue;
            }
        }
        return total;
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            Pdh.PdhCloseQuery(_query);
            _query = 0;
        }
        if (_arrayBuffer != 0)
        {
            Marshal.FreeHGlobal(_arrayBuffer);
            _arrayBuffer = 0;
        }
    }
}
