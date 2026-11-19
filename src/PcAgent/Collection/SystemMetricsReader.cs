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
    private readonly nint? _diskLatency;
    private readonly string _systemDrive;

    /// <summary>
    /// CPU 속도는 별도 쿼리로 30초에 한 번만 읽습니다. 이 카운터는 1회 수집에 약 0.2ms 가 들어,
    /// 5초마다 읽으면 에이전트 CPU 사용이 약 9% 늘어납니다(실측). 값 자체가 '직전 수집 이후 명령을 실행한 동안의
    /// 평균 속도'라서 간격을 늘려도 놓치는 구간이 없습니다.
    /// </summary>
    private const long CpuPerfIntervalMs = 30_000;
    private readonly PdhQuery? _perfPdh;
    private readonly nint? _cpuPerf;
    private long _lastPerfTick;

    public SystemMetricsReader()
    {
        Native.GetSystemTimes(out _idle, out _kernel, out _user);
        _systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        // 보조 지표라 없어도 경고하지 않음(가상 머신 등에는 이 카운터가 없음)
        _perfPdh = PdhQuery.TryOpen();
        _cpuPerf = _perfPdh?.TryAdd(@"\Processor Information(_Total)\% Processor Performance");
        if (_cpuPerf is null)
        {
            _perfPdh?.Dispose();
            _perfPdh = null;
        }
        else
        {
            _perfPdh!.Collect();
            _lastPerfTick = Environment.TickCount64;
        }

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
        // 디스크 응답 시간은 위와 같은 PhysicalDisk 개체라 추가 비용이 거의 없음(보조 지표라 없어도 경고하지 않음).
        // 새 카운터를 넣을 때는 1회 수집 비용을 먼저 잴 것 — \System\Processor Queue Length 는 1회 4ms 이상이라 쓰지 않음.
        _diskLatency = _pdh.TryAdd(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
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
        double? commitGb = null;
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
        if (Native.GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys > 0)
        {
            availPct = mem.ullAvailPhys * 100.0 / mem.ullTotalPhys;
            if (mem.ullTotalPageFile > 0)
            {
                ulong commit = mem.ullTotalPageFile - mem.ullAvailPageFile;
                commitPct = commit * 100.0 / mem.ullTotalPageFile;
                commitGb = commit / 1073741824.0;
            }
        }

        double? hardFaults = null, diskActive = null, diskQueue = null, diskLatency = null, cpuPerf = null;
        if (_pdh is not null && _pdh.Collect())
        {
            if (_pageReads is { } pr) hardFaults = PdhQuery.GetValue(pr);
            if (_diskIdle is { } di && _pdh.GetInstanceValue(di, _systemDrive) is { } idlePct)
                diskActive = Math.Clamp(100 - idlePct, 0, 100);
            if (_diskQueue is { } dq) diskQueue = _pdh.GetInstanceValue(dq, _systemDrive);
            if (_diskLatency is { } dl && _pdh.GetInstanceValue(dl, _systemDrive) is { } sec)
                diskLatency = Math.Clamp(sec * 1000, 0, 60_000);
        }

        // 30초에 한 번(샘플 주기의 흔들림을 감안해 0.5초 여유). 나머지 샘플은 null
        long tick = Environment.TickCount64;
        if (_perfPdh is not null && _cpuPerf is { } cp && tick - _lastPerfTick >= CpuPerfIntervalMs - 500)
        {
            _lastPerfTick = tick;
            if (_perfPdh.Collect() && PdhQuery.GetValue(cp) is { } perf && perf > 0)
                cpuPerf = Math.Min(perf, 400);
        }

        bool? onBattery = null;
        if (Native.GetSystemPowerStatus(out var power) && power.ACLineStatus <= 1)
            onBattery = power.ACLineStatus == 0;

        return new SystemSample(now, cpu, availPct, commitPct, hardFaults, diskActive, diskQueue, commitGb, cpuPerf, diskLatency, onBattery);
    }

    public void Dispose()
    {
        _pdh?.Dispose();
        _perfPdh?.Dispose();
    }
}
