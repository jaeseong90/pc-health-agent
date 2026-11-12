using PcAgent.Collection;
using PcAgent.Storage;

namespace PcAgent.Hosting;

/// <summary>
/// 에이전트 메인 루프. 타이머 1개로 동작하며 네트워크 통신은 전혀 하지 않습니다.
/// 데이터 폴더는 호출 측이 미리 준비합니다(서비스: SecureDirectory, 콘솔: Cli).
/// </summary>
internal sealed class AgentRunner(AgentPaths paths)
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken ct)
    {
        LowFootprint.Apply();

        var config = AgentConfig.Load(paths.ConfigFile, Log.Warn);
        var configStamp = Stamp(paths.ConfigFile);
        var store = new DataStore(paths.DataDir);
        Cleanup(store, config);
        Log.Info($"시작 v{AppInfo.Version} · 측정 주기 {config.SampleIntervalSeconds}초 · 데이터 {paths.DataDir}");

        using var reader = new SystemMetricsReader();
        var pipeline = new SamplingPipeline(config, store);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.SampleIntervalSeconds));
        var nextMaintenance = DateTimeOffset.UtcNow + MaintenanceInterval;
        var nextCleanup = DateTimeOffset.UtcNow + CleanupInterval;
        var errors = new ErrorThrottle();

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await pipeline.ProcessAsync(reader.Read(), ct);

                    var now = DateTimeOffset.UtcNow;
                    if (now < nextMaintenance) continue;
                    nextMaintenance = now + MaintenanceInterval;

                    // 설정 파일이 바뀌면 다시 읽음 (서비스 재시작 불필요, 진행 중 상태 유지)
                    var stamp = Stamp(paths.ConfigFile);
                    if (stamp != configStamp)
                    {
                        configStamp = stamp;
                        config = AgentConfig.Load(paths.ConfigFile, Log.Warn);
                        pipeline.Reconfigure(config);
                        timer.Period = TimeSpan.FromSeconds(config.SampleIntervalSeconds);
                        Log.Info("설정 변경을 적용했습니다.");
                    }

                    if (now >= nextCleanup)
                    {
                        nextCleanup = now + CleanupInterval;
                        Cleanup(store, config);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 한 번의 실패로 수집 전체가 멈추지 않도록 루프 안에서 처리
                    errors.Report(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        finally
        {
            try
            {
                pipeline.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("종료 처리 실패", ex);
            }
            Log.Info("중지");
        }
    }

    private static void Cleanup(DataStore store, AgentConfig config)
    {
        try
        {
            store.Cleanup(config.RetentionDays, config.MaxDataSizeMb * 1024L * 1024L);
        }
        catch (Exception ex)
        {
            Log.Error("오래된 데이터 정리 실패", ex);
        }
    }

    private static DateTime Stamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>같은 오류가 반복되면 10분에 한 번만 기록합니다.</summary>
    private sealed class ErrorThrottle
    {
        private string? _last;
        private DateTimeOffset _lastLogged;
        private int _suppressed;

        public void Report(Exception ex)
        {
            var key = ex.GetType().Name + ex.Message;
            var now = DateTimeOffset.UtcNow;
            if (key == _last && now - _lastLogged < TimeSpan.FromMinutes(10))
            {
                _suppressed++;
                return;
            }
            Log.Error(_suppressed > 0 ? $"샘플링 오류 (직전 오류 {_suppressed}회 생략)" : "샘플링 오류", ex);
            _last = key;
            _lastLogged = now;
            _suppressed = 0;
        }
    }
}
