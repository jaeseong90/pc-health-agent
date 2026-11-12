using System.Globalization;
using System.Text;

namespace PcAgent.Export;

/// <summary>엑셀에서 바로 열 수 있는 CSV 작성기(UTF-8 BOM 은 호출 측 인코딩으로 처리).</summary>
internal sealed class CsvWriter(TextWriter writer)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public void Row(params object?[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write(Format(cells[i]));
        }
        writer.Write("\r\n");
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        string s => Escape(s),
        double d => double.IsFinite(d) ? d.ToString("0.##", Inv) : "",
        bool b => b ? "Y" : "N",
        DateTimeOffset t => t.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => Escape(value.ToString() ?? ""),
    };

    internal static string Escape(string s)
    {
        // 엑셀 수식 주입 방지
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

internal static class Csv
{
    /// <summary>따옴표를 처리하는 한 줄 파서.</summary>
    public static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
