using PcAgent.Collection;

namespace PcAgent.Analysis;

/// <summary>수집 진행 상황: 데이터가 있는 날 수, 관측 시간, 판정에 필요한 최소 관측을 채웠는지.</summary>
public sealed record CollectionProgress(int Days, double ObservedHours, double WorkHours)
{
    public const int RecommendedDays = 14;

    /// <summary>UpgradeAdvisor 와 같은 기준: 업무시간이 모자라면 전체 시간으로 판정하므로 전체 관측 시간이 기준.</summary>
    public bool Sufficient => ObservedHours >= UpgradeAdvisor.MinObservedHours;

    public static CollectionProgress Compute(IEnumerable<MetricMinute> metrics, AgentConfig config, TimeZoneInfo tz)
    {
        double all = 0, work = 0;
        var days = new HashSet<DateOnly>();
        foreach (var m in metrics)
        {
            var local = TimeZoneInfo.ConvertTime(m.MinuteUtc, tz);
            days.Add(DateOnly.FromDateTime(local.DateTime));
            all += m.CoveredSeconds;
            if (UpgradeAdvisor.IsWorkTime(local, config)) work += m.CoveredSeconds;
        }
        return new CollectionProgress(days.Count, Math.Round(all / 3600, 1), Math.Round(work / 3600, 1));
    }

    public string Describe() =>
        $"수집 {Days}일 (권장 {RecommendedDays}일) · 관측 {ObservedHours:0.#}시간(업무시간 {WorkHours:0.#}시간) · " +
        (Sufficient ? "판정 가능" : $"판정까지 {UpgradeAdvisor.MinObservedHours - ObservedHours:0.#}시간 더 필요");
}
