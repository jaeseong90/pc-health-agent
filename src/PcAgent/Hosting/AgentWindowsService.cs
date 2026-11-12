using System.ServiceProcess;

namespace PcAgent.Hosting;

internal sealed class AgentWindowsService : ServiceBase
{
    private readonly AgentPaths _paths;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public AgentWindowsService(AgentPaths paths)
    {
        _paths = paths;
        ServiceName = AgentPaths.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        // 데이터 폴더를 먼저 검증한 뒤에야 그 안에 로그를 쓴다.
        // 실패하면 예외를 던져 서비스 시작을 실패시키고(sc query 에 표시), 이벤트 로그에 이유를 남긴다.
        try
        {
            SecureDirectory.EnsureForService(_paths);
        }
        catch (Exception ex)
        {
            WindowsEventLog.Error(ex.Message);
            throw;
        }
        Log.Init(_paths.LogFile, console: false);

        _cts = new CancellationTokenSource();
        var runner = new AgentRunner(_paths);
        var token = _cts.Token;
        _task = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // "실행 중"으로 보이면서 수집은 멈춘 상태로 남지 않도록 프로세스를 종료한다.
                // 서비스 관리자가 비정상 종료로 인식해 복구 동작(1분 후 재시작)을 수행함.
                Log.Error("에이전트 비정상 종료 — 프로세스를 종료합니다(서비스 관리자가 재시작)", ex);
                WindowsEventLog.Error($"PcAgent 비정상 종료: {ex.GetType().Name}: {ex.Message}");
                Environment.Exit(1);
            }
        });
    }

    protected override void OnStop() => StopCore();

    protected override void OnShutdown() => StopCore();

    private void StopCore()
    {
        try
        {
            _cts?.Cancel();
            _task?.Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException)
        {
            // 이미 기록됨
        }
    }
}
