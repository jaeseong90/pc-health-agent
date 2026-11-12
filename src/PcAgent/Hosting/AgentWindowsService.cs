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
        _cts = new CancellationTokenSource();
        var runner = new AgentRunner(_paths);
        _task = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                Log.Error("에이전트 비정상 종료", ex);
                throw;
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
