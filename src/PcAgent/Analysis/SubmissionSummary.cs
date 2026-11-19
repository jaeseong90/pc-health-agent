using PcAgent.Inventory;

namespace PcAgent.Analysis;

/// <summary>
/// 판정 결과를 결재·구매 담당자가 읽을 수 있는 형태로 풀어 씁니다.
/// 결론 한 줄, 시간·횟수로 환산한 근거, 항목별 점검표(사실 → 정상/주의/문제), 읽을 때 주의할 점.
/// 새로운 판정은 하지 않고, UpgradeAdvisor 가 낸 결과와 같은 기준값만 사용합니다.
/// </summary>
internal static class SubmissionSummary
{
    /// <summary>근거로 쓰기에 충분하다고 보는 측정 기간(업무일 2주).</summary>
    internal const int RecommendedBasisDays = 10;

    public static void Build(AnalysisResult r, InventoryInfo inv, string basisLabel)
    {
        r.Headline = Headline(r);
        r.Checklist = Checklist(r, inv);
        r.Evidence = Evidence(r, inv, basisLabel);
        r.Caveats = Caveats(r, inv);
    }

    private static string Headline(AnalysisResult r)
    {
        var recs = r.Recommendations;
        string Titles(IEnumerable<Recommendation> list) => string.Join(" · ", list.Select(x => x.Title));
        switch (r.Verdict)
        {
            case "Replace":
                var also = recs.Where(x => x.Priority == "High" && x.Code != "PC_REPLACE").ToList();
                return "PC 교체를 권장합니다." + (also.Count > 0 ? $" 교체 전까지 필요한 조치: {Titles(also)}." : "");
            case "Upgrade":
                return $"PC 교체까지는 필요하지 않고, 다음 조치를 권장합니다: {Titles(recs.Where(x => x.Priority == "High"))}.";
            case "Watch":
                return $"교체·증설을 확정하기 전에 확인할 항목이 {recs.Count}건 있습니다: {Titles(recs)}.";
            case "Ok":
                return "현재 사양으로 업무에 쓰는 데 문제가 없습니다.";
            default:
                return "측정 기간이 짧아 성능 판정은 보류했습니다. 사양으로 확인되는 항목만 점검했습니다.";
        }
    }

    private static List<CheckItem> Checklist(AnalysisResult r, InventoryInfo inv)
    {
        var st = r.Stats;
        var list = new List<CheckItem>();
        void Add(string item, string status, string value, string? note = null) =>
            list.Add(new CheckItem { Item = item, Status = status, Value = value, Note = string.IsNullOrWhiteSpace(note) ? null : note });

        // 사용 연수
        if (inv.EstimatedAgeYears is { } age)
            Add("사용 연수", age >= UpgradeAdvisor.ReplaceAgeYears ? "Warn" : "Ok", $"약 {age:0.#}년", "BIOS 날짜로 추정한 값 — 자산대장의 구매일로 확인하세요");
        else
            Add("사용 연수", "Unknown", "확인 불가", "자산대장의 구매일로 확인하세요");

        // Windows 11
        string issues = string.Join(", ", r.Win11.Issues);
        if (inv.IsWindows11 || r.Win11.Status == "AlreadyWindows11")
            Add("Windows 11", "Ok", "사용 중");
        else if (Win11Check.IsLtsc(inv.OsEdition))
            Add("Windows 11", "Ok", "Windows 10 LTSC", "일반판과 지원 주기가 달라 전환 대상이 아닙니다");
        else
            switch (r.Win11.Status)
            {
                case "Ready": Add("Windows 11", "Warn", "Windows 10 — 업그레이드 가능", "Windows 10 지원이 2025-10-14 에 끝나 업그레이드가 필요합니다"); break;
                case "NeedsConfig": Add("Windows 11", "Warn", "Windows 10 — 설정 변경 후 업그레이드 가능", issues); break;
                case "NotReady": Add("Windows 11", "Bad", "Windows 10 — 하드웨어가 Windows 11 미지원", issues); break;
                default: Add("Windows 11", "Unknown", "확인 필요", issues); break;
            }

        // 메모리: 용량(사실)과 여유(측정)를 나눠서
        double ramGb = inv.RamInstalledMb / 1024.0;
        string slots = inv.RamSoldered ? "온보드(납땜) 방식 — 증설 불가"
            : inv.RamSlotsTotal is { } total && inv.RamSlotsUsed is { } used
                ? (total > used ? $"빈 슬롯 {total - used}개 — 증설 가능" : "빈 슬롯 없음 — 기존 모듈을 교체해야 증설 가능")
            : "";
        Add("메모리 용량", ramGb is > 0 and <= 4.5 ? "Bad" : ramGb > 0 ? "Ok" : "Unknown", ramGb > 0 ? $"{ramGb:0.#}GB" : "확인 불가", slots);

        if (r.SufficientData)
        {
            string memStatus = st.MemPressurePct >= UpgradeAdvisor.MemPressureHighPct ? "Bad"
                : st.MemPressurePct >= UpgradeAdvisor.MemPressureWatchPct || st.MemTight20Pct >= UpgradeAdvisor.MemTightNotePct ? "Warn"
                : "Ok";
            string memNote = $"가용 메모리 20% 이하로 쓴 시간 {st.MemTight20Pct:0.#}%";
            if (st.CommitGbP95 is { } commit) memNote += $" · 프로그램이 요구한 메모리 상위 5% {commit:0.#}GB";
            Add("메모리 여유 (측정)", memStatus,
                $"부족 {st.MemPressurePct:0.#}% — 8시간 기준 하루 약 {UsageStats.MinutesPer8h(st.MemPressurePct):0.#}분", memNote);

            string cpuStatus = st.CpuHighPct >= UpgradeAdvisor.CpuHighPct ? "Bad"
                : st.CpuHighPct >= UpgradeAdvisor.CpuWatchPct || st.CpuPerfUnderLoad < UpgradeAdvisor.CpuThrottledPct ? "Warn"
                : "Ok";
            string cpuNote = $"평균 사용률 {st.CpuAvg:0.#}%";
            if (st.CpuPerfUnderLoad is { } perf) cpuNote += $" · 많이 쓸 때 속도 정격의 {perf:0}%";
            Add("CPU 여유 (측정)", cpuStatus,
                $"포화 {st.CpuHighPct:0.#}% — 8시간 기준 하루 약 {UsageStats.MinutesPer8h(st.CpuHighPct):0.#}분", cpuNote);
        }
        else
        {
            Add("메모리 여유 (측정)", "Unknown", "측정 기간 부족");
            Add("CPU 여유 (측정)", "Unknown", "측정 기간 부족");
        }

        // 저장장치: 종류, 상태, 여유 공간
        var sysDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        if (sysDisk is null)
        {
            Add("저장장치 종류", "Unknown", "확인 불가");
        }
        else
        {
            string diskNote = sysDisk.MediaType == "HDD" ? "HDD 는 부팅과 프로그램 실행이 느립니다 — SSD 교체 효과가 큽니다" : "";
            if (r.SufficientData && st.DiskLatencyAvgMs is { } ms) diskNote += (diskNote.Length > 0 ? " · " : "") + $"응답 시간 평균 {ms:0.#}ms";
            Add("저장장치 종류", sysDisk.MediaType switch { "HDD" => "Bad", "SSD" => "Ok", _ => "Unknown" },
                $"{sysDisk.MediaType} {sysDisk.SizeGb:0}GB ({sysDisk.BusType})", diskNote);
        }

        var worstDisk = inv.Disks.Where(d => !d.IsRemovable && d.Health is not null)
            .OrderByDescending(d => d.Health!.Level switch { "Critical" => 3, "Warning" => 2, "Ok" => 1, _ => 0 })
            .ThenByDescending(d => d.IsSystem)
            .FirstOrDefault();
        if (worstDisk?.Health is { } health && health.Level != "Unknown")
        {
            var problems = health.Problems();
            string healthNote = problems.Count > 0 ? string.Join(", ", problems)
                : string.Join(" · ", new[]
                {
                    health.PercentageUsed is { } wear ? $"수명 {wear}% 사용" : null,
                    health.PowerOnHours is { } hours ? $"사용 {hours:N0}시간" : null,
                }.Where(x => x is not null));
            Add("저장장치 상태", health.Level switch { "Critical" => "Bad", "Warning" => "Warn", _ => "Ok" }, DiskHealth.LevelLabel(health.Level), healthNote);
        }
        else
        {
            Add("저장장치 상태", "Unknown", "확인 불가");
        }

        var sysVol = inv.Volumes.FirstOrDefault(v => v.IsSystem);
        if (sysVol is { SizeGb: > 0 })
        {
            bool low = sysVol.FreeGb / sysVol.SizeGb < 0.10 || sysVol.FreeGb < 10;
            Add("저장 공간", low ? "Warn" : "Ok", $"여유 {sysVol.FreeGb:0}GB / {sysVol.SizeGb:0}GB ({sysVol.FreeGb * 100 / sysVol.SizeGb:0}%)");
        }

        // 배터리(노트북)
        if (inv.BatteryHealthPercent is { } battery)
        {
            int cycles = inv.Batteries.Sum(b => b.CycleCount ?? 0);
            var batteryNotes = new List<string>();
            if (cycles > 0) batteryNotes.Add($"충전 {cycles}회");
            if (st.OnBatteryPct is { } onBattery) batteryNotes.Add($"측정 기간의 {onBattery:0}%를 배터리로 사용");
            Add("배터리", battery <= UpgradeAdvisor.BatteryReplaceHighPct ? "Bad" : battery <= UpgradeAdvisor.BatteryReplacePct ? "Warn" : "Ok",
                $"새 배터리의 {battery:0}%", string.Join(" · ", batteryNotes));
        }

        // 안정성
        var stab = r.Stability;
        Add("안정성", stab.BugChecks >= 2 ? "Bad" : stab.BugChecks == 1 || stab.UnexpectedShutdowns >= 3 ? "Warn" : "Ok",
            $"블루스크린 {stab.BugChecks}회 · 비정상 종료 {stab.UnexpectedShutdowns}회", $"앱 오류 {stab.AppCrashes}회 · 응답 없음 {stab.AppHangs}회");

        return list;
    }

    private static List<string> Evidence(AnalysisResult r, InventoryInfo inv, string basisLabel)
    {
        var st = r.Stats;
        var list = new List<string>();
        double ramGb = inv.RamInstalledMb / 1024.0;

        if (ramGb is > 0 and <= 4.5)
            list.Add($"메모리 용량: 설치 메모리가 {ramGb:0.#}GB 로, Windows 10/11 업무용으로 쓰기에 부족한 용량입니다.");

        if (r.SufficientData)
        {
            bool memIssue = st.MemPressurePct >= UpgradeAdvisor.MemPressureWatchPct;
            bool memTight = st.MemTight20Pct >= UpgradeAdvisor.MemTightNotePct;

            if (st.MemPressurePct >= 0.5)
            {
                string line = $"메모리 부족: {basisLabel}의 {st.MemPressurePct:0.#}% — 8시간 사용 기준 하루 약 {UsageStats.MinutesPer8h(st.MemPressurePct):0.#}분입니다.";
                if (st.BasisDays > 0)
                {
                    line += $" 측정한 {st.BasisDays}일 가운데 {st.MemPressureDays}일에서 5분 이상 이어졌";
                    line += st.MemWorstDay is { } worst && st.MemWorstDayMinutes >= 5
                        ? $"고, 가장 심한 날({ShortDate(worst)})은 {st.MemWorstDayMinutes:0}분이었습니다."
                        : "습니다.";
                }
                list.Add(line);
            }
            if (st.MemTight20Pct >= 10)
                list.Add($"메모리 여유: 가용 메모리가 20% 이하인 시간이 {st.MemTight20Pct:0.#}%(15% 이하는 {st.MemTight15Pct:0.#}%)입니다. 부족 기준에 닿기 직전 상태로 쓰는 시간이 그만큼 깁니다.");
            if (memIssue && UpgradeAdvisor.HardFaultRatio(st) is { } ratio && ratio >= 3)
                list.Add($"체감 지연: 메모리가 부족한 동안 디스크에서 메모리를 다시 읽어 오는 횟수(하드 페이지 폴트)가 평소의 약 {ratio:0}배로 늘었습니다" +
                         $"(초당 {st.HardFaultsPressureMedian:0.#}회, 평소 {st.HardFaultsCalmMedian:0.#}회). 디스크 접근이 늘어 느려짐을 체감하기 쉬운 구간입니다.");
            if (st.CommitGbP95 is { } commit && ramGb > 0)
                list.Add($"필요 메모리: 프로그램들이 요구한 메모리는 상위 5% 구간에서 {commit:0.#}GB(최대 {st.CommitGbMax:0.#}GB)로, 설치 메모리 {ramGb:0.#}GB 의 {commit * 100 / ramGb:0}%입니다.");
            if ((memIssue || memTight) && inv.RamSoldered)
                list.Add("증설 가능 여부: 메모리가 메인보드에 납땜된 온보드 방식이라 증설할 수 없습니다. 용량을 늘리려면 PC 를 교체해야 합니다.");
            if (memIssue && r.MemoryOffenders.Count > 0)
                list.Add("부족할 때 메모리를 많이 쓴 프로그램: " +
                         string.Join(", ", r.MemoryOffenders.Take(3).Select(o => $"{ProcessNotes.Label(o.Name)} 약 {o.AvgPrivateMb / 1024:0.#}GB")) + ".");

            if (st.CpuHighPct >= UpgradeAdvisor.CpuWatchPct)
                list.Add($"CPU 포화: {basisLabel}의 {st.CpuHighPct:0.#}% — 8시간 사용 기준 하루 약 {UsageStats.MinutesPer8h(st.CpuHighPct):0.#}분 동안 CPU 를 한계까지 썼습니다.");
            if (st.CpuPerfUnderLoad < UpgradeAdvisor.CpuThrottledPct)
                list.Add($"CPU 속도 제한: CPU 를 많이 쓸 때의 속도가 정격의 {st.CpuPerfUnderLoad:0}%에 그칩니다(발열 또는 전원 설정).");
            if (st.DiskBusyPct >= UpgradeAdvisor.DiskBusyHddPct)
                list.Add($"디스크 포화: {basisLabel}의 {st.DiskBusyPct:0.#}% — 8시간 사용 기준 하루 약 {UsageStats.MinutesPer8h(st.DiskBusyPct):0.#}분 동안 디스크가 쉬지 못했습니다.");
        }

        var sysDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        if (sysDisk?.MediaType == "HDD")
            list.Add("저장장치: 시스템 디스크가 HDD 입니다" +
                     (r.SufficientData && st.DiskLatencyAvgMs is { } ms ? $"(응답 시간 평균 {ms:0.#}ms, SSD 는 보통 1ms 안팎)." : "."));
        foreach (var d in inv.Disks.Where(d => !d.IsRemovable && d.Health is not null))
        {
            var problems = d.Health!.Problems();
            if (problems.Count > 0)
                list.Add($"저장장치 상태({(d.IsSystem ? "시스템 디스크" : $"디스크 {d.Number}")}): {string.Join(", ", problems)}.");
        }

        if (!inv.IsWindows11 && !Win11Check.IsLtsc(inv.OsEdition) && r.Win11.Status == "NotReady")
            list.Add($"Windows 11: 하드웨어가 요구사항을 충족하지 못합니다({string.Join(", ", r.Win11.Issues)}). Windows 10 은 2025-10-14 에 지원이 끝났습니다.");
        if (inv.EstimatedAgeYears >= UpgradeAdvisor.ReplaceAgeYears)
            list.Add($"사용 연수: 약 {inv.EstimatedAgeYears:0.#}년입니다(BIOS 날짜 기준 추정).");
        if (inv.BatteryHealthPercent <= UpgradeAdvisor.BatteryReplacePct)
            list.Add($"배터리: 완충 용량이 새 배터리의 {inv.BatteryHealthPercent:0}%로 줄었습니다.");
        if (r.Stability.BugChecks >= 1 || r.Stability.UnexpectedShutdowns >= 3)
            list.Add($"안정성: 블루스크린 {r.Stability.BugChecks}회, 비정상 종료 {r.Stability.UnexpectedShutdowns}회가 기록됐습니다.");

        if (list.Count == 0)
        {
            list.Add(r.SufficientData
                ? "측정 기간 동안 CPU·메모리·디스크 부족이 거의 없었고, 사양·저장장치 상태·안정성에서도 문제가 확인되지 않았습니다."
                : "사양·저장장치 상태·안정성에서는 문제가 확인되지 않았습니다. 성능은 측정 기간이 부족해 판단하지 않았습니다.");
        }
        return list;
    }

    private static List<string> Caveats(AnalysisResult r, InventoryInfo inv)
    {
        var st = r.Stats;
        var list = new List<string>();
        if (!r.SufficientData)
        {
            list.Add($"관측 시간이 {st.BasisHours:0.#}시간으로 부족해 성능 항목은 판정하지 않았습니다(최소 {UpgradeAdvisor.MinObservedHours:0}시간).");
            return list;
        }

        if (st.BasisDays < RecommendedBasisDays)
            list.Add($"측정 기간이 {st.BasisDays}일로 권장({RecommendedBasisDays}일, 약 2주)보다 짧습니다. 평소 업무가 고루 포함되도록 더 수집한 뒤 다시 내보내면 근거가 탄탄해집니다.");

        // 하루에 몰린 결과는 반복성을 뒷받침하지 못함
        if (st.BasisDays >= 3 && st.MemPressureMinutes >= 10 && st.MemWorstDay is { } worst && st.MemWorstDayMinutes >= 0.7 * st.MemPressureMinutes)
            list.Add($"메모리 부족 시간의 {st.MemWorstDayMinutes * 100 / st.MemPressureMinutes:0}%가 하루({ShortDate(worst)})에 몰려 있습니다. 반복되는 현상인지 더 지켜볼 필요가 있습니다.");

        // 전체 사용률 기준의 한계: 코어가 많으면 프로그램 하나의 속도 한계가 드러나지 않음
        if (inv.CpuLogicalProcessors >= 8 && st.CpuHighPct < UpgradeAdvisor.CpuWatchPct)
            list.Add($"CPU 포화는 전체 코어의 사용률로 판단합니다. 논리 프로세서가 {inv.CpuLogicalProcessors}개인 이 PC 에서는 프로그램 하나가 코어 하나를 다 써도 전체 사용률은 약 {100.0 / inv.CpuLogicalProcessors:0}%라서, 특정 프로그램이 느린 경우는 이 수치에 나타나지 않습니다.");

        return list;
    }

    /// <summary>"2026-11-13" → "11/13".</summary>
    private static string ShortDate(string isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", out var d) ? $"{d.Month}/{d.Day}" : isoDate;
}

/// <summary>리포트를 읽는 사람이 이름만으로 알기 어려운 프로세스에 붙이는 설명.</summary>
internal static class ProcessNotes
{
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        // 설명에는 괄호를 쓰지 않음(Label 이 괄호로 감싸므로)
        ["vmmem"] = "WSL·Docker·Hyper-V 가상 머신",
        ["vmmemWSL"] = "WSL·Docker 가상 머신",
        ["Memory Compression"] = "Windows 메모리 압축",
        ["MsMpEng"] = "Windows Defender 백신",
        ["System"] = "Windows 커널",
        ["svchost"] = "Windows 서비스",
        ["SearchIndexer"] = "Windows 검색 색인",
        ["TiWorker"] = "Windows 업데이트",
        ["dwm"] = "Windows 화면 표시",
    };

    public static string? Describe(string name) => Notes.GetValueOrDefault(name);

    /// <summary>"vmmem(WSL·Docker·Hyper-V 가상 머신)" 처럼 이름 뒤에 설명을 붙임. 설명이 없으면 이름 그대로.</summary>
    public static string Label(string name) => Describe(name) is { } note ? $"{name}({note})" : name;
}
