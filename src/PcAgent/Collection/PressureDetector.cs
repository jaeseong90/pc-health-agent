namespace PcAgent.Collection;

public enum TransitionType
{
    Started,
    Ended,
}

public readonly record struct PressureTransition(
    TransitionType Type,
    PressureKind Kind,
    Guid EventId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    double Peak);

/// <summary>
/// 자원 부족 상태 감지기.
/// - 진입 조건이 sustain 이상 "지속"돼야 시작 (순간 스파이크 무시). 시작 시각 = 조건이 처음 충족된 시각
/// - 해제 조건이 recover 이상 지속돼야 종료 (진입/해제 기준 분리 = 히스테리시스). 종료 시각 = 회복이 시작된 시각
/// - 절전/최대절전 등으로 샘플 간격이 maxGap 을 넘으면 마지막 샘플 시점에 종료
/// </summary>
public sealed class PressureDetector
{
    private TimeSpan _sustain;
    private TimeSpan _recover;
    private TimeSpan _maxGap;

    private DateTimeOffset? _enterSince;
    private DateTimeOffset? _exitSince;
    private DateTimeOffset? _last;
    private Guid _eventId;
    private DateTimeOffset _startedUtc;
    private double _peak;

    public PressureDetector(PressureKind kind, TimeSpan sustain, TimeSpan recover, TimeSpan maxGap)
    {
        Kind = kind;
        _sustain = sustain;
        _recover = recover;
        _maxGap = maxGap;
    }

    public PressureKind Kind { get; }
    public bool IsActive { get; private set; }

    /// <summary>진행 중인 상태(이벤트)를 유지한 채 기준 시간만 바꿉니다(설정 변경 시).</summary>
    public void Reconfigure(TimeSpan sustain, TimeSpan recover, TimeSpan maxGap)
    {
        _sustain = sustain;
        _recover = recover;
        _maxGap = maxGap;
    }

    public PressureTransition? Update(DateTimeOffset now, bool enter, bool exit, double value)
    {
        if (_last is { } last && now - last > _maxGap)
        {
            var gapEnd = IsActive ? End(last) : (PressureTransition?)null;
            _enterSince = null;
            _exitSince = null;
            _last = now;
            return gapEnd;
        }
        _last = now;

        if (!IsActive)
        {
            if (!enter)
            {
                _enterSince = null;
                return null;
            }
            _enterSince ??= now;
            if (now - _enterSince.Value < _sustain) return null;

            IsActive = true;
            _eventId = Guid.NewGuid();
            _startedUtc = _enterSince.Value;
            _peak = value;
            _exitSince = null;
            return new PressureTransition(TransitionType.Started, Kind, _eventId, _startedUtc, null, Math.Round(value, 1));
        }

        _peak = Math.Max(_peak, value);
        if (!exit)
        {
            _exitSince = null;
            return null;
        }
        _exitSince ??= now;
        return now - _exitSince.Value >= _recover ? End(_exitSince.Value) : null;
    }

    /// <summary>에이전트 종료 시 진행 중인 이벤트를 닫습니다.</summary>
    public PressureTransition? ForceEnd(DateTimeOffset now) => IsActive ? End(_exitSince ?? now) : null;

    private PressureTransition End(DateTimeOffset endedUtc)
    {
        IsActive = false;
        _enterSince = null;
        _exitSince = null;
        return new PressureTransition(TransitionType.Ended, Kind, _eventId, _startedUtc, endedUtc, Math.Round(_peak, 1));
    }
}
