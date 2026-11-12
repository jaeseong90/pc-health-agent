using System.Globalization;
using System.Text;
using PcAgent.Collection;

namespace PcAgent.Storage;

/// <summary>
/// 로컬 파일 저장소. DB 없이 일 단위 파일에 append 만 합니다(분당 1회, 약 150바이트).
///   data\metrics-yyyyMMdd.csv   : 1분 집계 지표 (UTC 날짜 기준)
///   data\pressure-yyyyMMdd.jsonl: 자원 부족 이벤트(시작/종료 레코드)
/// 사용자가 파일을 엑셀 등으로 열어 잠그면 줄을 메모리에 모아 두었다가 잠금이 풀리면 씁니다.
/// </summary>
public sealed class DataStore(string dataDir)
{
    public const string MetricsHeader =
        "minute_utc,samples,covered_sec,cpu_avg,cpu_max,mem_avail_avg,mem_avail_min,commit_avg,commit_max," +
        "hard_faults_avg,disk_active_avg,disk_active_max,disk_queue_avg,cpu_high_sec,mem_pressure_sec,disk_busy_sec";

    private const string MetricsPrefix = "metrics-";
    private const string PressurePrefix = "pressure-";
    private const int MaxPendingChars = 2 * 1024 * 1024;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Pending(string? Header, StringBuilder Lines);

    public string DataDir => dataDir;

    /// <summary>잠금 때문에 아직 쓰지 못한 줄이 있는 파일 수.</summary>
    public int PendingFiles => _pending.Count;

    // ---------------- 쓰기 ----------------

    public void AppendMetric(MetricMinute m)
    {
        var path = Path.Combine(dataDir, $"{MetricsPrefix}{m.MinuteUtc:yyyyMMdd}.csv");
        var line = string.Join(',',
            m.MinuteUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv),
            m.Samples.ToString(Inv),
            m.CoveredSeconds.ToString(Inv),
            F(m.CpuAvg), F(m.CpuMax),
            F(m.MemAvailAvg), F(m.MemAvailMin),
            F(m.CommitAvg), F(m.CommitMax),
            F(m.HardFaultsAvg), F(m.DiskActiveAvg), F(m.DiskActiveMax), F(m.DiskQueueAvg),
            m.CpuHighSeconds.ToString(Inv),
            m.MemPressureSeconds.ToString(Inv),
            m.DiskBusySeconds.ToString(Inv));
        Append(path, line, MetricsHeader);
    }

    public void AppendPressure(PressureRecord record)
    {
        var path = Path.Combine(dataDir, $"{PressurePrefix}{record.StartedUtc:yyyyMMdd}.jsonl");
        Append(path, Json.Serialize(record, AgentJsonContext.Default.PressureRecord), header: null);
    }

    /// <summary>밀려 있는 줄을 다시 써 봅니다(종료 시에도 호출).</summary>
    public void FlushPending(string? except = null)
    {
        if (_pending.Count == 0) return;
        foreach (var path in _pending.Keys.ToList())
        {
            if (except is not null && string.Equals(path, except, StringComparison.OrdinalIgnoreCase)) continue;
            var p = _pending[path];
            if (TryAppend(path, p.Lines.ToString(), p.Header))
            {
                _pending.Remove(path);
                Log.Info($"잠금이 풀려 밀린 기록을 저장했습니다: {Path.GetFileName(path)}");
            }
        }
    }

    private void Append(string path, string line, string? header)
    {
        FlushPending(except: path);

        var text = new StringBuilder();
        bool wasPending = _pending.Remove(path, out var pending);
        if (wasPending) text.Append(pending!.Lines);
        text.Append(line).Append('\n');

        if (TryAppend(path, text.ToString(), header))
        {
            if (wasPending) Log.Info($"잠금이 풀려 밀린 기록을 저장했습니다: {Path.GetFileName(path)}");
            return;
        }

        // 너무 오래 잠겨 있으면 오래된 줄부터 버림(줄 단위)
        int excess = text.Length - MaxPendingChars;
        if (excess > 0)
        {
            int cut = text.ToString().IndexOf('\n', excess);
            text.Remove(0, cut < 0 ? text.Length : cut + 1);
        }
        _pending[path] = new Pending(header, text);
        if (!wasPending) Log.Warn($"다른 프로그램이 파일을 잠가 기록을 잠시 메모리에 보관합니다: {Path.GetFileName(path)} (엑셀 등으로 열려 있으면 닫아 주세요)");
    }

    /// <summary>공유 위반(다른 프로세스의 잠금)만 false, 그 외 오류는 예외.</summary>
    private static bool TryAppend(string path, string text, string? header)
    {
        try
        {
            var content = header is not null && !File.Exists(path) ? header + "\n" + text : text;
            File.AppendAllText(path, content, Utf8NoBom);
            return true;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        {
            return false;
        }
    }

    // ---------------- 읽기 (내보내기 시) ----------------

    public List<MetricMinute> ReadMetrics(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var result = new List<MetricMinute>();
        foreach (var file in FilesInRange(MetricsPrefix, ".csv", fromUtc, toUtc))
        {
            foreach (var line in ReadLinesShared(file))
            {
                if (line.Length == 0 || line.StartsWith("minute_utc", StringComparison.Ordinal)) continue;
                if (TryParseMetric(line, out var m) && m.MinuteUtc >= fromUtc && m.MinuteUtc <= toUtc) result.Add(m);
            }
        }
        result.Sort((a, b) => a.MinuteUtc.CompareTo(b.MinuteUtc));
        return result;
    }

    /// <summary>
    /// 시작/종료 레코드를 합쳐 이벤트 목록을 반환합니다. 레코드 순서와 무관하게 동작하며,
    /// 시작 레코드가 없는 종료 레코드(시작 기록 전에 서비스가 멈춘 경우 등)는 버립니다.
    /// </summary>
    public List<PressureRecord> ReadPressureEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var starts = new Dictionary<Guid, PressureRecord>();
        var ends = new Dictionary<Guid, PressureRecord>();
        foreach (var file in FilesInRange(PressurePrefix, ".jsonl", fromUtc.AddDays(-1), toUtc))
        {
            foreach (var line in ReadLinesShared(file))
            {
                if (line.Length == 0) continue;
                PressureRecord? rec;
                try
                {
                    rec = Json.Deserialize(line, AgentJsonContext.Default.PressureRecord);
                }
                catch
                {
                    continue; // 전원 차단 등으로 잘린 줄
                }
                if (rec is null) continue;
                if (rec.Phase == PressureRecord.PhaseEnd) ends[rec.Id] = rec;
                else starts.TryAdd(rec.Id, rec);
            }
        }

        foreach (var start in starts.Values)
        {
            if (!ends.TryGetValue(start.Id, out var end)) continue;
            start.EndedUtc = end.EndedUtc;
            start.Peak = Math.Max(start.Peak, end.Peak);
        }
        return starts.Values
            .Where(e => e.StartedUtc >= fromUtc && e.StartedUtc <= toUtc)
            .OrderBy(e => e.StartedUtc)
            .ToList();
    }

    public MetricMinute? ReadLastMetric()
    {
        var newest = ListFiles(MetricsPrefix, ".csv").OrderByDescending(f => f.Date).FirstOrDefault();
        if (newest.Path is null) return null;
        var last = ReadLinesShared(newest.Path).LastOrDefault(l => l.Length > 0 && !l.StartsWith("minute_utc", StringComparison.Ordinal));
        return last is not null && TryParseMetric(last, out var m) ? m : null;
    }

    // ---------------- 정리 ----------------

    public void Cleanup(int retentionDays, long maxBytes)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);
        var files = ListFiles(MetricsPrefix, ".csv").Concat(ListFiles(PressurePrefix, ".jsonl"))
            .OrderBy(f => f.Date)
            .ToList();

        long total = 0;
        var kept = new List<(string Path, DateOnly Date, long Size)>();
        foreach (var f in files)
        {
            if (f.Date < cutoff)
            {
                TryDelete(f.Path);
                continue;
            }
            long size = new FileInfo(f.Path).Length;
            total += size;
            kept.Add((f.Path, f.Date, size));
        }

        // 용량 상한 초과 시 오래된 파일부터 삭제 (오늘 파일은 유지)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var f in kept)
        {
            if (total <= maxBytes || f.Date >= today) break;
            TryDelete(f.Path);
            total -= f.Size;
        }
    }

    public (int Files, long Bytes, DateOnly? Oldest, DateOnly? Newest) GetStats()
    {
        var files = ListFiles(MetricsPrefix, ".csv").Concat(ListFiles(PressurePrefix, ".jsonl")).ToList();
        if (files.Count == 0) return (0, 0, null, null);
        long bytes = files.Sum(f => new FileInfo(f.Path).Length);
        return (files.Count, bytes, files.Min(f => f.Date), files.Max(f => f.Date));
    }

    // ---------------- 내부 ----------------

    private IEnumerable<(string Path, DateOnly Date)> ListFiles(string prefix, string ext)
    {
        if (!Directory.Exists(dataDir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dataDir, $"{prefix}*{ext}"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (DateOnly.TryParseExact(name.AsSpan(prefix.Length), "yyyyMMdd", Inv, DateTimeStyles.None, out var date))
                yield return (path, date);
        }
    }

    private IEnumerable<string> FilesInRange(string prefix, string ext, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var from = DateOnly.FromDateTime(fromUtc.UtcDateTime);
        var to = DateOnly.FromDateTime(toUtc.UtcDateTime);
        return ListFiles(prefix, ext).Where(f => f.Date >= from && f.Date <= to).OrderBy(f => f.Date).Select(f => f.Path);
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        // 서비스가 쓰는 중이거나 다른 프로그램이 열어 둔 상태에서도 읽을 수 있도록 공유 모드로 연다
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    internal static bool TryParseMetric(string line, out MetricMinute m)
    {
        m = new MetricMinute();
        var c = line.Split(',');
        if (c.Length < 16) return false;
        if (!DateTimeOffset.TryParse(c[0], Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var minute)) return false;
        try
        {
            m.MinuteUtc = minute;
            m.Samples = int.Parse(c[1], Inv);
            m.CoveredSeconds = int.Parse(c[2], Inv);
            m.CpuAvg = D(c[3]);
            m.CpuMax = D(c[4]);
            m.MemAvailAvg = D(c[5]);
            m.MemAvailMin = D(c[6]);
            m.CommitAvg = D(c[7]);
            m.CommitMax = D(c[8]);
            m.HardFaultsAvg = ND(c[9]);
            m.DiskActiveAvg = ND(c[10]);
            m.DiskActiveMax = ND(c[11]);
            m.DiskQueueAvg = ND(c[12]);
            m.CpuHighSeconds = int.Parse(c[13], Inv);
            m.MemPressureSeconds = int.Parse(c[14], Inv);
            m.DiskBusySeconds = int.Parse(c[15], Inv);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 사용 중이면 다음 정리 때 */ }
    }

    private static string F(double v) => v.ToString("0.##", Inv);
    private static string F(double? v) => v is { } x ? x.ToString("0.##", Inv) : "";
    private static double D(string s) => double.Parse(s, Inv);
    private static double? ND(string s) => s.Length == 0 ? null : double.Parse(s, Inv);
}
