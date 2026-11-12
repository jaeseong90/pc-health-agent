using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using PcAgent.Interop;
using PcAgent.Privacy;

namespace PcAgent.Collection;

/// <summary>
/// 자원 부족 시점의 상위 프로세스를 기록합니다. 약 1초 간격으로 두 번 측정해 CPU%·I/O 속도를 계산합니다.
/// 창 제목, 명령줄 인수, URL 등은 수집하지 않습니다.
/// </summary>
internal sealed class ProcessSnapshotter(ProcessMasker masker)
{
    private readonly Dictionary<string, string?> _publisherCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>설정 변경 시 교체됩니다.</summary>
    public ProcessMasker Masker { get; set; } = masker;

    /// <summary>Timestamp: 이 프로세스의 카운터를 읽은 시점(Stopwatch). 프로세스별 실제 측정 간격 계산용.</summary>
    private readonly record struct Counters(long CreationTime, long CpuTime, ulong IoBytes, long Timestamp);

    public async Task<List<ProcessUsage>> CaptureAsync(int topN, CancellationToken ct)
    {
        var first = new Dictionary<int, Counters>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id > 0 && TryReadCounters(p.Id, readPath: false, out var c, out _)) first[p.Id] = c;
            }
        }

        await Task.Delay(1000, ct).ConfigureAwait(false);

        var processes = Process.GetProcesses();
        int cpuCount = Math.Max(1, Environment.ProcessorCount);
        var groups = new Dictionary<(string Name, string? Path), ProcessUsage>();

        foreach (var p in processes)
        {
            using (p)
            {
                int pid;
                string name;
                long privateBytes, workingSet;
                try
                {
                    pid = p.Id;
                    if (pid == 0) continue; // Idle
                    name = p.ProcessName;
                    privateBytes = p.PrivateMemorySize64;
                    workingSet = p.WorkingSet64;
                }
                catch
                {
                    continue; // 그 사이 종료된 프로세스
                }

                double cpu = 0, io = 0;
                string? rawPath = null;
                if (TryReadCounters(pid, readPath: true, out var now, out rawPath)
                    && first.TryGetValue(pid, out var before)
                    && before.CreationTime == now.CreationTime)
                {
                    // 전체 열거 시간이 아니라 이 프로세스를 두 번 읽은 실제 간격으로 나눈다
                    double elapsed = Math.Max(0.1, Stopwatch.GetElapsedTime(before.Timestamp, now.Timestamp).TotalSeconds);
                    cpu = Math.Clamp((now.CpuTime - before.CpuTime) / 1e7 / elapsed / cpuCount * 100.0, 0, 100);
                    if (now.IoBytes >= before.IoBytes) io = (now.IoBytes - before.IoBytes) / elapsed;
                }

                var (displayName, displayPath, masked) = Masker.Apply(name, rawPath);
                var key = (displayName, displayPath);
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new ProcessUsage { Name = displayName, Path = displayPath, RawPath = masked ? null : rawPath };
                    groups[key] = g;
                }
                g.Instances++;
                g.CpuPercent += cpu;
                g.PrivateBytes += privateBytes;
                g.WorkingSetBytes += workingSet;
                g.IoBytesPerSec += io;
            }
        }

        // CPU / 메모리 / I/O 각각의 상위 N 개 합집합
        var all = groups.Values.ToList();
        var selected = new HashSet<ProcessUsage>();
        foreach (var x in all.OrderByDescending(x => x.CpuPercent).Take(topN)) selected.Add(x);
        foreach (var x in all.OrderByDescending(x => x.PrivateBytes).Take(topN)) selected.Add(x);
        foreach (var x in all.Where(x => x.IoBytesPerSec > 0).OrderByDescending(x => x.IoBytesPerSec).Take(topN)) selected.Add(x);

        var result = selected.OrderByDescending(x => x.CpuPercent).ToList();
        foreach (var x in result)
        {
            x.CpuPercent = Math.Round(Math.Min(x.CpuPercent, 100), 1);
            x.IoBytesPerSec = Math.Round(x.IoBytesPerSec);
            x.Publisher = GetPublisher(x.RawPath);
        }
        return result;
    }

    private static unsafe bool TryReadCounters(int pid, bool readPath, out Counters counters, out string? path)
    {
        counters = default;
        path = null;
        using SafeProcessHandle h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return false;
        if (!Native.GetProcessTimes(h, out long creation, out _, out long kernel, out long user)) return false;

        ulong io = 0;
        if (Native.GetProcessIoCounters(h, out var ioc)) io = ioc.ReadTransferCount + ioc.WriteTransferCount;
        counters = new Counters(creation, kernel + user, io, Stopwatch.GetTimestamp());

        if (readPath)
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            if (Native.QueryFullProcessImageName(h, 0, buffer, ref size)) path = new string(buffer, 0, (int)size);
        }
        return true;
    }

    private string? GetPublisher(string? rawPath)
    {
        // 네트워크 경로(UNC, WebDAV, 네트워크 드라이브)는 읽지 않는다: SYSTEM 이 파일을 열면
        // PC 계정으로 원격 서버에 인증하게 되어 NTLM 릴레이 공격에 악용될 수 있음
        if (rawPath is null || !IsLocalFixedDrivePath(rawPath)) return null;
        if (_publisherCache.TryGetValue(rawPath, out var cached)) return cached;

        string? publisher = null;
        try
        {
            publisher = FileVersionInfo.GetVersionInfo(rawPath).CompanyName?.Trim();
            if (string.IsNullOrEmpty(publisher)) publisher = null;
        }
        catch
        {
            // 접근 불가 파일
        }

        if (_publisherCache.Count > 2000) _publisherCache.Clear();
        _publisherCache[rawPath] = publisher;
        return publisher;
    }

    /// <summary>"C:\..." 형태이고 해당 드라이브가 로컬 고정 디스크인 경우만 true.</summary>
    internal static bool IsLocalFixedDrivePath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Fixed;
        }
        catch
        {
            return false;
        }
    }
}
