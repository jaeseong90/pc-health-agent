using PcAgent.Collection;
using PcAgent.Inventory;

namespace PcAgent.Analysis;

/// <summary>
/// 수집된 데이터로 PC 사양이 업무에 적절한지 판정하고 조치를 권고합니다(규칙 기반).
/// 임계값은 파일럿 데이터를 보고 조정하세요.
/// </summary>
public static class UpgradeAdvisor
{
    /// <summary>성능 기반 판정에 필요한 최소 관측 시간. 업무시간 데이터가 이보다 적으면 전체 시간 기준으로 판정.</summary>
    public const double MinObservedHours = 8;
    internal const double MemPressureHighPct = 5;
    internal const double MemPressureWatchPct = 2;
    /// <summary>가용 메모리 20% 이하로 쓴 시간이 이 비율을 넘으면 '빠듯함'으로 알림(부족 기준에는 못 미쳐도).</summary>
    internal const double MemTightNotePct = 30;
    internal const double CpuHighPct = 10;
    internal const double CpuWatchPct = 4;
    /// <summary>CPU 를 많이 쓸 때의 속도가 정격의 이 비율보다 낮으면 발열·전원 제한으로 봄.</summary>
    internal const double CpuThrottledPct = 75;
    /// <summary>'CPU 를 많이 쓰는 중'으로 보는 1분 평균 사용률.</summary>
    private const double CpuLoadedPct = 20;
    internal const double DiskBusyHddPct = 3;
    private const double DiskBusySsdPct = 10;
    /// <summary>SSD 인데 응답 시간 상위 5%가 이보다 길면 알림(ms).</summary>
    private const double SsdSlowLatencyMs = 50;
    internal const double ReplaceAgeYears = 5;
    private const int MinSnapshotEventsForDominance = 3;
    internal const double BatteryReplaceHighPct = 40;
    internal const double BatteryReplacePct = 60;
    private const double BatteryWatchPct = 80;
    /// <summary>비교·분포 통계에 필요한 최소 표본(분).</summary>
    private const int MinMinutesForComparison = 10;
    private const int MinMinutesForDistribution = 60;

    public static AnalysisResult Analyze(AnalysisInput input)
    {
        var c = input.Config;
        var inv = input.Inventory;
        var tz = input.TimeZone;
        var r = new AnalysisResult();

        DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, tz);
        DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(Local(utc).DateTime);
        bool IsWork(DateTimeOffset utc) => IsWorkTime(Local(utc), c);

        // ---------- 지표 ----------
        var all = input.Metrics;
        var work = all.Where(m => IsWork(m.MinuteUtc)).ToList();
        // 업무시간 데이터가 판정에 충분할 때만 업무시간 기준. 아니면 전체 시간 기준
        // (업무시간 데이터가 늘어날수록 판정이 불리해지는 역전이 없도록 같은 기준값 사용)
        bool useWork = Hours(work) >= MinObservedHours;
        var basis = useWork ? (IReadOnlyList<MetricMinute>)work : all;
        string basisLabel = useWork ? "업무시간" : "전체 시간";
        r.Basis = useWork ? $"업무시간 기준 ({(c.WorkdaysOnly ? "평일 " : "")}{c.WorkStartHour}~{c.WorkEndHour}시)" : "전체 시간 기준";

        var st = r.Stats;
        st.ObservedHours = Math.Round(Hours(all), 1);
        st.BasisHours = Math.Round(Hours(basis), 1);
        if (basis.Count > 0)
        {
            double covered = Math.Max(1, basis.Sum(m => (double)m.CoveredSeconds));
            st.CpuAvg = Round(Weighted(basis, m => m.CpuAvg));
            st.CpuP95 = Round(Percentile(basis.Select(m => m.CpuAvg), 95));
            st.CpuHighPct = Round(basis.Sum(m => (double)m.CpuHighSeconds) * 100 / covered);
            st.MemAvailAvg = Round(Weighted(basis, m => m.MemAvailAvg));
            st.MemAvailP5 = Round(Percentile(basis.Select(m => m.MemAvailMin), 5));
            st.CommitP95 = Round(Percentile(basis.Select(m => m.CommitMax), 95));
            st.MemPressurePct = Round(basis.Sum(m => (double)m.MemPressureSeconds) * 100 / covered);
            st.DiskBusyPct = Round(basis.Sum(m => (double)m.DiskBusySeconds) * 100 / covered);
            var disk = basis.Where(m => m.DiskActiveAvg.HasValue).ToList();
            if (disk.Count > 0) st.DiskActiveAvg = Round(disk.Average(m => m.DiskActiveAvg!.Value));
            var hf = basis.Where(m => m.HardFaultsAvg.HasValue).Select(m => m.HardFaultsAvg!.Value).ToList();
            if (hf.Count > 0)
            {
                st.HardFaultsAvg = Round(hf.Average());
                st.HardFaultsP95 = Round(Percentile(hf, 95));
            }

            // 빠듯한 구간: 부족 기준(기본 가용 10%) 하나만 보면 한계 근처에서 쓰는 시간이 가려짐
            st.MemTight20Pct = Round(basis.Where(m => m.MemAvailAvg <= 20).Sum(m => (double)m.CoveredSeconds) * 100 / covered);
            st.MemTight15Pct = Round(basis.Where(m => m.MemAvailAvg <= 15).Sum(m => (double)m.CoveredSeconds) * 100 / covered);
            st.MemPressureMinutes = Math.Round(basis.Sum(m => (double)m.MemPressureSeconds) / 60);

            // 날짜별: 여러 날에 걸쳐 반복되는지, 하루에 몰렸는지 (1시간 미만 관측한 날은 '하루'로 세지 않음)
            var days = basis
                .GroupBy(m => LocalDate(m.MinuteUtc))
                .Select(g => (Date: g.Key, Covered: g.Sum(m => (double)m.CoveredSeconds), Mem: g.Sum(m => (double)m.MemPressureSeconds)))
                .Where(d => d.Covered >= 3600)
                .ToList();
            st.BasisDays = days.Count;
            st.MemPressureDays = days.Count(d => d.Mem >= 300);
            if (days.Count > 0)
            {
                var worst = days.MaxBy(d => d.Mem);
                if (worst.Mem > 0)
                {
                    st.MemWorstDay = worst.Date.ToString("yyyy-MM-dd");
                    st.MemWorstDayMinutes = Math.Round(worst.Mem / 60);
                }
            }

            // 메모리 부족이 실제 지연(디스크에서 다시 읽기)으로 이어지는지: 부족하던 분과 아니던 분의 하드 폴트 비교
            var hfPressure = basis.Where(m => m.HardFaultsAvg.HasValue && m.MemPressureSeconds > 0).Select(m => m.HardFaultsAvg!.Value).ToList();
            var hfCalm = basis.Where(m => m.HardFaultsAvg.HasValue && m.MemPressureSeconds == 0).Select(m => m.HardFaultsAvg!.Value).ToList();
            if (hfPressure.Count >= MinMinutesForComparison && hfCalm.Count >= MinMinutesForComparison)
            {
                st.HardFaultsPressureMedian = Round(Percentile(hfPressure, 50));
                st.HardFaultsCalmMedian = Round(Percentile(hfCalm, 50));
            }

            // v0.5.0 부터 수집하는 지표(이전 데이터에는 없으므로 표본이 충분할 때만)
            var commit = basis.Where(m => m.CommitGbMax.HasValue).Select(m => m.CommitGbMax!.Value).ToList();
            if (commit.Count >= MinMinutesForDistribution)
            {
                st.CommitGbP95 = Round(Percentile(commit, 95));
                st.CommitGbMax = Round(commit.Max());
            }
            var loaded = basis.Where(m => m.CpuPerfAvg.HasValue && m.CpuAvg >= CpuLoadedPct).ToList();
            if (loaded.Count >= MinMinutesForComparison)
            {
                double weight = loaded.Sum(m => m.CpuAvg * m.CoveredSeconds);
                if (weight > 0) st.CpuPerfUnderLoad = Round(loaded.Sum(m => m.CpuPerfAvg!.Value * m.CpuAvg * m.CoveredSeconds) / weight);
            }
            var latency = basis.Where(m => m.DiskLatencyAvg.HasValue).Select(m => m.DiskLatencyAvg!.Value).ToList();
            if (latency.Count >= MinMinutesForDistribution)
            {
                st.DiskLatencyAvgMs = Round(latency.Average());
                st.DiskLatencyP95Ms = Round(Percentile(latency, 95));
            }
            var power = basis.Where(m => m.BatterySeconds.HasValue).ToList();
            if (inv.Batteries.Count > 0 && power.Count >= MinMinutesForDistribution)
            {
                st.OnBatteryPct = Round(power.Sum(m => (double)m.BatterySeconds!.Value) * 100 / Math.Max(1, power.Sum(m => (double)m.CoveredSeconds)));
            }
        }
        st.CpuEvents = input.Events.Count(e => e.Kind == PressureKind.Cpu);
        st.MemoryEvents = input.Events.Count(e => e.Kind == PressureKind.Memory);
        st.DiskEvents = input.Events.Count(e => e.Kind == PressureKind.Disk);

        bool sufficient = st.BasisHours >= MinObservedHours;
        r.SufficientData = sufficient;

        // ---------- 일별 추이 / 원인 프로세스 / 안정성 ----------
        var eventsPerDay = input.Events.GroupBy(e => LocalDate(e.StartedUtc)).ToDictionary(g => g.Key, g => g.Count());
        r.Daily = all
            .GroupBy(m => LocalDate(m.MinuteUtc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var day = g.ToList();
                double covered = Math.Max(1, day.Sum(m => (double)m.CoveredSeconds));
                return new DailyStat
                {
                    Date = g.Key.ToString("yyyy-MM-dd"),
                    Weekday = "일월화수목금토"[(int)g.Key.DayOfWeek].ToString(),
                    ObservedHours = Math.Round(Hours(day), 1),
                    CpuAvg = Round(Weighted(day, m => m.CpuAvg)),
                    CpuHighPct = Round(day.Sum(m => (double)m.CpuHighSeconds) * 100 / covered),
                    MemPressurePct = Round(day.Sum(m => (double)m.MemPressureSeconds) * 100 / covered),
                    DiskBusyPct = Round(day.Sum(m => (double)m.DiskBusySeconds) * 100 / covered),
                    Events = eventsPerDay.GetValueOrDefault(g.Key),
                };
            })
            .ToList();

        r.CpuOffenders = Offenders(input.Events, PressureKind.Cpu);
        r.MemoryOffenders = Offenders(input.Events, PressureKind.Memory);
        r.DiskOffenders = Offenders(input.Events, PressureKind.Disk);
        r.Stability = Stability(input);
        r.Win11 = Win11Check.Evaluate(inv);

        // ---------- 규칙 ----------
        var recs = r.Recommendations;
        // 같은 키의 권고는 하나로 합침(예: 여러 이유의 PC 교체). 프로그램 점검은 프로그램·자원별로 따로 둠
        void Add(string code, string priority, string title, string detail, string? key = null)
        {
            key ??= code;
            var existing = recs.FirstOrDefault(x => (x.Code == "SOFTWARE_REVIEW" ? x.Code + x.Title : x.Code) == key);
            if (existing is null)
            {
                recs.Add(new Recommendation { Code = code, Priority = priority, Title = title, Detail = detail });
                return;
            }
            existing.Detail += " / " + detail;
            if (Rank(priority) < Rank(existing.Priority)) existing.Priority = priority;
        }

        if (!sufficient)
        {
            r.Findings.Add($"관측 데이터가 {st.BasisHours:0.#}시간으로 부족해 성능 기반 판정은 생략했습니다(최소 {MinObservedHours:0}시간). 사양 기반 항목만 판정했습니다.");
        }

        // 1) Windows 11 (LTSC/IoT 는 별도 지원 주기라 강제 전환 대상 아님)
        if (!inv.IsWindows11 && Win11Check.IsLtsc(inv.OsEdition))
        {
            r.Findings.Add($"Windows 10 LTSC/IoT 에디션({inv.OsEdition})은 일반판과 지원 주기가 달라 Windows 11 전환을 강제하지 않았습니다(호환성: {r.Win11.Status}).");
        }
        else
        {
            switch (r.Win11.Status)
            {
                case "NotReady":
                    Add("PC_REPLACE", "High", "PC 교체",
                        $"Windows 10 지원이 2025-10-14 종료됐고, 이 PC 는 Windows 11 하드웨어 요구사항을 충족하지 못합니다: {string.Join(", ", r.Win11.Issues)}.");
                    break;
                case "NeedsConfig":
                    Add("OS_UPGRADE", "Medium", "Windows 11 업그레이드 (설정 변경 필요)",
                        $"하드웨어는 요구사항을 충족할 가능성이 높지만 펌웨어 설정 변경이 먼저 필요합니다: {string.Join(", ", r.Win11.Issues)}.");
                    break;
                case "Ready":
                    Add("OS_UPGRADE", "Medium", "Windows 11 업그레이드",
                        "하드웨어는 Windows 11 요구사항을 충족합니다. Windows 10 지원 종료로 OS 업그레이드가 필요합니다.");
                    break;
                case "Unknown":
                    Add("WIN11_CHECK", "Medium", "Windows 11 호환성 확인",
                        $"자동 판별하지 못한 항목이 있습니다: {string.Join(", ", r.Win11.Issues)}. 'PC 상태 검사' 앱으로 확인하세요.");
                    break;
            }
        }

        // 2) 메모리
        double ramGb = inv.RamInstalledMb / 1024.0;
        string? ramShortage = null;
        if (ramGb is > 0 and <= 4.5)
        {
            ramShortage = $"설치 메모리 {ramGb:0.#}GB 는 Windows 10/11 업무용으로 부족합니다";
        }
        else if (sufficient && (st.MemPressurePct >= MemPressureHighPct || r.Stability.LowMemoryWarnings >= 2))
        {
            ramShortage = st.MemPressurePct >= MemPressureHighPct
                ? $"{basisLabel}의 {st.MemPressurePct:0.#}%에서 메모리 부족(가용 ≤{c.MemoryAvailableLowPercent:0}% 또는 커밋 ≥{c.CommitHighPercent:0}%) — {MemEvidence(st)}"
                : $"Windows 메모리 부족 경고 {r.Stability.LowMemoryWarnings}회";
        }

        // 한 프로그램이 원인인지: 프로세스 스냅샷이 있는 이벤트만 분모로 사용(빈도 제한으로 생략된 이벤트 제외)
        int memSnapshots = SnapshotEvents(input.Events, PressureKind.Memory);
        var memTop = r.MemoryOffenders.FirstOrDefault();
        bool memDominated = sufficient && memTop is not null && memSnapshots >= MinSnapshotEventsForDominance && memTop.Rank1 >= 0.6 * memSnapshots;

        // 부족의 대부분이 한 프로그램 때문이면 하드웨어 조치는 '보통'으로 낮추고 프로그램 점검을 먼저(설치 메모리가 아주 적으면 제외)
        bool programFirst = memDominated && ramGb > 4.5;
        string ramPriority = programFirst ? "Medium" : "High";
        string programNote = programFirst ? $" 다만 메모리 부족의 대부분이 '{memTop!.Name}' 사용 중에 생겨, 이 프로그램을 먼저 점검한 뒤 결정하세요." : "";
        if (ramShortage is not null && inv.RamSoldered)
        {
            // 온보드 메모리는 증설이 불가하므로 사실상 교체 대상
            Add("PC_REPLACE", ramPriority, "PC 교체 (메모리 증설 불가)",
                $"{ramShortage}. 메모리가 온보드(납땜) {ramGb:0.#}GB 로 보여 증설이 불가하므로 {TargetRam(ramGb)}GB 이상 PC 로 교체가 필요합니다.{programNote}");
        }
        else if (ramShortage is not null)
        {
            Add("RAM_UPGRADE", ramPriority, "메모리 증설", $"{ramShortage}. 현재 {ramGb:0.#}GB → {TargetRam(ramGb)}GB 권장.{SlotNote(inv)}{programNote}");
        }
        else if (sufficient && st.MemPressurePct >= MemPressureWatchPct)
        {
            // 온보드 메모리는 증설할 수 없으므로 '증설 검토'를 권하면 안 됨
            string next = inv.RamSoldered
                ? $"메모리가 온보드(납땜)라 증설할 수 없습니다. 부족이 늘면 {TargetRam(ramGb)}GB 이상 PC 로 교체를 검토하세요."
                : $"추세를 지켜보고 증가 시 {TargetRam(ramGb)}GB 증설을 검토하세요.";
            Add("RAM_WATCH", "Medium", "메모리 사용 관찰",
                $"{basisLabel}의 {st.MemPressurePct:0.#}%에서 메모리 부족이 관측됐습니다 — {MemEvidence(st)}. {next}");
        }
        else if (sufficient && st.MemTight20Pct >= MemTightNotePct)
        {
            r.Findings.Add($"메모리 부족 기준에는 못 미치지만, 가용 메모리 20% 이하로 쓴 시간이 {basisLabel}의 {st.MemTight20Pct:0.#}%입니다 — 프로그램을 더 띄우면 부족해질 수 있습니다.");
        }

        double ramVisibleGb = (inv.RamVisibleMb > 0 ? inv.RamVisibleMb : inv.RamInstalledMb) / 1024.0;
        if (sufficient && ramVisibleGb > 0 && st.CommitGbP95 > ramVisibleGb)
        {
            r.Findings.Add($"프로그램들이 요구한 메모리(커밋)가 상위 5% 구간에서 {st.CommitGbP95:0.#}GB로, 설치 메모리 {ramGb:0.#}GB를 넘습니다 — 넘는 만큼은 디스크(페이지 파일)에 의존합니다.");
        }

        if (memDominated && memTop is not null)
        {
            Add("SOFTWARE_REVIEW", "Medium", $"프로그램 점검: {memTop.Name}",
                $"프로세스 기록이 있는 메모리 부족 {memSnapshots}건 중 {memTop.Rank1}건에서 '{memTop.Name}' 이(가) 메모리 사용 1위(평균 {memTop.AvgPrivateMb:0}MB). {(inv.RamSoldered ? "교체" : "증설")}를 결정하기 전에 해당 프로그램의 과다 사용·누수 여부를 확인하세요.",
                key: "SOFTWARE_REVIEW" + $"프로그램 점검: {memTop.Name}");
        }

        // 3) 디스크
        var sysDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        if (sysDisk?.MediaType == "HDD")
        {
            bool busy = sufficient && st.DiskBusyPct >= DiskBusyHddPct;
            string latencyNote = sufficient && st.DiskLatencyAvgMs is { } hddMs ? $" 디스크 응답 시간은 평균 {hddMs:0.#}ms 입니다(SSD 는 보통 1ms 안팎)." : "";
            Add("SSD_REPLACE", busy ? "High" : "Medium", "SSD 교체",
                $"시스템 디스크가 HDD 입니다{(busy ? $"(디스크 포화 {st.DiskBusyPct:0.#}% — 8시간 사용 기준 하루 약 {UsageStats.MinutesPer8h(st.DiskBusyPct):0.#}분)" : "")}. SSD 교체가 비용 대비 체감 효과가 가장 큽니다.{latencyNote}");
        }
        else if (sufficient && st.DiskBusyPct >= DiskBusySsdPct)
        {
            r.Findings.Add($"SSD 사용 중이지만 디스크 포화 비율이 {st.DiskBusyPct:0.#}% 입니다. 백신 검사·동기화·인덱싱 프로그램을 확인하세요.");
        }
        if (sufficient && sysDisk?.MediaType == "SSD" && st.DiskLatencyP95Ms >= SsdSlowLatencyMs)
        {
            r.Findings.Add($"SSD 인데 디스크 응답 시간이 상위 5% 구간에서 {st.DiskLatencyP95Ms:0.#}ms 로 깁니다 — 디스크 상태와 백신 검사·동기화 프로그램을 확인하세요.");
        }

        // 디스크 건강(SMART): 고장 위험이면 즉시 백업·교체, 주의면 점검
        foreach (var d in inv.Disks.Where(d => !d.IsRemovable && d.Health is not null))
        {
            var problems = d.Health!.Problems();
            if (problems.Count == 0) continue;
            string where = d.IsSystem ? "시스템 디스크" : $"디스크 {d.Number}";
            string detail = $"{where}({d.Model ?? d.MediaType}): {string.Join(", ", problems)}.";
            if (d.Health.Level == "Critical" && d.IsSystem && d.MediaType == "HDD")
                Add("SSD_REPLACE", "High", "SSD 교체", detail + " 데이터를 먼저 백업하세요.");
            else if (d.Health.Level == "Critical")
                Add("DISK_REPLACE", "High", "디스크 교체", detail + " 데이터를 즉시 백업하고 교체하세요.");
            else
                Add("DISK_CHECK", "Medium", "디스크 점검", detail + " 백업 상태를 확인하고 추이를 지켜보세요.");
        }

        var sysVol = inv.Volumes.FirstOrDefault(v => v.IsSystem);
        if (sysVol is { SizeGb: > 0 } && (sysVol.FreeGb / sysVol.SizeGb < 0.10 || sysVol.FreeGb < 10))
        {
            Add("DISK_CLEANUP", "Medium", "디스크 공간 확보",
                $"시스템 드라이브 여유 공간이 {sysVol.FreeGb:0.#}GB ({sysVol.FreeGb * 100 / sysVol.SizeGb:0}%) 입니다. 정리 또는 용량 확장이 필요합니다.");
        }

        // 4) CPU
        var cpuSupport = Win11Check.EvaluateCpu(inv.CpuName);
        bool old = inv.EstimatedAgeYears >= ReplaceAgeYears;
        int cpuSnapshots = SnapshotEvents(input.Events, PressureKind.Cpu);
        var cpuTop = r.CpuOffenders.FirstOrDefault();
        bool cpuDominated = cpuTop is not null && cpuSnapshots >= MinSnapshotEventsForDominance && cpuTop.Rank1 >= 0.5 * cpuSnapshots;
        if (sufficient && st.CpuHighPct >= CpuHighPct)
        {
            var reasons = new List<string>();
            if (old) reasons.Add($"추정 연식 {inv.EstimatedAgeYears:0.#}년");
            if (cpuSupport == CpuSupport.Unsupported) reasons.Add("구형 CPU");
            if (inv.CpuCores is > 0 and <= 2) reasons.Add($"{inv.CpuCores}코어");

            string perDay = $"8시간 사용 기준 하루 약 {UsageStats.MinutesPer8h(st.CpuHighPct):0.#}분";
            if (reasons.Count > 0)
                Add("PC_REPLACE", "High", "PC 교체",
                    $"{basisLabel}의 {st.CpuHighPct:0.#}%에서 CPU 포화(≥{c.CpuHighPercent:0}%, {perDay}) — CPU 성능 부족({string.Join(", ", reasons)}).");
            else if (!cpuDominated)
                Add("CPU_REVIEW", "Medium", "CPU 사용 검토",
                    $"{basisLabel}의 {st.CpuHighPct:0.#}%에서 CPU 포화가 관측됐습니다({perDay}). 사용 프로그램 대비 CPU 사양이 부족할 수 있으니 상위 프로세스를 확인하세요.");
        }
        else if (sufficient && st.CpuHighPct >= CpuWatchPct)
        {
            r.Findings.Add($"{basisLabel}의 {st.CpuHighPct:0.#}%에서 CPU 포화 — 아직 조치 기준(≥{CpuHighPct:0}%) 미만입니다.");
        }

        if (cpuDominated && sufficient && st.CpuHighPct >= CpuWatchPct)
        {
            Add("SOFTWARE_REVIEW", "Medium", $"프로그램 점검: {cpuTop!.Name}",
                $"프로세스 기록이 있는 CPU 포화 {cpuSnapshots}건 중 {cpuTop.Rank1}건에서 '{cpuTop.Name}' 이(가) CPU 사용 1위(평균 {cpuTop.AvgCpu:0.#}%).",
                key: "SOFTWARE_REVIEW" + $"프로그램 점검: {cpuTop.Name}");
        }

        // CPU 사용률은 높지 않아도 속도 자체가 제한되면 느림(발열·전원 설정). 배터리 사용이 많으면 절전의 영향일 수 있음
        if (sufficient && st.CpuPerfUnderLoad < CpuThrottledPct)
        {
            string onBatteryNote = st.OnBatteryPct >= 30 ? $" 배터리로 쓴 시간이 {st.OnBatteryPct:0}%라 절전 설정의 영향일 수 있습니다." : "";
            r.Findings.Add($"CPU 를 많이 쓸 때의 속도가 정격의 {st.CpuPerfUnderLoad:0}%로 제한됩니다 — 발열(먼지·서멀)이나 전원 설정을 점검하세요.{onBatteryNote}");
        }

        // 5) 안정성
        var stab = r.Stability;
        if (stab.BugChecks >= 2)
            Add("STABILITY_CHECK", "High", "하드웨어 안정성 점검", $"블루스크린 {stab.BugChecks}회 — 메모리·디스크·드라이버 점검이 필요합니다.");
        else if (stab.UnexpectedShutdowns >= 3 || stab.BugChecks == 1)
            Add("STABILITY_CHECK", "Medium", "안정성 점검", $"비정상 종료 {stab.UnexpectedShutdowns}회, 블루스크린 {stab.BugChecks}회.");

        // 6) 배터리(노트북): 완충 용량이 설계 대비 줄면 전원 없이 쓰는 시간이 짧아지고 갑자기 꺼질 수 있음
        if (inv.BatteryHealthPercent is { } battery)
        {
            int cycleCount = inv.Batteries.Sum(b => b.CycleCount ?? 0);
            string cycles = cycleCount > 0 ? $", 충전 횟수 {cycleCount}회" : "";
            if (st.OnBatteryPct >= 1) cycles += $", 측정 기간의 {st.OnBatteryPct:0}%를 배터리로 사용";
            if (battery <= BatteryReplaceHighPct)
                Add("BATTERY_REPLACE", "High", "배터리 교체",
                    $"배터리 수명이 설계 용량의 {battery:0}%로 크게 줄었습니다{cycles}. 전원 없이 쓸 수 있는 시간이 매우 짧고 갑자기 꺼질 수 있습니다.");
            else if (battery <= BatteryReplacePct)
                Add("BATTERY_REPLACE", "Medium", "배터리 교체 검토",
                    $"배터리 수명이 설계 용량의 {battery:0}%입니다{cycles}. 회의·외근 등 전원 없이 자주 쓴다면 교체를 권장합니다.");
            else if (battery <= BatteryWatchPct)
                r.Findings.Add($"배터리 수명 {battery:0}%{cycles} — 사용 기간에 따른 정상 노화 범위입니다.");
        }

        if (stab.AppCrashes >= 20)
            r.Findings.Add($"앱 비정상 종료 {stab.AppCrashes}회 (최다: {string.Join(", ", stab.TopCrashApps.Take(3).Select(a => $"{a.Name} {a.Count}회"))}).");
        if (stab.BootAvgSec > 120)
            r.Findings.Add($"평균 부팅 시간 {stab.BootAvgSec:0}초 — 시작 프로그램 점검을 권장합니다.");
        if (old)
            r.Findings.Add($"추정 연식 {inv.EstimatedAgeYears:0.#}년 (BIOS 날짜 기준, BIOS 업데이트 시 실제보다 짧게 표시될 수 있음).");

        // ---------- 점수 / 판정 ----------
        recs.Sort((a, b) => Rank(a.Priority).CompareTo(Rank(b.Priority)));
        r.Score = Math.Min(100, recs.Sum(x => x.Priority switch { "High" => 40, "Medium" => 15, _ => 5 }));
        (r.Verdict, r.VerdictLabel) =
            recs.Any(x => x is { Code: "PC_REPLACE", Priority: "High" }) ? ("Replace", "교체 권장")
            : recs.Any(x => x.Priority == "High") ? ("Upgrade", "업그레이드 권장")
            : recs.Count > 0 ? ("Watch", "점검 필요")
            : sufficient ? ("Ok", "양호")
            : ("InsufficientData", "데이터 부족");

        SubmissionSummary.Build(r, inv, basisLabel);
        return r;
    }

    /// <summary>권고 문장에 붙이는 보강 근거: 시간 환산, 빠듯한 구간, 실제 지연 여부.</summary>
    private static string MemEvidence(UsageStats st)
    {
        var parts = new List<string> { $"8시간 사용 기준 하루 약 {UsageStats.MinutesPer8h(st.MemPressurePct):0.#}분" };
        if (st.MemTight20Pct > 0) parts.Add($"가용 20% 이하로 쓴 시간 {st.MemTight20Pct:0.#}%");
        if (HardFaultRatio(st) >= 3) parts.Add($"부족할 때 하드 페이지 폴트가 평소의 약 {HardFaultRatio(st):0}배");
        return string.Join(", ", parts);
    }

    /// <summary>메모리 부족이던 분의 하드 폴트가 평소(부족하지 않던 분)의 몇 배인지. 표본이 부족하면 null.</summary>
    internal static double? HardFaultRatio(UsageStats st) =>
        st.HardFaultsPressureMedian is { } pressure && st.HardFaultsCalmMedian is { } calm ? pressure / Math.Max(calm, 1) : null;

    /// <summary>현지 시각이 설정의 업무시간(기본 평일 9~18시)인지.</summary>
    internal static bool IsWorkTime(DateTimeOffset local, AgentConfig c)
    {
        if (c.WorkdaysOnly && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        return local.Hour >= c.WorkStartHour && local.Hour < c.WorkEndHour;
    }

    private static int SnapshotEvents(IEnumerable<PressureRecord> events, PressureKind kind) =>
        events.Count(e => e.Kind == kind && e.Processes is { Count: > 0 });

    private static List<Offender> Offenders(IEnumerable<PressureRecord> events, PressureKind kind)
    {
        var map = new Dictionary<string, (Offender O, double Cpu, double Mem, double Io)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (e.Kind != kind || e.Processes is not { Count: > 0 }) continue;
            // 이름 기준으로 합친 순위(같은 이름이 경로만 달라 두 줄이면 한 이벤트에서 두 번 세어지는 것을 방지)
            var ranked = e.RankedByName().Take(5).ToList();
            for (int i = 0; i < ranked.Count; i++)
            {
                var p = ranked[i];
                if (!map.TryGetValue(p.Name, out var acc))
                    acc = (new Offender { Name = p.Name, Path = p.Path, Publisher = p.Publisher }, 0, 0, 0);
                acc.O.Events++;
                if (i == 0) acc.O.Rank1++;
                if (i < 3) acc.O.Top3++;
                acc.Cpu += p.CpuPercent;
                acc.Mem += p.PrivateBytes / 1048576.0;
                acc.Io += p.IoBytesPerSec / 1024.0;
                map[p.Name] = acc;
            }
        }
        return map.Values
            .Select(a =>
            {
                a.O.AvgCpu = Round(a.Cpu / a.O.Events);
                a.O.AvgPrivateMb = Math.Round(a.Mem / a.O.Events);
                a.O.AvgIoKBps = Math.Round(a.Io / a.O.Events);
                return a.O;
            })
            .OrderByDescending(o => o.Rank1)
            .ThenByDescending(o => o.Top3)
            .ThenByDescending(o => o.Events)
            .Take(10)
            .ToList();
    }

    private static StabilityStats Stability(AnalysisInput input)
    {
        var logs = input.Logs;
        var boots = logs.Where(l => l.Category == "Boot" && l.Value > 0).OrderByDescending(l => l.TimeUtc).Take(10).ToList();
        return new StabilityStats
        {
            AppCrashes = logs.Count(l => l.Category == "AppCrash"),
            AppHangs = logs.Count(l => l.Category == "AppHang"),
            BugChecks = logs.Count(l => l.Category == "BugCheck"),
            UnexpectedShutdowns = logs.Count(l => l.Category == "UnexpectedShutdown"),
            LowMemoryWarnings = logs.Count(l => l.Category == "LowMemory"),
            BootCount = logs.Count(l => l.Category == "Boot"),
            BootAvgSec = boots.Count > 0 ? Math.Round(boots.Average(b => b.Value!.Value) / 1000, 1) : null,
            TopCrashApps = logs
                .Where(l => l.Category is "AppCrash" or "AppHang" && l.Detail is not null)
                .GroupBy(l => l.Detail!.Split(' ')[0], StringComparer.OrdinalIgnoreCase)
                .Select(g => new NameCount { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList(),
        };
    }

    private static string SlotNote(InventoryInfo inv)
    {
        if (inv.RamSlotsTotal is { } total && inv.RamSlotsUsed is { } used)
        {
            if (total > used) return $" 빈 슬롯 {total - used}개(모듈 추가 가능).";
            if (total > 0) return " 빈 슬롯 없음 — 기존 모듈을 큰 용량으로 교체해야 합니다.";
        }
        return "";
    }

    private static int TargetRam(double gb) => gb switch
    {
        <= 8.5 => 16,
        <= 16.5 => 32,
        <= 32.5 => 64,
        _ => (int)Math.Ceiling(gb * 2),
    };

    private static int Rank(string priority) => priority switch { "High" => 0, "Medium" => 1, _ => 2 };

    private static double Hours(IReadOnlyCollection<MetricMinute> m) => m.Sum(x => (double)x.CoveredSeconds) / 3600.0;

    private static double Weighted(IReadOnlyCollection<MetricMinute> m, Func<MetricMinute, double> selector)
    {
        double w = m.Sum(x => (double)x.CoveredSeconds);
        return w > 0 ? m.Sum(x => selector(x) * x.CoveredSeconds) / w : 0;
    }

    internal static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        double rank = p / 100 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static double Round(double v) => Math.Round(v, 1);
}
