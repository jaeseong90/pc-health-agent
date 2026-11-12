using System.ServiceProcess;
using System.Text;
using PcAgent.Export;
using PcAgent.Hosting;
using PcAgent.Inventory;
using PcAgent.Storage;

namespace PcAgent;

internal static class Cli
{
    private const string Usage = """
        PC Health Agent — PC 성능 진단 에이전트 (로컬 저장 전용, 외부 전송 없음)

        사용법:
          PcAgent.exe install                 서비스 설치 및 시작 (관리자 권한)
          PcAgent.exe uninstall [--purge]     서비스 제거 (--purge: 수집 데이터도 삭제)
          PcAgent.exe status                  설치 상태와 수집 현황 확인
          PcAgent.exe export [--days 14] [--out 폴더]
                                              수집 데이터를 진단 리포트(zip)로 내보내기
          PcAgent.exe merge <폴더> [--out 파일.csv]
                                              여러 PC 의 내보내기 zip 을 하나의 요약 CSV 로 합치기
          PcAgent.exe inventory               하드웨어 사양 확인(JSON)
          PcAgent.exe run                     서비스 없이 콘솔에서 수집 실행(테스트용, Ctrl+C 종료)

        공통 옵션:
          --data-dir <폴더>                   데이터 폴더 지정 (기본: %ProgramData%\PcAgent)
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var a = ParsedArgs.Parse(args);
        var paths = AgentPaths.Resolve(a.Get("data-dir"));

        if (a.Has("service"))
        {
            Log.Init(paths.LogFile, console: false);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("처리되지 않은 예외", e.ExceptionObject as Exception);
            ServiceBase.Run(new AgentWindowsService(paths));
            return 0;
        }

        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            switch (a.Command)
            {
                case "run":
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
                case "install":
                    return ServiceInstaller.Install(paths);
                case "uninstall":
                    return ServiceInstaller.Uninstall(paths, a.Has("purge"));
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
        var (file, count) = FleetMerge.Run(dir, a.Get("out"));
        Console.WriteLine($"PC {count}대 요약을 합쳤습니다: {file}");
        return 0;
    }

    private static int Status(AgentPaths paths)
    {
        var store = new DataStore(paths.DataDir);
        var (files, bytes, oldest, newest) = store.GetStats();
        var last = store.ReadLastMetric();
        Console.WriteLine($"PC Health Agent v{AppInfo.Version}");
        Console.WriteLine($"서비스      : {ServiceInstaller.GetServiceStatus()}");
        Console.WriteLine($"데이터 폴더 : {paths.Root}");
        Console.WriteLine(files == 0
            ? "수집 데이터 : 없음"
            : $"수집 데이터 : 파일 {files}개, {bytes / 1024.0:0.#}KB, {oldest:yyyy-MM-dd} ~ {newest:yyyy-MM-dd} (UTC)");
        if (last is not null)
            Console.WriteLine($"마지막 측정 : {last.MinuteUtc.ToLocalTime():yyyy-MM-dd HH:mm} · CPU {last.CpuAvg}% · 가용 메모리 {last.MemAvailAvg}%");
        Console.WriteLine($"설정 파일   : {paths.ConfigFile}{(File.Exists(paths.ConfigFile) ? "" : " (없음 — 기본값 사용)")}");
        return 0;
    }

    private static string DefaultExportDir(AgentPaths paths)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        bool systemProfile = desktop.Contains("systemprofile", StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(desktop) && Directory.Exists(desktop) && !systemProfile
            ? desktop
            : Path.Combine(paths.Root, "exports");
    }

    private sealed class ParsedArgs
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
