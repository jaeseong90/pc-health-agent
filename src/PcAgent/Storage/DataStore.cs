using System.Globalization;
using System.Text;
using PcAgent.Collection;

namespace PcAgent.Storage;

/// <summary>
/// 로컬 파일 저장소. DB 없이 일 단위 파일에 append 만 합니다(분당 1회, 약 150바이트).
///   data\metrics-yyyyMMdd.csv   : 1분 집계 지표 (UTC 날짜 기준)
///   data\pressure-yyyyMMdd.jsonl: 자원 부족 이벤트(시작/종료 레코드)
/// </summary>
public sealed class DataStore(string dataDir)
{
    public const string MetricsHeader =
        "minute_utc,samples,covered_sec,cpu_avg,cpu_max,mem_avail_avg,mem_avail_min,commit_avg,commit_max," +
        "hard_faults_avg,disk_active_avg,disk_active_max,disk_queue_avg,cpu_high_sec,mem_pressure_sec,disk_busy_sec";

    private const string MetricsPrefix = "metrics-";
    private const string PressurePrefix = "pressure-";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string DataDir => dataDir;

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

    private static void Append(string path, string line, string? header)
    {
        var sb = new StringBuilder(line.Length + (header?.Length ?? 0) + 4);
        if (header is not null && !File.Exists(path)) sb.Append(header).Append('\n');
        sb.Append(line).Append('\n');
        File.AppendAllText(path, sb.ToString(), Utf8NoBom);
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

    /// <summary>시작/종료 레코드를 합쳐 이벤트 목록을 반환합니다.</summary>
    public List<PressureRecord> ReadPressureEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var map = new Dictionary<Guid, PressureRecord>();
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

                if (map.TryGetValue(rec.Id, out var existing))
                {
                    if (rec.Phase == PressureRecord.PhaseEnd)
                    {
                        existing.EndedUtc = rec.EndedUtc;
                        existing.Peak = Math.Max(existing.Peak, rec.Peak);
                    }
                }
                else
                {
                    map[rec.Id] = rec;
                }
            }
        }
        return map.Values
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
        // 서비스가 쓰는 중에도 읽을 수 있도록 공유 모드로 연다
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
