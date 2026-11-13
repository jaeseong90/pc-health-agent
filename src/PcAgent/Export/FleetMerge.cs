using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PcAgent.Export;

/// <summary>
/// 여러 PC 에서 내보낸 zip 을 하나로 합칩니다(파일럿 결과 비교용).
/// 각 zip 의 summary.json 을 읽어 현재 버전의 열 구성으로 다시 쓰므로, 에이전트 버전이 섞여 있어도 열이 어긋나지 않습니다.
/// 같은 PC 의 파일이 여러 개면 가장 최근 것만 사용하고, 점수가 높은(조치가 시급한) 순으로 정렬합니다.
/// 결과: fleet_summary_*.csv(엑셀용) + fleet_summary_*.html(한눈에 보는 리포트)
/// </summary>
internal static class FleetMerge
{
    private const long MaxSummaryBytes = 20 * 1024 * 1024;

    public sealed record Result(string CsvPath, string HtmlPath, int Count, List<string> Skipped);

    public static Result Run(string inputDir, string? outFile)
    {
        var zips = Directory.GetFiles(inputDir, "PcHealth_*.zip", SearchOption.TopDirectoryOnly);
        if (zips.Length == 0) throw new InvalidOperationException($"'{inputDir}' 에 PcHealth_*.zip 파일이 없습니다.");

        var latest = new Dictionary<string, ExportSummary>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        foreach (var path in zips)
        {
            var s = TryRead(path);
            if (s is null || string.IsNullOrWhiteSpace(s.Inventory.Hostname))
            {
                skipped.Add(Path.GetFileName(path));
                continue;
            }
            if (!latest.TryGetValue(s.Inventory.Hostname, out var existing) || existing.GeneratedUtc < s.GeneratedUtc)
                latest[s.Inventory.Hostname] = s;
        }
        if (latest.Count == 0) throw new InvalidOperationException("summary.json 을 읽을 수 있는 내보내기 파일이 없습니다.");

        var rows = latest.Values
            .OrderByDescending(s => s.Analysis.Score)
            .ThenBy(s => s.Inventory.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var csvPath = outFile ?? Path.Combine(inputDir, $"fleet_summary_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        var htmlPath = Path.ChangeExtension(csvPath, ".html");
        using (var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
        {
            var csv = new CsvWriter(writer);
            csv.Row(Exporter.SummaryHeader);
            foreach (var s in rows) Exporter.WriteSummaryRow(csv, s, TimeZoneOf(s));
        }
        using (var writer = new StreamWriter(htmlPath, false, new UTF8Encoding(false)))
        {
            FleetReport.Write(writer, rows, skipped);
        }
        return new Result(csvPath, htmlPath, rows.Count, skipped);
    }

    private static ExportSummary? TryRead(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("summary.json");
            if (entry is null || entry.Length > MaxSummaryBytes) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Json.Deserialize(reader.ReadToEnd(), AgentJsonContext.Default.ExportSummary);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>내보낸 PC 의 시간대(없거나 모르면 이 PC 의 시간대).</summary>
    internal static TimeZoneInfo TimeZoneOf(ExportSummary s)
    {
        try
        {
            return string.IsNullOrEmpty(s.TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
