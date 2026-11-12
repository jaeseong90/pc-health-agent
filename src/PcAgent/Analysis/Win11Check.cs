using System.Text.RegularExpressions;
using PcAgent.Inventory;

namespace PcAgent.Analysis;

public enum CpuSupport
{
    Supported,
    Unsupported,
    Unknown,
}

public sealed class Win11Readiness
{
    /// <summary>
    /// AlreadyWindows11, Ready, NeedsConfig(펌웨어 설정만 바꾸면 됨), NotReady(하드웨어 미충족), Unknown
    /// </summary>
    public string Status { get; set; } = "Unknown";
    public List<string> Issues { get; set; } = [];
}

/// <summary>
/// Windows 11 요구사항 점검(추정). CPU 는 모델명 기반 휴리스틱이므로 최종 확인은 PC 상태 검사 앱으로 하세요.
/// </summary>
public static partial class Win11Check
{
    public static Win11Readiness Evaluate(InventoryInfo inv)
    {
        var r = new Win11Readiness();
        if (inv.IsWindows11)
        {
            r.Status = "AlreadyWindows11";
            return r;
        }

        var failures = new List<string>();   // 하드웨어 미충족 → 교체 대상
        var configs = new List<string>();    // 펌웨어 설정만 바꾸면 되는 항목
        var unknowns = new List<string>();

        switch (inv.TpmVersion)
        {
            case "2.0": break;
            case null: unknowns.Add("TPM 확인 불가"); break;
            // 8세대 이상 Intel / Ryzen 2000 이상은 펌웨어 TPM(PTT/fTPM)이 있으므로, 미감지는 대개 BIOS 에서 꺼진 상태
            case "none": configs.Add("TPM 미감지 — BIOS 에서 TPM(PTT/fTPM) 활성화 확인"); break;
            default: failures.Add($"TPM {inv.TpmVersion} (2.0 필요)"); break;
        }

        // UEFI 지원 PC 를 레거시(CSM) 모드로 설치한 경우가 많음 → MBR2GPT 로 전환 가능
        if (inv.IsUefi == false) configs.Add("레거시 BIOS 부팅 — UEFI 전환 필요(MBR2GPT)");
        else if (inv.IsUefi is null) unknowns.Add("펌웨어 유형 확인 불가");

        if (inv.RamInstalledMb is > 0 and < 4096) failures.Add($"메모리 {inv.RamInstalledMb / 1024.0:0.#}GB (4GB 필요)");

        var systemDisk = inv.Disks.FirstOrDefault(d => d.IsSystem);
        if (systemDisk is { SizeGb: > 0 and < 59.6 }) failures.Add($"시스템 디스크 {systemDisk.SizeGb:0}GB (64GB 필요)");

        if (inv.CpuCores is > 0 and < 2) failures.Add("CPU 코어 2개 미만");

        switch (EvaluateCpu(inv.CpuName))
        {
            case CpuSupport.Unsupported: failures.Add($"미지원 CPU ({inv.CpuName})"); break;
            case CpuSupport.Unknown: unknowns.Add($"CPU 지원 여부 자동 판별 불가 ({inv.CpuName ?? "알 수 없음"})"); break;
        }

        r.Issues = [.. failures, .. configs, .. unknowns];
        r.Status = failures.Count > 0 ? "NotReady"
            : configs.Count > 0 ? "NeedsConfig"
            : unknowns.Count > 0 ? "Unknown"
            : "Ready";
        return r;
    }

    /// <summary>Windows 10 Enterprise LTSC / IoT Enterprise LTSC — 일반판과 지원 주기가 다름.</summary>
    public static bool IsLtsc(string? editionId) =>
        editionId is not null &&
        (editionId.StartsWith("EnterpriseS", StringComparison.OrdinalIgnoreCase) ||
         editionId.StartsWith("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase));

    /// <summary>Intel 8세대+, AMD Ryzen 2000+ (Zen+) 를 지원으로 추정.</summary>
    public static CpuSupport EvaluateCpu(string? cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName)) return CpuSupport.Unknown;
        var name = cpuName.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ");

        if (name.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase)) return CpuSupport.Supported;
        if (CoreUltraRegex().IsMatch(name) || NewCoreRegex().IsMatch(name)) return CpuSupport.Supported;

        var m = IntelCoreRegex().Match(name);
        if (m.Success)
        {
            var digits = m.Groups[1].Value;
            int gen = digits.Length switch
            {
                5 => int.Parse(digits[..2]),
                4 when digits[0] == '1' => int.Parse(digits[..2]),
                4 => digits[0] - '0',
                _ => 1,
            };
            return gen >= 8 ? CpuSupport.Supported : CpuSupport.Unsupported;
        }
        if (IntelFirstGenRegex().IsMatch(name)) return CpuSupport.Unsupported;

        if (name.Contains("Ryzen AI", StringComparison.OrdinalIgnoreCase)) return CpuSupport.Supported;
        var a = RyzenRegex().Match(name);
        if (a.Success) return a.Groups[1].Value[0] >= '2' ? CpuSupport.Supported : CpuSupport.Unsupported;

        return CpuSupport.Unknown;
    }

    [GeneratedRegex(@"Core\s*Ultra", RegexOptions.IgnoreCase)]
    private static partial Regex CoreUltraRegex();

    // Intel Core 3/5/7 (예: "Intel Core 5 120U") — i 없는 신규 브랜드
    [GeneratedRegex(@"Intel\s+Core\s+[357]\s+\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex NewCoreRegex();

    [GeneratedRegex(@"\bi[3579]-(\d{3,5})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCoreRegex();

    [GeneratedRegex(@"\bi[3579]\s+CPU\s+\w?\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelFirstGenRegex();

    [GeneratedRegex(@"(?:Ryzen\s+\d+|Threadripper)\s+(?:PRO\s+)?(\d)\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenRegex();
}
