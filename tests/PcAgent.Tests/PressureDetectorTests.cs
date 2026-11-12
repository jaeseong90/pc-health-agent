using PcAgent.Collection;

namespace PcAgent.Tests;

public class PressureDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);

    private static PressureDetector Create() =>
        new(PressureKind.Cpu, sustain: TimeSpan.FromSeconds(30), recover: TimeSpan.FromSeconds(30), maxGap: TimeSpan.FromSeconds(20));

    [Fact]
    public void Starts_only_after_condition_is_sustained()
    {
        var d = Create();
        for (int s = 0; s < 30; s += 5)
            Assert.Null(d.Update(T0.AddSeconds(s), enter: true, exit: false, value: 95));

        var t = d.Update(T0.AddSeconds(30), enter: true, exit: false, value: 97);

        Assert.NotNull(t);
        Assert.Equal(TransitionType.Started, t.Value.Type);
        Assert.Equal(T0, t.Value.StartedUtc);
        Assert.True(d.IsActive);
    }

    [Fact]
    public void Short_spike_is_ignored()
    {
        var d = Create();
        for (int s = 0; s <= 20; s += 5) d.Update(T0.AddSeconds(s), true, false, 99);
        Assert.Null(d.Update(T0.AddSeconds(25), enter: false, exit: true, value: 10));

        // 스파이크가 끊기면 지속 시간 카운트도 처음부터
        for (int s = 30; s < 60; s += 5) Assert.Null(d.Update(T0.AddSeconds(s), true, false, 99));
        Assert.False(d.IsActive);
    }

    [Fact]
    public void Ends_only_after_recovery_is_sustained_and_reports_peak()
    {
        var d = Create();
        for (int s = 0; s <= 30; s += 5) d.Update(T0.AddSeconds(s), true, false, 92);
        d.Update(T0.AddSeconds(35), true, false, 99);

        // 진입·해제 기준 사이 구간(히스테리시스)에서는 계속 활성
        for (int s = 40; s <= 60; s += 5) Assert.Null(d.Update(T0.AddSeconds(s), false, false, 80));
        Assert.True(d.IsActive);

        PressureTransition? end = null;
        for (int s = 65; s <= 95 && end is null; s += 5) end = d.Update(T0.AddSeconds(s), false, true, 20);

        Assert.NotNull(end);
        Assert.Equal(TransitionType.Ended, end.Value.Type);
        Assert.Equal(T0.AddSeconds(95), end.Value.EndedUtc);
        Assert.Equal(99, end.Value.Peak);
        Assert.False(d.IsActive);
    }

    [Fact]
    public void Sampling_gap_ends_active_event_at_last_sample()
    {
        var d = Create();
        for (int s = 0; s <= 30; s += 5) d.Update(T0.AddSeconds(s), true, false, 95);
        Assert.True(d.IsActive);

        // 절전 등으로 10분 공백
        var t = d.Update(T0.AddMinutes(10), true, false, 95);

        Assert.NotNull(t);
        Assert.Equal(TransitionType.Ended, t.Value.Type);
        Assert.Equal(T0.AddSeconds(30), t.Value.EndedUtc);
        Assert.False(d.IsActive);
    }

    [Fact]
    public void Gap_does_not_count_toward_sustain()
    {
        var d = Create();
        d.Update(T0, true, false, 95);
        // 공백 직후 샘플 하나로 바로 시작되면 안 됨
        Assert.Null(d.Update(T0.AddMinutes(5), true, false, 95));
        Assert.False(d.IsActive);
    }

    [Fact]
    public void ForceEnd_closes_active_event()
    {
        var d = Create();
        for (int s = 0; s <= 30; s += 5) d.Update(T0.AddSeconds(s), true, false, 95);

        var t = d.ForceEnd(T0.AddSeconds(40));

        Assert.Equal(TransitionType.Ended, t!.Value.Type);
        Assert.Null(d.ForceEnd(T0.AddSeconds(50)));
    }
}
