using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using PcAgent.Interop;
using PcAgent.Privacy;

namespace PcAgent.EventLogs;

public sealed class EventLogItem
{
    public DateTimeOffset TimeUtc { get; set; }
    /// <summary>AppCrash, AppHang, BugCheck, UnexpectedShutdown, LowMemory, Boot</summary>
    public string Category { get; set; } = "";
    public string Provider { get; set; } = "";
    public int EventId { get; set; }
    public int Level { get; set; }
    public string? Detail { get; set; }
    /// <summary>Boot: 부팅 시간(ms).</summary>
    public double? Value { get; set; }
}

/// <summary>
/// Windows 이벤트 로그에서 안정성 관련 이벤트를 내보내기 시점에 조회합니다.
/// Windows 가 이미 기록하고 있으므로 에이전트가 상시 수집할 필요가 없습니다.
/// </summary>
internal static unsafe class EventLogCollector
{
    private const int MaxPerChannel = 3000;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private sealed record Source(string Channel, int[] EventIds, Func<string, int, string?> Classify);

    private static readonly Source[] Sources =
    [
        new("System", [41, 1001, 2004], (provider, id) => (provider, id) switch
        {
            ("Microsoft-Windows-Kernel-Power", 41) => "UnexpectedShutdown",
            ("Microsoft-Windows-WER-SystemErrorReporting", 1001) => "BugCheck",
            ("Microsoft-Windows-Resource-Exhaustion-Detector", 2004) => "LowMemory",
            _ => null,
        }),
        new("Application", [1000, 1002], (provider, id) => (provider, id) switch
        {
            ("Application Error", 1000) => "AppCrash",
            ("Application Hang", 1002) => "AppHang",
            _ => null,
        }),
        new("Microsoft-Windows-Diagnostics-Performance/Operational", [100], (_, id) => id == 100 ? "Boot" : null),
    ];

    public static List<EventLogItem> Collect(DateTimeOffset fromUtc, ProcessMasker masker, List<string> warnings)
    {
        var items = new List<EventLogItem>();
        long ms = Math.Max(1, (long)(DateTimeOffset.UtcNow - fromUtc).TotalMilliseconds);
        foreach (var source in Sources)
        {
            var ids = string.Join(" or ", source.EventIds.Select(i => $"EventID={i}"));
            var query = $"*[System[({ids}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            try
            {
                ReadChannel(source, query, masker, items, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add($"이벤트 로그({source.Channel}) 조회 실패: {ex.Message}");
            }
        }
        items.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        return items;
    }

    private static void ReadChannel(Source source, string query, ProcessMasker masker, List<EventLogItem> items, List<string> warnings)
    {
        nint resultSet = Native.EvtQuery(0, source.Channel, query, Native.EvtQueryChannelPath | Native.EvtQueryReverseDirection);
        if (resultSet == 0)
        {
            int err = Marshal.GetLastPInvokeError();
            warnings.Add(err == 5
                ? $"이벤트 로그({source.Channel}) 접근 권한 없음 — 관리자 권한으로 내보내면 포함됩니다."
                : $"이벤트 로그({source.Channel}) 조회 불가 (오류 {err})");
            return;
        }

        try
        {
            const int batch = 32;
            nint* handles = stackalloc nint[batch];
            var buffer = new char[16 * 1024];
            int count = 0;
            while (count < MaxPerChannel && Native.EvtNext(resultSet, batch, handles, 1000, 0, out uint returned))
            {
                for (int i = 0; i < returned; i++)
                {
                    try
                    {
                        var xml = Render(handles[i], ref buffer);
                        if (xml is not null && Parse(xml, source, masker) is { } item)
                        {
                            items.Add(item);
                            count++;
                        }
                    }
                    finally
                    {
                        Native.EvtClose(handles[i]);
                    }
                }
            }
        }
        finally
        {
            Native.EvtClose(resultSet);
        }
    }

    private static string? Render(nint handle, ref char[] buffer)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            fixed (char* p = buffer)
            {
                if (Native.EvtRender(0, handle, Native.EvtRenderEventXml, (uint)(buffer.Length * sizeof(char)), p, out uint used, out _))
                    return new string(p);
                if (Marshal.GetLastPInvokeError() != Native.ERROR_INSUFFICIENT_BUFFER) return null;
                buffer = new char[used / sizeof(char) + 1];
            }
        }
        return null;
    }

    private static EventLogItem? Parse(string xml, Source source, ProcessMasker masker)
    {
        var root = XDocument.Parse(xml).Root;
        var sys = root?.Element(Ns + "System");
        if (root is null || sys is null) return null;

        var provider = sys.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "";
        if (!int.TryParse(sys.Element(Ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return null;
        var category = source.Classify(provider, id);
        if (category is null) return null;

        int.TryParse(sys.Element(Ns + "Level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level);
        var timeText = sys.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            return null;

        var data = root.Element(Ns + "EventData")?.Elements(Ns + "Data").ToList() ?? [];
        string? Data(int index) => index < data.Count ? data[index].Value.Trim() : null;
        string? Named(string name) => data.FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value.Trim();

        var item = new EventLogItem { TimeUtc = time, Category = category, Provider = provider, EventId = id, Level = level };
        switch (category)
        {
            case "AppCrash":
            {
                var app = Data(0);
                var module = Data(3);
                if (app is not null) app = masker.MaskName(app);
                item.Detail = module is null || app is null || ProcessMasker.IsMaskedToken(app) ? app : $"{app} ({module})";
                break;
            }
            case "AppHang":
                item.Detail = Data(0) is { } hung ? masker.MaskName(hung) : null;
                break;
            case "BugCheck":
                item.Detail = Data(0) is { } code ? Truncate(code, 120) : null;
                break;
            case "LowMemory":
            {
                // 메모리를 가장 많이 쓰던 프로세스 이름들이 UserData 에 포함됨
                var names = root.Descendants()
                    .Where(e => !e.HasElements && e.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .Select(e => masker.MaskName(e.Value.Trim()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();
                item.Detail = names.Count > 0 ? string.Join(", ", names) : null;
                break;
            }
            case "Boot":
                if (double.TryParse(Named("BootTime"), NumberStyles.Float, CultureInfo.InvariantCulture, out double bootMs) && bootMs > 0)
                    item.Value = bootMs;
                break;
        }
        return item;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
