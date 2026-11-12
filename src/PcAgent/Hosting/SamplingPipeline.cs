using PcAgent.Collection;
using PcAgent.Privacy;
using PcAgent.Storage;

namespace PcAgent.Hosting;

/// <summary>
/// 샘플 1개를 받아 1분 집계 → 저장, 자원 부족 감지 → 프로세스 스냅샷 → 저장까지 처리합니다.
/// </summary>
internal sealed class SamplingPipeline
{
    /// <summary>
    /// 이보다 긴 샘플 공백은 절전/최대절전으로 보고 진행 중 이벤트를 닫습니다.
    /// 부하가 심한 PC 에서 스냅샷이 오래 걸려도 공백으로 오인하지 않도록 최소 2분.
    /// </summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(2);

    private readonly DataStore _store;
    private readonly ProcessSnapshotter _snapshotter;
    private readonly MinuteAggregator _aggregator;
    private readonly PressureDetector _cpu;
    private readonly PressureDetector _memory;
    private readonly PressureDetector _disk;
    private readonly Dictionary<PressureKind, DateTimeOffset> _lastSnapshot = [];
    private AgentConfig _config;
    private DateOnly _snapshotDay;
    private int _snapshotsToday;

    public SamplingPipeline(AgentConfig config, DataStore store)
    {
        _config = config;
        _store = store;
        _snapshotter = new ProcessSnapshotter(ProcessMasker.FromConfig(config));
        _aggregator = new MinuteAggregator(SampleThresholds.From(config), config.SampleIntervalSeconds);
        var (cpuSustain, memSustain, diskSustain, recover, maxGap) = Durations(config);
        _cpu = new PressureDetector(PressureKind.Cpu, cpuSustain, recover, maxGap);
        _memory = new PressureDetector(PressureKind.Memory, memSustain, recover, maxGap);
        _disk = new PressureDetector(PressureKind.Disk, diskSustain, recover, maxGap);
    }

    /// <summary>설정 변경 반영. 진행 중인 1분 집계·이벤트·스냅샷 빈도 제한 상태는 그대로 유지합니다.</summary>
    public void Reconfigure(AgentConfig config)
    {
        _config = config;
        _snapshotter.Masker = ProcessMasker.FromConfig(config);
        _aggregator.Reconfigure(SampleThresholds.From(config), config.SampleIntervalSeconds);
        var (cpuSustain, memSustain, diskSustain, recover, maxGap) = Durations(config);
        _cpu.Reconfigure(cpuSustain, recover, maxGap);
        _memory.Reconfigure(memSustain, recover, maxGap);
        _disk.Reconfigure(diskSustain, recover, maxGap);
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
        _store.FlushPending();
        if (_store.PendingFiles > 0) Log.Warn("파일 잠금이 풀리지 않아 일부 기록을 저장하지 못했습니다.");
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
                // 스냅샷 도중 서비스가 멈춰도 시작 기록은 남긴다(종료 기록만 남는 것을 방지)
                record.SnapshotSkipped = true;
                _store.AppendPressure(record);
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

    private static (TimeSpan Cpu, TimeSpan Memory, TimeSpan Disk, TimeSpan Recover, TimeSpan MaxGap) Durations(AgentConfig c)
    {
        var gap = TimeSpan.FromSeconds(c.SampleIntervalSeconds * 3 + 5);
        return (TimeSpan.FromSeconds(c.CpuSustainSeconds),
                TimeSpan.FromSeconds(c.MemorySustainSeconds),
                TimeSpan.FromSeconds(c.DiskSustainSeconds),
                TimeSpan.FromSeconds(c.RecoverSustainSeconds),
                gap > MinGap ? gap : MinGap);
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
