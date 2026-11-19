using System.Globalization;
using System.Net;
using PcAgent.Analysis;
using PcAgent.Collection;
using PcAgent.EventLogs;
using PcAgent.Inventory;

namespace PcAgent.Export;

/// <summary>외부 리소스 없이 단독으로 열리는 HTML 진단 리포트.</summary>
internal static class HtmlReport
{
    public static void Write(TextWriter w, ExportSummary s, IReadOnlyList<PressureRecord> events, IReadOnlyList<EventLogItem> logs, TimeZoneInfo tz)
    {
        var a = s.Analysis;
        var inv = s.Inventory;
        var st = a.Stats;
        // 조회 기간(기본 30일)이 아니라 실제로 데이터가 있는 첫날부터 표시
        string from = a.Daily.Count > 0 ? a.Daily[0].Date : Exporter.Local(s.FromUtc, tz).ToString("yyyy-MM-dd");
        string to = Exporter.Local(s.ToUtc, tz).ToString("yyyy-MM-dd HH:mm");

        w.Write($"""
            <!doctype html>
            <html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>PC 진단 리포트 - {H(inv.Hostname)}</title>
            <style>{Css}</style></head><body><div class="wrap">
            <header>
              <div><h1>PC 진단 리포트 · {H(inv.Hostname)}</h1>
              <div class="sub">{H(Join(" ", inv.Manufacturer, inv.Model))} · 기간 {from} ~ {to} · {H(a.Basis)}</div></div>
              <div class="verdict v-{a.Verdict}">{H(a.VerdictLabel)}<small>조치 점수 {a.Score}/100</small></div>
            </header>
            """);

        WriteSummary(w, s, from, to);

        // 권장 조치
        w.Write("<section><h2>권장 조치</h2>");
        if (a.Recommendations.Count == 0)
        {
            w.Write($"<p class=\"muted\">{(a.SufficientData ? "현재 사양으로 업무 사용에 문제가 없어 보입니다." : "판정할 데이터가 아직 부족합니다. 며칠 더 수집한 뒤 다시 내보내세요.")}</p>");
        }
        foreach (var r in a.Recommendations)
        {
            w.Write($"<div class=\"rec\"><span class=\"badge p-{r.Priority}\">{Exporter.PriorityLabel(r.Priority)}</span><div><b>{H(r.Title)}</b><div>{H(r.Detail)}</div></div></div>");
        }
        if (a.Findings.Count > 0)
        {
            w.Write("<h3>참고</h3><ul>");
            foreach (var f in a.Findings) w.Write($"<li>{H(f)}</li>");
            w.Write("</ul>");
        }
        w.Write("</section>");

        // 사용 현황
        w.Write($"""
            <section><h2>사용 현황 <span class="muted">({H(a.Basis)}, {st.BasisHours:0.#}시간 / 전체 {st.ObservedHours:0.#}시간)</span></h2>
            <div class="kpis">
              {Kpi("CPU 평균", $"{st.CpuAvg:0.#}%", $"상위 5% 구간 {st.CpuP95:0.#}%")}
              {Kpi("CPU 포화 비율", $"{st.CpuHighPct:0.#}%", $"8시간 기준 하루 약 {UsageStats.MinutesPer8h(st.CpuHighPct):0.#}분")}
              {Kpi("메모리 부족 비율", $"{st.MemPressurePct:0.#}%", $"8시간 기준 하루 약 {UsageStats.MinutesPer8h(st.MemPressurePct):0.#}분 · 가용 평균 {st.MemAvailAvg:0.#}%")}
              {Kpi("메모리 여유 20% 이하", $"{st.MemTight20Pct:0.#}%", $"15% 이하 {st.MemTight15Pct:0.#}% — 부족 직전 상태로 쓴 시간")}
              {Kpi("필요 메모리 (커밋)", st.CommitGbP95 is { } cg ? $"{cg:0.#}GB" : "-", st.CommitGbP95 is null ? "v0.5.0 이후 수집분부터 표시" : $"상위 5% 구간 · 최대 {st.CommitGbMax:0.#}GB · 설치 {inv.RamInstalledMb / 1024.0:0.#}GB")}
              {Kpi("하드 페이지 폴트", st.HardFaultsAvg is { } hf ? $"{hf:0.#}/s" : "-", HardFaultNote(st))}
              {Kpi("디스크 포화 비율", $"{st.DiskBusyPct:0.#}%", st.DiskActiveAvg is { } diskAvg ? $"평균 활성 {diskAvg:0.#}%" : "측정값 없음")}
              {Kpi("디스크 응답 시간", st.DiskLatencyAvgMs is { } lat ? $"{lat:0.#}ms" : "-", st.DiskLatencyP95Ms is { } latP95 ? $"상위 5% {latP95:0.#}ms (SSD 는 보통 1ms 안팎)" : "v0.5.0 이후 수집분부터 표시")}
              {Kpi("CPU 속도 (부하 시)", st.CpuPerfUnderLoad is { } perf ? $"{perf:0}%" : "-", st.CpuPerfUnderLoad is null ? "v0.5.0 이후 수집분부터 표시" : "정격 대비 — 낮으면 발열·전원 제한")}
              {Kpi("자원 부족 이벤트", $"{st.CpuEvents + st.MemoryEvents + st.DiskEvents}건", $"CPU {st.CpuEvents} · 메모리 {st.MemoryEvents} · 디스크 {st.DiskEvents}")}
            </div></section>
            """);

        // 하드웨어
        var sysDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        var sysVol = inv.Volumes.FirstOrDefault(v => v.IsSystem);
        var modules = inv.RamModules.Count == 0 ? ""
            : inv.RamSoldered ? $" · 온보드(납땜) {inv.RamModules[0].Type} — 증설 불가 가능성"
            : " · " + string.Join(", ", inv.RamModules.Select(m => $"{m.SizeMb / 1024.0:0.#}GB {m.Type} {m.FormFactor}"));
        w.Write("<section><h2>하드웨어 사양</h2><div class=\"tbl\"><table>");
        Row(w, "모델", Join(" ", inv.Manufacturer, inv.Model) + (inv.SerialNumber is null ? "" : $" (S/N {inv.SerialNumber})"));
        Row(w, "CPU", $"{inv.CpuName} · {inv.CpuCores}코어 {inv.CpuLogicalProcessors}스레드");
        Row(w, "메모리", $"{inv.RamInstalledMb / 1024.0:0.#}GB" +
                       (!inv.RamSoldered && inv.RamSlotsTotal is { } t ? $" · 슬롯 {inv.RamSlotsUsed}/{t} 사용" : "") + modules);
        Row(w, "디스크", string.Join(" / ", inv.Disks.Where(d => !d.IsRemovable).Select(d =>
            $"{(d.IsSystem ? "[시스템] " : "")}{d.Model} · {d.MediaType} · {d.BusType} · {d.SizeGb:0}GB")));
        var healthRows = inv.Disks.Where(d => !d.IsRemovable).Select(DiskHealthLabel).Where(x => x is not null).ToList();
        if (healthRows.Count > 0) Row(w, "디스크 상태", string.Join(" / ", healthRows));
        if (sysVol is not null) Row(w, "시스템 드라이브", $"{sysVol.Name} 여유 {sysVol.FreeGb:0.#}GB / {sysVol.SizeGb:0.#}GB");
        Row(w, "그래픽", string.Join(", ", inv.Gpus));
        if (inv.Batteries.Count > 0) Row(w, "배터리", BatteryLabel(inv));
        Row(w, "OS", $"{inv.OsName} {inv.OsDisplayVersion} (빌드 {inv.OsBuild}.{inv.OsUbr})");
        Row(w, "BIOS", Join(" ", inv.BiosVendor, inv.BiosVersion, inv.BiosDate) +
                       (inv.EstimatedAgeYears is { } age ? $" · 추정 연식 {age:0.#}년" : ""));
        Row(w, "보안", $"TPM {TpmLabel(inv.TpmVersion)} · {(inv.IsUefi switch { true => "UEFI", false => "레거시 BIOS", _ => "펌웨어 확인 불가" })} · " +
                     $"보안 부팅 {(inv.SecureBootEnabled switch { true => "켜짐", false => "꺼짐", _ => "확인 불가" })}");
        Row(w, "Windows 11", Exporter.Win11Label(a.Win11.Status) + (a.Win11.Issues.Count > 0 ? " — " + string.Join(", ", a.Win11.Issues) : ""));
        w.Write("</table></div></section>");

        // 일별 추이
        if (a.Daily.Count > 0)
        {
            w.Write("<section><h2>일별 추이</h2><div class=\"tbl\"><table><tr><th>날짜</th><th>관측</th><th>CPU 평균</th><th>CPU 포화</th><th>메모리 부족</th><th>디스크 포화</th><th>이벤트</th></tr>");
            foreach (var d in a.Daily)
            {
                w.Write($"<tr><td>{d.Date} ({d.Weekday})</td><td class=\"n\">{d.ObservedHours:0.#}h</td>" +
                        $"<td>{Bar(d.CpuAvg)}</td><td>{Bar(d.CpuHighPct)}</td><td>{Bar(d.MemPressurePct)}</td><td>{Bar(d.DiskBusyPct)}</td>" +
                        $"<td class=\"n\">{d.Events}</td></tr>");
            }
            w.Write("</table></div></section>");
        }

        // 원인 프로세스
        if (a.CpuOffenders.Count + a.MemoryOffenders.Count + a.DiskOffenders.Count > 0)
        {
            w.Write("<section><h2>자원 부족 시 주요 프로세스</h2><p class=\"muted\">이벤트마다 해당 자원을 가장 많이 쓴 상위 5개를 집계했습니다. '1위'는 그 이벤트에서 가장 많이 사용한 횟수입니다.</p>");
            Offenders(w, "메모리 부족", a.MemoryOffenders, o => $"{o.AvgPrivateMb:0}MB", "평균 메모리");
            Offenders(w, "CPU 포화", a.CpuOffenders, o => $"{o.AvgCpu:0.#}%", "평균 CPU");
            Offenders(w, "디스크 포화", a.DiskOffenders, o => $"{o.AvgIoKBps:0}KB/s", "평균 I/O");
            w.Write("</section>");
        }

        // 최근 이벤트
        if (events.Count > 0)
        {
            w.Write("<section><h2>최근 자원 부족 이벤트</h2><div class=\"tbl\"><table><tr><th>시작</th><th>유형</th><th>지속</th><th>최고치</th><th>상위 프로세스</th></tr>");
            foreach (var e in events.OrderByDescending(e => e.StartedUtc).Take(30))
            {
                var dur = e.EndedUtc is { } end ? Duration(end - e.StartedUtc) : "진행 중/기록 없음";
                var top = string.Join(", ", e.RankedByName().Take(3).Select(p => p.Name));
                if (e.SnapshotSkipped) top = "(빈도 제한으로 스냅샷 생략)";
                w.Write($"<tr><td>{Exporter.Local(e.StartedUtc, tz):MM-dd HH:mm}</td><td>{Exporter.KindLabel(e.Kind)}</td><td>{dur}</td><td class=\"n\">{e.Peak:0.#}%</td><td>{H(top)}</td></tr>");
            }
            w.Write("</table></div></section>");
        }

        // 안정성
        var stab = a.Stability;
        w.Write($"""
            <section><h2>안정성 (Windows 이벤트 로그)</h2>
            <div class="kpis">
              {Kpi("블루스크린", $"{stab.BugChecks}회", "")}
              {Kpi("비정상 종료", $"{stab.UnexpectedShutdowns}회", "전원 끊김·강제 종료")}
              {Kpi("앱 비정상 종료", $"{stab.AppCrashes}회", $"응답 없음 {stab.AppHangs}회")}
              {Kpi("메모리 부족 경고", $"{stab.LowMemoryWarnings}회", "Windows 자체 경고")}
              {Kpi("평균 부팅 시간", stab.BootAvgSec is { } b ? $"{b:0}초" : "-", stab.BootCount > 0 ? $"최근 부팅 {Math.Min(stab.BootCount, 10)}회 기준" : "관리자 권한 필요")}
            </div>
            """);
        if (stab.TopCrashApps.Count > 0)
        {
            w.Write("<p>자주 멈춘 앱: " + H(string.Join(", ", stab.TopCrashApps.Select(x => $"{x.Name} {x.Count}회"))) + "</p>");
        }
        w.Write("</section>");

        // 안내
        w.Write("<footer><p>수집하지 않는 정보: 창 제목, 문서·파일 이름, 웹 주소, 키 입력, 화면, 명령줄 인수. " +
                "사용자 폴더 경로는 %USERPROFILE% 로, 마스킹 대상 프로세스는 [masked-xxxxxx] 로 표시됩니다.</p>");
        if (s.Warnings.Count > 0)
        {
            w.Write("<p>수집 경고:</p><ul>");
            foreach (var warn in s.Warnings) w.Write($"<li>{H(warn)}</li>");
            w.Write("</ul>");
        }
        var usage = s.AgentUsage is { } u
            ? $" · 에이전트 자체 사용량: 메모리 {u.PrivateMb:0.#}MB" + (u.CpuAvgPercent is { } cpu ? $", CPU 평균 {cpu:0.###}%" : "")
            : "";
        w.Write($"<p>PC Health Agent v{H(s.AgentVersion)} · 생성 {Exporter.Local(s.GeneratedUtc, tz):yyyy-MM-dd HH:mm}{H(usage)}</p></footer></div></body></html>");
    }

    internal static string? DiskHealthLabel(DiskDrive d)
    {
        if (d.Health is not { } h) return null;
        var parts = new List<string> { DiskHealth.LevelLabel(h.Level) };
        var problems = h.Problems();
        if (problems.Count > 0) parts.Add(string.Join(", ", problems));
        if (h.PercentageUsed is { } used && used < 80) parts.Add($"수명 사용 {used}%");
        if (h.PowerOnHours is { } poh) parts.Add($"사용 {poh:N0}시간");
        if (h.TemperatureC is { } t) parts.Add($"{t}°C");
        if (h.DataWrittenTb is { } tb) parts.Add($"누적 기록 {tb:0.#}TB");
        return (d.IsSystem ? "[시스템] " : $"[디스크 {d.Number}] ") + string.Join(" · ", parts);
    }

    internal static string BatteryLabel(InventoryInfo inv)
    {
        var parts = new List<string>();
        if (inv.BatteryHealthPercent is { } health) parts.Add($"수명 {health:0.#}%");
        foreach (var b in inv.Batteries)
        {
            parts.Add($"완충 {b.FullChargedCapacity:N0} / 설계 {b.DesignedCapacity:N0}{(b.Relative ? "" : "mWh")}" +
                      (b.CycleCount is { } c ? $" · 충전 {c}회" : "") + (b.Chemistry is null ? "" : $" · {b.Chemistry}"));
        }
        return string.Join(" · ", parts);
    }

    private static void Offenders(TextWriter w, string title, List<Offender> list, Func<Offender, string> metric, string metricLabel)
    {
        if (list.Count == 0) return;
        w.Write($"<h3>{title}</h3><div class=\"tbl\"><table><tr><th>프로세스</th><th>게시자</th><th>1위</th><th>상위 3위</th><th>{metricLabel}</th></tr>");
        foreach (var o in list)
        {
            string note = ProcessNotes.Describe(o.Name) is { } d ? $" <span class=\"muted\">{H(d)}</span>" : "";
            w.Write($"<tr><td title=\"{H(o.Path)}\">{H(o.Name)}{note}</td><td>{H(o.Publisher)}</td><td class=\"n\">{o.Rank1}</td><td class=\"n\">{o.Top3}</td><td class=\"n\">{H(metric(o))}</td></tr>");
        }
        w.Write("</table></div>");
    }

    /// <summary>
    /// 맨 위 요약(제출용 1장): 결론 → 대상 PC → 근거(시간·횟수) → 점검표 → 측정 방법·주의. 인쇄하면 이 부분이 첫 장에 들어갑니다.
    /// 이전 버전이 만든 summary.json 에는 요약 항목이 없으므로, 그때는 아무것도 쓰지 않습니다.
    /// </summary>
    private static void WriteSummary(TextWriter w, ExportSummary s, string from, string to)
    {
        var a = s.Analysis;
        var inv = s.Inventory;
        if (a.Headline.Length == 0 && a.Checklist.Count == 0) return;

        var sysDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        string target = Join(" · ",
            Join(" ", inv.Manufacturer, inv.Model),
            inv.CpuName,
            inv.RamInstalledMb > 0 ? $"메모리 {inv.RamInstalledMb / 1024.0:0.#}GB{(inv.RamSoldered ? "(온보드)" : "")}" : null,
            sysDisk is null ? null : $"{sysDisk.MediaType} {sysDisk.SizeGb:0}GB",
            inv.EstimatedAgeYears is { } age ? $"약 {age:0.#}년 사용" : null);

        w.Write($"<section class=\"summary\"><h2>요약</h2><p class=\"headline v-{H(a.Verdict)}\">{H(a.Headline)}</p>");
        w.Write($"<p class=\"target\"><b>대상 PC</b> {H(inv.Hostname)} — {H(target)}{(inv.SerialNumber is null ? "" : H($" (S/N {inv.SerialNumber})"))}</p>");

        if (a.Evidence.Count > 0)
        {
            w.Write("<h3>판단 근거</h3><ul class=\"evidence\">");
            foreach (var e in a.Evidence) w.Write($"<li>{H(e)}</li>");
            w.Write("</ul>");
        }

        if (a.Checklist.Count > 0)
        {
            w.Write("<h3>점검표</h3><div class=\"tbl\"><table class=\"check\"><tr><th>항목</th><th>상태</th><th>확인한 값</th><th>설명</th></tr>");
            foreach (var c in a.Checklist)
                w.Write($"<tr><td>{H(c.Item)}</td><td><span class=\"badge c-{H(c.Status)}\">{StatusLabel(c.Status)}</span></td><td>{H(c.Value)}</td><td class=\"muted\">{H(c.Note)}</td></tr>");
            w.Write("</table></div>");
        }

        var st = a.Stats;
        w.Write("<h3>측정 방법</h3><p class=\"muted\">" +
                H($"{from} ~ {to} 사이 PC 가 켜져 있던 {st.ObservedHours:0.#}시간을 5초 간격으로 자동 측정했습니다" +
                  $"({a.Basis} {st.BasisHours:0.#}시간, {st.BasisDays}일). 창 제목·문서·웹 주소·키 입력은 수집하지 않습니다.") +
                "</p>");
        if (a.Caveats.Count > 0)
        {
            w.Write("<ul class=\"caveats\">");
            foreach (var c in a.Caveats) w.Write($"<li>{H(c)}</li>");
            w.Write("</ul>");
        }
        w.Write("</section>");
    }

    private static string StatusLabel(string status) => status switch
    {
        "Ok" => "정상",
        "Warn" => "주의",
        "Bad" => "문제",
        _ => "확인 불가",
    };

    private static string HardFaultNote(UsageStats st)
    {
        if (UpgradeAdvisor.HardFaultRatio(st) is { } ratio)
            return $"메모리 부족일 때 {st.HardFaultsPressureMedian:0.#}/s ↔ 평소 {st.HardFaultsCalmMedian:0.#}/s (약 {ratio:0.#}배)";
        return st.HardFaultsP95 is { } p95 ? $"상위 5% {p95:0.#}/s (높을수록 메모리 부족)" : "";
    }

    private static void Row(TextWriter w, string label, string? value) =>
        w.Write($"<tr><th>{label}</th><td>{H(string.IsNullOrWhiteSpace(value) ? "-" : value)}</td></tr>");

    private static string Kpi(string label, string value, string note) =>
        $"<div class=\"kpi\"><span>{H(label)}</span><b>{H(value)}</b><span>{H(note)}</span></div>";

    private static string Bar(double pct)
    {
        var width = Math.Clamp(pct, 0, 100).ToString("0.#", CultureInfo.InvariantCulture);
        return $"<div class=\"barcell\"><div class=\"bar\"><i style=\"width:{width}%\"></i></div><span>{pct:0.#}%</span></div>";
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds}초" : $"{t.Seconds}초";

    private static string TpmLabel(string? v) => v switch { null => "확인 불가", "none" => "없음", _ => v };

    private static string Join(string sep, params string?[] parts) => string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    internal static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    internal const string Css = """
        :root{--bg:#f5f6f8;--card:#fff;--text:#1c2230;--muted:#667085;--line:#e4e7ec;--bar:#4f6bd8;
        --high:#c4320a;--high-bg:#fef3f2;--med:#a15c07;--med-bg:#fef7e6;--low:#3e4784;--low-bg:#eef2ff;--ok:#067647;--ok-bg:#ecfdf3}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1218;--card:#171b23;--text:#e6e9ef;--muted:#98a2b3;--line:#2a303c;--bar:#7b93f0;
        --high:#ff8a73;--high-bg:#3a1a14;--med:#fdb022;--med-bg:#36280c;--low:#a4bcfd;--low-bg:#1c2342;--ok:#47cd89;--ok-bg:#0f2b1d}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.6 "Malgun Gothic","Apple SD Gothic Neo",system-ui,sans-serif}
        .wrap{max-width:1080px;margin:0 auto;padding:24px 16px 48px}
        header{display:flex;flex-wrap:wrap;gap:16px;align-items:center;justify-content:space-between;margin-bottom:18px}
        h1{font-size:21px;margin:0 0 4px}h2{font-size:16px;margin:0 0 12px}h3{font-size:14px;margin:16px 0 8px}
        .sub,.muted{color:var(--muted);font-size:13px;font-weight:400}
        .verdict{padding:10px 18px;border-radius:12px;font-weight:700;font-size:18px;text-align:center}
        .verdict small{display:block;font-size:12px;font-weight:400;opacity:.85}
        .v-Replace,.p-High{color:var(--high);background:var(--high-bg)}
        .v-Upgrade{color:var(--high);background:var(--high-bg)}
        .v-Watch,.p-Medium{color:var(--med);background:var(--med-bg)}
        .v-Ok{color:var(--ok);background:var(--ok-bg)}
        .v-InsufficientData,.p-Low{color:var(--low);background:var(--low-bg)}
        section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-bottom:14px}
        .rec{display:flex;gap:12px;align-items:flex-start;padding:10px 0;border-bottom:1px solid var(--line)}.rec:last-of-type{border-bottom:0}
        .badge{flex:none;display:inline-block;padding:2px 9px;border-radius:999px;font-size:12px;font-weight:700}
        .kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px}
        .kpi{border:1px solid var(--line);border-radius:10px;padding:10px 12px;display:flex;flex-direction:column}
        .kpi b{font-size:21px;margin:2px 0}.kpi span{color:var(--muted);font-size:12px}
        .tbl{overflow-x:auto}table{width:100%;border-collapse:collapse;font-size:13px}
        th,td{text-align:left;padding:7px 8px;border-bottom:1px solid var(--line);vertical-align:top}
        th{color:var(--muted);font-weight:600;white-space:nowrap}td.n{text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}
        .barcell{display:flex;align-items:center;gap:6px;min-width:110px}.barcell span{font-variant-numeric:tabular-nums;font-size:12px;min-width:38px}
        .bar{flex:1;height:8px;background:var(--line);border-radius:4px;overflow:hidden}.bar i{display:block;height:100%;background:var(--bar)}
        ul{margin:6px 0;padding-left:20px}footer{color:var(--muted);font-size:12px;margin-top:20px}
        .summary{border-width:2px}.headline{font-size:17px;font-weight:700;margin:0 0 10px;padding:10px 14px;border-radius:10px}
        .target{margin:0 0 4px}.target b{margin-right:8px}.evidence li{margin:4px 0}.caveats{color:var(--muted);font-size:13px}
        .c-Ok{color:var(--ok);background:var(--ok-bg)}.c-Warn{color:var(--med);background:var(--med-bg)}
        .c-Bad{color:var(--high);background:var(--high-bg)}.c-Unknown{color:var(--low);background:var(--low-bg)}
        table.check td:first-child{white-space:nowrap;font-weight:600}table.check .badge{white-space:nowrap}
        @media print{:root{--bg:#fff;--card:#fff;--text:#111;--muted:#555;--line:#ccc;--bar:#4f6bd8;
        --high:#c4320a;--high-bg:#fef3f2;--med:#a15c07;--med-bg:#fef7e6;--low:#3e4784;--low-bg:#eef2ff;--ok:#067647;--ok-bg:#ecfdf3}
        body{font-size:11px;line-height:1.45}.wrap{max-width:none;padding:0}header{margin-bottom:8px}
        h1{font-size:16px}h2{font-size:13px;margin-bottom:6px}h3{font-size:12px;margin:8px 0 4px}
        section{break-inside:avoid;border-color:#ccc;padding:10px 12px;margin-bottom:8px}
        .summary{break-inside:auto;break-after:page}.headline{font-size:13px;padding:6px 10px;margin-bottom:6px}
        .verdict{padding:6px 12px;font-size:14px}th,td{padding:3px 6px}.evidence li{margin:2px 0}.caveats{font-size:10.5px}
        *{-webkit-print-color-adjust:exact;print-color-adjust:exact}}
        @page{size:A4;margin:12mm}
        """;
}
