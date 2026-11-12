using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PcAgent.Export;

/// <summary>
/// 여러 PC 에서 내보낸 zip 의 summary.csv 를 하나로 합칩니다(파일럿 결과 비교용).
/// 같은 PC 의 파일이 여러 개면 가장 최근 것만 사용하고, 점수가 높은(조치가 시급한) 순으로 정렬합니다.
/// </summary>
internal static class FleetMerge
{
    public static (string Path, int Count) Run(string inputDir, string? outFile)
    {
        var zips = Directory.GetFiles(inputDir, "PcHealth_*.zip", SearchOption.TopDirectoryOnly);
        if (zips.Length == 0) throw new InvalidOperationException($"'{inputDir}' 에 PcHealth_*.zip 파일이 없습니다.");

        int hostIdx = Array.IndexOf(Exporter.SummaryHeader, "PC명");
        int timeIdx = Array.IndexOf(Exporter.SummaryHeader, "내보낸시각");
        int scoreIdx = Array.IndexOf(Exporter.SummaryHeader, "점수");

        string? header = null;
        var latest = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in zips)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry("summary.csv");
                if (entry is null) continue;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var h = reader.ReadLine();
                var line = reader.ReadLine();
                if (h is null || line is null) continue;
                header ??= h;

                var fields = Csv.ParseLine(line);
                if (fields.Count <= Math.Max(hostIdx, Math.Max(timeIdx, scoreIdx))) continue;
                var host = fields[hostIdx];
                if (!latest.TryGetValue(host, out var existing) || string.CompareOrdinal(existing[timeIdx], fields[timeIdx]) < 0)
                    latest[host] = fields;
            }
            catch (InvalidDataException)
            {
                Console.Error.WriteLine($"손상된 zip 건너뜀: {Path.GetFileName(path)}");
            }
        }

        if (header is null) throw new InvalidOperationException("summary.csv 를 포함한 내보내기 파일이 없습니다.");

        outFile ??= Path.Combine(inputDir, $"fleet_summary_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        using (var writer = new StreamWriter(outFile, false, new UTF8Encoding(true)))
        {
            writer.Write(header + "\r\n");
            var rows = latest.Values.OrderByDescending(f =>
                double.TryParse(f[scoreIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : -1);
            foreach (var fields in rows)
            {
                writer.Write(string.Join(',', fields.Select(CsvWriter.Escape)) + "\r\n");
            }
        }
        return (outFile, latest.Count);
    }
}
