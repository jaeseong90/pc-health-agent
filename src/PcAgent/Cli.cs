using System.ServiceProcess;
using System.Text;
using PcAgent.Analysis;
using PcAgent.Export;
using PcAgent.Hosting;
using PcAgent.Inventory;
using PcAgent.Storage;

namespace PcAgent;

internal static class Cli
{
    private const int ErrorInstallUserExit = 1602;

    private const string Usage = """
        PC Health Agent — PC 성능 진단 에이전트 (로컬 저장 전용, 외부 전송 없음)

        사용법:
          PcAgent.exe                         (더블클릭) 메뉴: 설치·업데이트·결과 내보내기·제거
          PcAgent.exe install                 설치 또는 업데이트 (관리자 권한 — 없으면 확인 창이 뜸)
          PcAgent.exe uninstall [--purge]     제거 (--purge: 수집 데이터도 삭제)
          PcAgent.exe status                  설치 상태와 수집 현황 확인
          PcAgent.exe export [--days 30] [--out 폴더]
                                              수집 데이터를 진단 리포트(zip)로 내보내기
          PcAgent.exe merge <폴더> [--out 파일.csv]
                                              여러 PC 의 내보내기 zip 을 요약 CSV·HTML 로 합치기
          PcAgent.exe inventory               하드웨어 사양 확인(JSON)
          PcAgent.exe version                 버전 출력
          PcAgent.exe menu                    메뉴 열기 (cmd 에서 실행할 때)
          PcAgent.exe run --data-dir <폴더>   서비스 없이 콘솔에서 수집 실행(테스트용, Ctrl+C 종료)

        옵션:
          --data-dir <폴더>                   읽을 데이터 폴더 지정 (기본: %ProgramData%\PcAgent)
                                              install/uninstall 에는 사용할 수 없음
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var a = ParsedArgs.Parse(args);
        var dataDirOverride = a.Get("data-dir");
        var paths = AgentPaths.Resolve(dataDirOverride);

        if (a.Has("service"))
        {
            // 서비스는 항상 기본 경로. 폴더 검증 전에는 파일 로그를 쓰지 않음(AgentWindowsService.OnStart 에서 Init)
            Log.Init(null, console: false);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log.Error("처리되지 않은 예외", e.ExceptionObject as Exception);
                WindowsEventLog.Error($"PcAgent 처리되지 않은 예외: {e.ExceptionObject}");
            };
            ServiceBase.Run(new AgentWindowsService(AgentPaths.Default()));
            return 0;
        }

        if (a.Command is "install" or "uninstall" && dataDirOverride is not null)
        {
            Console.Error.WriteLine("install/uninstall 에는 --data-dir 을 쓸 수 없습니다. 서비스는 항상 %ProgramData%\\PcAgent 를 사용합니다.");
            return 1;
        }

        Console.OutputEncoding = Encoding.UTF8;

        // 탐색기에서 더블클릭(또는 시작 메뉴)하면 이 프로세스 전용 콘솔 창이 열림 → 직원용 메뉴
        if (args.Length == 0 && ConsoleHost.OwnsConsole())
        {
            try
            {
                return InteractiveMenu.Run();
            }
            catch (Exception ex)
            {
                // 더블클릭 창은 오류가 나면 바로 닫히므로 읽을 수 있게 멈춤
                Console.Error.WriteLine($"오류: {ex.Message}");
                ConsoleHost.Pause();
                return 1;
            }
        }

        try
        {
            switch (a.Command)
            {
                case "run":
                    if (!AgentPaths.HasOverride(dataDirOverride))
                    {
                        // 기본 경로를 일반 사용자 권한으로 만들면 서비스가 신뢰할 수 없는 폴더가 되므로 테스트 폴더를 명시하게 함
                        Console.Error.WriteLine("run 은 테스트용입니다. --data-dir <폴더> 로 데이터 폴더를 지정하세요. (서비스 데이터 폴더와 분리)");
                        return 1;
                    }
                    return await RunConsoleAsync(paths);
                case "export":
                    return Export(paths, a);
                case "merge":
                    return Merge(a);
                case "status":
                    return Status(paths);
                case "inventory":
                    var warnings = new List<string>();
                    var inv = InventoryCollector.Collect(warnings);
                    Console.WriteLine(Json.Serialize(inv, AgentJsonContext.Default.InventoryInfo, indented: true));
                    foreach (var w in warnings) Console.Error.WriteLine($"경고: {w}");
                    return 0;
                case "version":
                    Console.WriteLine(AppInfo.Version);
                    return 0;
                case "menu":
                    return InteractiveMenu.Run();
                case "install":
                case "uninstall":
                    if (AdminCommand(args) is { } rc) return rc;
                    Console.Error.WriteLine("관리자 권한 확인이 취소되어 진행하지 않았습니다.");
                    return 1;
                default:
                    Console.WriteLine(Usage);
                    return a.Command is null or "help" ? 0 : 1;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"권한이 없습니다: {ex.Message}");
            Console.Error.WriteLine("관리자 권한으로 실행하거나 --data-dir 로 쓰기 가능한 폴더를 지정하세요.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"오류: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// install/uninstall 실행. 관리자 권한이 없으면 UAC 로 자신을 다시 실행합니다(새 창, 끝나면 키 입력 대기).
    /// UAC 확인 창에서 취소하면 null. --pause 면 결과를 읽을 수 있도록 끝에서 기다립니다(오류가 나도).
    /// </summary>
    internal static int? AdminCommand(string[] args)
    {
        var a = ParsedArgs.Parse(args);
        if (!Elevation.IsAdmin())
        {
            Console.WriteLine("관리자 권한이 필요합니다. 권한 확인 창에서 [예]를 누르면 새 창에서 진행됩니다.");
            return Elevation.RunElevated(a.Has("pause") ? args : [.. args, "--pause"]);
        }

        int result;
        try
        {
            var paths = AgentPaths.Default();
            if (a.Command == "install")
            {
                result = ServiceInstaller.Install(paths);
            }
            else
            {
                bool? purge = a.Has("purge") ? true : a.Has("ask") ? AskPurge(paths) : false;
                if (purge is null)
                {
                    Console.WriteLine("제거를 취소했습니다.");
                    result = ErrorInstallUserExit;
                }
                else
                {
                    result = ServiceInstaller.Uninstall(paths, purge.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"오류: {ex.Message}");
            result = 1;
        }

        if (a.Has("pause")) ConsoleHost.Pause();
        return result;
    }

    /// <summary>제거 시 데이터 처리 확인(설정 앱·메뉴에서 제거할 때). 취소하면 null.</summary>
    private static bool? AskPurge(AgentPaths paths)
    {
        Console.WriteLine("PC Health Agent 를 제거합니다.");
        Console.WriteLine($"이 PC 에 모인 수집 데이터({paths.Root})는 어떻게 할까요?");
        Console.WriteLine("  1. 함께 삭제");
        Console.WriteLine("  2. 남겨두기 (다시 설치하면 이어서 사용)");
        Console.WriteLine("  0. 제거 취소");
        return ConsoleHost.Choose("번호를 누르세요: ", "120") switch
        {
            '1' => true,
            '2' => false,
            _ => null,
        };
    }

    private static async Task<int> RunConsoleAsync(AgentPaths paths)
    {
        Directory.CreateDirectory(paths.DataDir);
        Log.Init(paths.LogFile, console: true);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.WriteLine("콘솔 모드로 수집합니다. 종료하려면 Ctrl+C");
        await new AgentRunner(paths).RunAsync(cts.Token);
        return 0;
    }

    private static int Export(AgentPaths paths, ParsedArgs a)
    {
        int days = Math.Clamp(a.GetInt("days", 30), 1, 365);
        var outDir = a.Get("out") ?? DefaultExportDir(paths);
        var config = AgentConfig.Load(paths.ConfigFile, msg => Console.Error.WriteLine($"경고: {msg}"));
        var file = Exporter.Run(paths, config, days, outDir, msg => Console.WriteLine(msg));
        Console.WriteLine();
        Console.WriteLine($"내보내기 완료: {file}");
        Console.WriteLine("zip 안의 report.html 을 브라우저로 열면 진단 결과를 볼 수 있습니다.");
        return 0;
    }

    private static int Merge(ParsedArgs a)
    {
        var dir = a.Positional.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        var r = FleetMerge.Run(dir, a.Get("out"));
        Console.WriteLine($"PC {r.Count}대 결과를 합쳤습니다.");
        Console.WriteLine($"  요약 CSV : {r.CsvPath}");
        Console.WriteLine($"  리포트   : {r.HtmlPath}");
        if (r.Skipped.Count > 0) Console.WriteLine($"  읽지 못한 파일 {r.Skipped.Count}개: {string.Join(", ", r.Skipped)}");
        return 0;
    }

    internal static int Status(AgentPaths paths)
    {
        var store = new DataStore(paths.DataDir);
        var (files, bytes, oldest, newest) = store.GetStats();
        var last = store.ReadLastMetric();
        Console.WriteLine($"PC Health Agent v{AppInfo.Version}");
        Console.WriteLine($"서비스      : {ServiceInstaller.GetServiceStatus()}" +
                          (ServiceInstaller.InstalledVersion() is { } iv ? $" (설치 버전 v{iv})" : ""));
        Console.WriteLine($"데이터 폴더 : {paths.Root}");
        if (Directory.Exists(paths.Root))
        {
            bool secure = SecureDirectory.IsTrusted(paths.Root, out var reason)
                          && (!Directory.Exists(paths.DataDir) || SecureDirectory.IsTrusted(paths.DataDir, out reason));
            Console.WriteLine(secure ? "폴더 보안   : 정상" : $"폴더 보안   : 주의 — {reason} (서비스용 폴더라면 관리자 권한으로 install 재실행)");
        }
        Console.WriteLine(files == 0
            ? "수집 데이터 : 없음"
            : $"수집 데이터 : 파일 {files}개, {bytes / 1024.0:0.#}KB, {oldest:yyyy-MM-dd} ~ {newest:yyyy-MM-dd} (UTC)");
        if (files > 0)
            Console.WriteLine($"수집 진행   : {Progress(paths, store).Describe()}");
        if (last is not null)
            Console.WriteLine($"마지막 측정 : {last.MinuteUtc.ToLocalTime():yyyy-MM-dd HH:mm} · CPU {last.CpuAvg}% · 가용 메모리 {last.MemAvailAvg}%");

        var usage = AgentUsage.Read(paths.Root);
        var liveMb = AgentUsage.ServicePrivateMb();
        if (usage is not null || liveMb is not null)
        {
            var parts = new List<string> { $"메모리 {(liveMb ?? usage!.PrivateMb):0.#}MB" };
            if (usage?.CpuAvgPercent is { } cpu)
                parts.Add($"CPU 평균 {cpu:0.###}% (가동 {(usage.UpdatedUtc - usage.StartedUtc).TotalHours:0.#}시간 기준)");
            Console.WriteLine($"에이전트    : {string.Join(" · ", parts)}");
        }
        Console.WriteLine($"설정 파일   : {paths.ConfigFile}{(File.Exists(paths.ConfigFile) ? "" : " (없음 — 기본값 사용)")}");
        return 0;
    }

    /// <summary>최근 30일(기본 내보내기 기간) 수집 진행 상황.</summary>
    internal static CollectionProgress Progress(AgentPaths paths, DataStore store)
    {
        var config = AgentConfig.Load(paths.ConfigFile, _ => { });
        var now = DateTimeOffset.UtcNow;
        return CollectionProgress.Compute(store.ReadMetrics(now.AddDays(-30), now), config, TimeZoneInfo.Local);
    }

    internal static string DefaultExportDir(AgentPaths paths)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        bool systemProfile = desktop.Contains("systemprofile", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(desktop) && Directory.Exists(desktop) && !systemProfile
            ? desktop
            : Path.Combine(paths.Root, "exports");
    }

    internal sealed class ParsedArgs
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
        public string? Command { get; private set; }
        public List<string> Positional { get; } = [];

        public static ParsedArgs Parse(string[] args)
        {
            var parsed = new ParsedArgs();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    var key = arg[2..];
                    string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                    parsed._options[key] = value;
                }
                else if (parsed.Command is null)
                {
                    parsed.Command = arg.ToLowerInvariant();
                }
                else
                {
                    parsed.Positional.Add(arg);
                }
            }
            return parsed;
        }

        public bool Has(string key) => _options.ContainsKey(key);
        public string? Get(string key) => _options.TryGetValue(key, out var v) ? v : null;
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
    }
}
