using PcAgent.Collection;
using PcAgent.Storage;

namespace PcAgent.Hosting;

/// <summary>
/// 샘플 1개를 받아 1분 집계 → 저장, 자원 부족 감지 → 프로세스 스냅샷 → 저장까지 처리합니다.
/// </summary>
internal sealed class SamplingPipeline
{
    private readonly AgentConfig _config;
    private readonly DataStore _store;
    private readonly ProcessSnapshotter _snapshotter;
    private readonly MinuteAggregator _aggregator;
    private readonly PressureDetector _cpu;
    private readonly PressureDetector _memory;
    private readonly PressureDetector _disk;
    private readonly Dictionary<PressureKind, DateTimeOffset> _lastSnapshot = [];
    private DateOnly _snapshotDay;
    private int _snapshotsToday;

    public SamplingPipeline(AgentConfig config, DataStore store, ProcessSnapshotter snapshotter)
    {
        _config = config;
        _store = store;
        _snapshotter = snapshotter;
        _aggregator = new MinuteAggregator(SampleThresholds.From(config), config.SampleIntervalSeconds);

        var recover = TimeSpan.FromSeconds(config.RecoverSustainSeconds);
        var maxGap = TimeSpan.FromSeconds(config.SampleIntervalSeconds * 3 + 5);
        _cpu = new PressureDetector(PressureKind.Cpu, TimeSpan.FromSeconds(config.CpuSustainSeconds), recover, maxGap);
        _memory = new PressureDetector(PressureKind.Memory, TimeSpan.FromSeconds(config.MemorySustainSeconds), recover, maxGap);
        _disk = new PressureDetector(PressureKind.Disk, TimeSpan.FromSeconds(config.DiskSustainSeconds), recover, maxGap);
    }

    public async Task ProcessAsync(SystemSample s, CancellationToken ct)
    {
        if (_aggregator.Add(s) is { } minute) _store.AppendMetric(minute);

        var c = _config;
        await HandleAsync(_cpu.Update(s.TimestampUtc, s.CpuPercent >= c.CpuHighPercent, s.CpuPercent <= c.CpuRecoverPercent, s.CpuPercent), s, ct);

        bool memEnter = s.MemAvailPercent <= c.MemoryAvailableLowPercent || s.CommitPercent >= c.CommitHighPercent;
        bool memExit = s.MemAvailPercent >= c.MemoryAvailableRecoverPercent && s.CommitPercent <= c.CommitRecoverPercent;
        await HandleAsync(_memory.Update(s.TimestampUtc, memEnter, memExit, 100 - s.MemAvailPercent), s, ct);

        if (s.DiskActivePercent is { } d)
            await HandleAsync(_disk.Update(s.TimestampUtc, d >= c.DiskActiveHighPercent, d <= c.DiskActiveRecoverPercent, d), s, ct);
    }

    /// <summary>종료 시 남은 1분 집계와 진행 중 이벤트를 기록합니다.</summary>
    public void Shutdown()
    {
        if (_aggregator.Flush() is { } minute) _store.AppendMetric(minute);
        var now = DateTimeOffset.UtcNow;
        foreach (var detector in new[] { _cpu, _memory, _disk })
        {
            if (detector.ForceEnd(now) is { } t) _store.AppendPressure(EndRecord(t));
        }
    }

    private async Task HandleAsync(PressureTransition? transition, SystemSample s, CancellationToken ct)
    {
        if (transition is not { } t) return;
        if (t.Type == TransitionType.Ended)
        {
            _store.AppendPressure(EndRecord(t));
            return;
        }

        var record = new PressureRecord
        {
            Id = t.EventId,
            Kind = t.Kind,
            Phase = PressureRecord.PhaseStart,
            StartedUtc = t.StartedUtc,
            Peak = t.Peak,
            CpuPercent = Math.Round(s.CpuPercent, 1),
            MemAvailPercent = Math.Round(s.MemAvailPercent, 1),
            CommitPercent = Math.Round(s.CommitPercent, 1),
            DiskActivePercent = s.DiskActivePercent is { } d ? Math.Round(d, 1) : null,
        };

        if (CanSnapshot(t.Kind, s.TimestampUtc))
        {
            try
            {
                record.Processes = await _snapshotter.CaptureAsync(_config.TopProcessCount, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("프로세스 스냅샷 실패", ex);
            }
        }
        else
        {
            record.SnapshotSkipped = true;
        }
        _store.AppendPressure(record);
    }

    /// <summary>스냅샷 빈도 제한: 유형별 쿨다운 + 하루 최대 횟수.</summary>
    private bool CanSnapshot(PressureKind kind, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (today != _snapshotDay)
        {
            _snapshotDay = today;
            _snapshotsToday = 0;
        }
        if (_snapshotsToday >= _config.MaxSnapshotsPerDay) return false;
        if (_lastSnapshot.TryGetValue(kind, out var last) && now - last < TimeSpan.FromMinutes(_config.SnapshotCooldownMinutes))
            return false;

        _lastSnapshot[kind] = now;
        _snapshotsToday++;
        return true;
    }

    private static PressureRecord EndRecord(PressureTransition t) => new()
    {
        Id = t.EventId,
        Kind = t.Kind,
        Phase = PressureRecord.PhaseEnd,
        StartedUtc = t.StartedUtc,
        EndedUtc = t.EndedUtc,
        Peak = t.Peak,
    };
}
