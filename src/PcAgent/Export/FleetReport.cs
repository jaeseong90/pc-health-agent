using PcAgent.Analysis;
using PcAgent.Inventory;
using static PcAgent.Export.HtmlReport;

namespace PcAgent.Export;

/// <summary>여러 PC 취합 결과를 한 장으로 보는 HTML(외부 리소스 없음). 모든 값은 HTML 인코딩합니다(받은 zip 은 신뢰하지 않음).</summary>
internal static class FleetReport
{
    private static readonly (string Verdict, string Label)[] Verdicts =
    [
        ("Replace", "교체 권장"), ("Upgrade", "업그레이드 권장"), ("Watch", "점검 필요"), ("Ok", "양호"), ("InsufficientData", "데이터 부족"),
    ];

    /// <summary>조치 집계용 이름(권고 제목은 상황별로 달라서 코드 기준으로 묶음).</summary>
    internal static string ActionLabel(string code) => code switch
    {
        "PC_REPLACE" => "PC 교체",
        "RAM_UPGRADE" => "메모리 증설",
        "SSD_REPLACE" => "SSD 교체",
        "BATTERY_REPLACE" => "배터리 교체",
        "DISK_REPLACE" => "디스크 교체",
        "DISK_CHECK" => "디스크 점검",
        "OS_UPGRADE" => "Windows 11 업그레이드",
        "WIN11_CHECK" => "Windows 11 호환성 확인",
        "DISK_CLEANUP" => "디스크 공간 확보",
        "SOFTWARE_REVIEW" => "프로그램 점검",
        "CPU_REVIEW" => "CPU 사용 검토",
        "STABILITY_CHECK" => "안정성 점검",
        "RAM_WATCH" => "메모리 사용 관찰",
        _ => code,
    };

    public static void Write(TextWriter w, IReadOnlyList<ExportSummary> rows, IReadOnlyList<string> skipped)
    {
        w.Write($"""
            <!doctype html>
            <html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>PC 진단 취합 리포트</title>
            <style>{Css}{ExtraCss}</style></head><body><div class="wrap wide">
            <header><div><h1>PC 진단 취합 리포트</h1>
            <div class="sub">PC {rows.Count}대 · 생성 {DateTime.Now:yyyy-MM-dd HH:mm} · 조치 점수가 높은 순</div></div></header>
            """);

        // 판정 분포
        w.Write("<section><h2>판정 분포</h2><div class=\"kpis\">");
        foreach (var (verdict, label) in Verdicts)
        {
            int n = rows.Count(r => r.Analysis.Verdict == verdict);
            w.Write($"<div class=\"kpi\"><span><i class=\"dot v-{verdict}\"></i>{H(label)}</span><b>{n}대</b><span>{(rows.Count > 0 ? n * 100 / rows.Count : 0)}%</span></div>");
        }
        w.Write("</div></section>");

        // 필요한 조치 집계(PC 수 기준)
        var actions = rows
            .SelectMany(r => r.Analysis.Recommendations.Select(x => (x.Code, Host: r.Inventory.Hostname, x.Priority)).DistinctBy(x => (x.Code, x.Host)))
            .GroupBy(x => x.Code)
            .Select(g => (Code: g.Key, Count: g.Count(), High: g.Count(x => x.Priority == "High"), Hosts: g.Select(x => x.Host).ToList()))
            .OrderByDescending(x => x.High).ThenByDescending(x => x.Count)
            .ToList();
        w.Write("<section><h2>필요한 조치</h2>");
        if (actions.Count == 0)
        {
            w.Write("<p class=\"muted\">권장 조치가 있는 PC 가 없습니다.</p>");
        }
        else
        {
            w.Write("<div class=\"tbl\"><table><tr><th>조치</th><th class=\"n\">PC 수</th><th class=\"n\">높음</th><th>대상 PC</th></tr>");
            foreach (var a in actions)
                w.Write($"<tr><td>{H(ActionLabel(a.Code))}</td><td class=\"n\">{a.Count}</td><td class=\"n\">{a.High}</td><td>{H(string.Join(", ", a.Hosts))}</td></tr>");
            w.Write("</table></div>");
        }
        w.Write("</section>");

        // PC 목록
        w.Write("<section><h2>PC 별 결과</h2><div class=\"tbl\"><table><tr>" +
                "<th>PC</th><th>판정</th><th class=\"n\">점수</th><th>권장 조치</th><th>CPU</th><th class=\"n\">메모리</th><th>시스템 디스크</th>" +
                "<th class=\"n\">연식</th><th>Win11</th><th class=\"n\">메모리 부족</th><th class=\"n\">CPU 포화</th><th class=\"n\">배터리</th><th class=\"n\">관측</th><th class=\"n\">내보낸 날</th></tr>");
        foreach (var s in rows)
        {
            var a = s.Analysis;
            var inv = s.Inventory;
            var tz = FleetMerge.TimeZoneOf(s);
            var disk = inv.Disks.FirstOrDefault(d => d.IsSystem);
            var recs = a.Recommendations.Count == 0 ? "-"
                : string.Join("<br>", a.Recommendations.Select(r => $"<span class=\"badge p-{H(r.Priority)}\">{H(Exporter.PriorityLabel(r.Priority))}</span> {H(r.Title)}"));
            w.Write("<tr>" +
                    $"<td><b>{H(inv.Hostname)}</b><div class=\"muted\">{H(string.Join(" ", new[] { inv.Manufacturer, inv.Model }.Where(x => !string.IsNullOrWhiteSpace(x))))}</div></td>" +
                    $"<td><span class=\"badge v-{H(a.Verdict)}\">{H(a.VerdictLabel)}</span></td>" +
                    $"<td class=\"n\">{a.Score}</td>" +
                    $"<td class=\"recs\">{recs}</td>" +
                    $"<td>{H(inv.CpuName)}</td>" +
                    $"<td class=\"n\">{inv.RamInstalledMb / 1024.0:0.#}GB{(inv.RamSlotsLabel is { } slots ? $"<div class=\"muted\">{H(slots)}</div>" : "")}</td>" +
                    $"<td class=\"nw\">{(disk is null ? "-" : H($"{disk.MediaType} {disk.SizeGb:0}GB"))}" +
                    $"{(disk?.Health is { } dh ? $"<div class=\"muted\">{H(DiskHealth.LevelLabel(dh.Level))}{(dh.PercentageUsed is { } pu ? $" · 수명 {pu}%" : "")}</div>" : "")}</td>" +
                    $"<td class=\"n\">{(inv.EstimatedAgeYears is { } age ? $"{age:0.#}년" : "-")}</td>" +
                    $"<td class=\"nw\">{H(Exporter.Win11Label(a.Win11.Status))}</td>" +
                    $"<td class=\"n\">{a.Stats.MemPressurePct:0.#}%</td>" +
                    $"<td class=\"n\">{a.Stats.CpuHighPct:0.#}%</td>" +
                    $"<td class=\"n\">{(inv.BatteryHealthPercent is { } bat ? $"{bat:0}%" : "-")}</td>" +
                    $"<td class=\"n\">{a.Stats.BasisHours:0.#}h</td>" +
                    $"<td class=\"n\">{Exporter.Local(s.GeneratedUtc, tz):yyyy-MM-dd}<div class=\"muted\">v{H(s.AgentVersion)}</div></td>" +
                    "</tr>");
        }
        w.Write("</table></div></section>");

        // 에이전트 자체 부하(가벼움 확인)
        var usages = rows.Where(r => r.AgentUsage is not null).Select(r => r.AgentUsage!).ToList();
        if (usages.Count > 0)
        {
            var cpus = usages.Where(u => u.CpuAvgPercent is not null).Select(u => u.CpuAvgPercent!.Value).ToList();
            w.Write("<section><h2>에이전트 자체 사용량</h2><p>" +
                    $"{usages.Count}대 기준 · 메모리 평균 {usages.Average(u => u.PrivateMb):0.#}MB (최대 {usages.Max(u => u.PrivateMb):0.#}MB)" +
                    (cpus.Count > 0 ? $" · CPU 평균 {cpus.Average():0.###}% (최대 {cpus.Max():0.###}%)" : "") +
                    "</p></section>");
        }

        w.Write("<footer>");
        if (skipped.Count > 0) w.Write($"<p>읽지 못한 파일 {skipped.Count}개: {H(string.Join(", ", skipped))}</p>");
        w.Write("<p>판정 기준은 각 PC 리포트(report.html)의 권장 조치 설명을 참고하세요. 임계값은 파일럿 결과를 보고 조정할 수 있습니다.</p></footer></div></body></html>");
    }

    private const string ExtraCss = """
        .wrap.wide{max-width:1400px}.recs{min-width:220px}.recs .badge{margin:2px 4px 2px 0}th.n{text-align:right}.nw{white-space:nowrap}
        .dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:6px;vertical-align:middle;background:currentColor}
        .kpi .dot.v-Replace,.kpi .dot.v-Upgrade{color:var(--high)}.kpi .dot.v-Watch{color:var(--med)}.kpi .dot.v-Ok{color:var(--ok)}.kpi .dot.v-InsufficientData{color:var(--low)}
        td .badge.v-Replace,td .badge.v-Upgrade,td .badge.v-Watch,td .badge.v-Ok,td .badge.v-InsufficientData{white-space:nowrap}
        """;
}
