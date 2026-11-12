using PcAgent.Interop;

namespace PcAgent.Collection;

/// <summary>
/// 시스템 지표 수집. CPU/메모리는 커널 API(마이크로초 단위 비용), 디스크/페이지 폴트는 PDH 를 사용합니다.
/// </summary>
internal sealed unsafe class SystemMetricsReader : IDisposable
{
    private long _idle, _kernel, _user;
    private readonly PdhQuery? _pdh;
    private readonly nint? _pageReads;
    private readonly nint? _diskIdle;
    private readonly nint? _diskQueue;
    private readonly string _systemDrive;

    public SystemMetricsReader()
    {
        Native.GetSystemTimes(out _idle, out _kernel, out _user);
        _systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        _pdh = PdhQuery.TryOpen();
        if (_pdh is null)
        {
            Log.Warn("성능 카운터(PDH)를 열 수 없어 디스크/페이지 폴트 지표 없이 동작합니다.");
            return;
        }
        _pageReads = _pdh.TryAdd(@"\Memory\Page Reads/sec");
        _diskIdle = _pdh.TryAdd(@"\PhysicalDisk(*)\% Idle Time");
        _diskQueue = _pdh.TryAdd(@"\PhysicalDisk(*)\Avg. Disk Queue Length");
        if (_pageReads is null || _diskIdle is null || _diskQueue is null)
        {
            Log.Warn("일부 성능 카운터를 사용할 수 없습니다(카운터 손상 시 관리자 cmd 에서 'lodctr /r' 로 복구).");
        }
        _pdh.Collect(); // 비율 카운터는 두 번째 수집부터 값이 나옴
    }

    public SystemSample Read()
    {
        var now = DateTimeOffset.UtcNow;

        double cpu = 0;
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long dIdle = idle - _idle;
            long total = (kernel - _kernel) + (user - _user); // kernel 에 idle 포함
            if (total > 0) cpu = Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
            (_idle, _kernel, _user) = (idle, kernel, user);
        }

        double availPct = 100, commitPct = 0;
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
        if (Native.GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            availPct = mem.ullAvailPhys * 100.0 / mem.ullTotalPhys;
            if (mem.ullTotalPageFile > 0)
                commitPct = (mem.ullTotalPageFile - mem.ullAvailPageFile) * 100.0 / mem.ullTotalPageFile;
        }

        double? hardFaults = null, diskActive = null, diskQueue = null;
        if (_pdh is not null && _pdh.Collect())
        {
            if (_pageReads is { } pr) hardFaults = PdhQuery.GetValue(pr);
            if (_diskIdle is { } di && _pdh.GetInstanceValue(di, _systemDrive) is { } idlePct)
                diskActive = Math.Clamp(100 - idlePct, 0, 100);
            if (_diskQueue is { } dq) diskQueue = _pdh.GetInstanceValue(dq, _systemDrive);
        }

        return new SystemSample(now, cpu, availPct, commitPct, hardFaults, diskActive, diskQueue);
    }

    public void Dispose() => _pdh?.Dispose();
}
