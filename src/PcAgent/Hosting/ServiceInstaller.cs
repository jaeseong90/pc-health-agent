using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace PcAgent.Hosting;

/// <summary>
/// 파일럿용 간단 설치/제거. 관리자 권한 cmd 에서 PcAgent.exe install / uninstall.
/// </summary>
internal static class ServiceInstaller
{
    private const string DisplayName = "PC Health Agent";
    private const string Description = "PC 성능 진단 에이전트 (로컬 저장, 외부 전송 없음)";

    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PcAgent");

    public static int Install(AgentPaths paths)
    {
        if (!IsAdmin()) return Fail("관리자 권한으로 실행해 주세요 (cmd 를 '관리자 권한으로 실행').");

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(source)!, "PcAgent.dll")))
            return Fail("개발용 빌드에서는 설치할 수 없습니다. publish.ps1 로 만든 단일 exe 로 설치하세요.");

        var targetExe = Path.Combine(InstallDir, "PcAgent.exe");
        bool exists = ServiceExists();
        if (exists) StopService();

        Directory.CreateDirectory(InstallDir);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, targetExe, overwrite: true);

        // 데이터 폴더: SYSTEM/관리자만 쓰기, 일반 사용자는 읽기(본인 PC 데이터 확인·내보내기 가능)
        Directory.CreateDirectory(paths.DataDir);
        Run("icacls", paths.Root, "/inheritance:r", "/grant:r",
            "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-32-545:(OI)(CI)RX", "/T", "/Q");
        if (!File.Exists(paths.ConfigFile)) File.WriteAllText(paths.ConfigFile, new AgentConfig().ToJson());

        var binPath = $"\"{targetExe}\" --service";
        if (exists)
            Run("sc.exe", "config", AgentPaths.ServiceName, "binPath=", binPath, "start=", "delayed-auto");
        else
            Run("sc.exe", "create", AgentPaths.ServiceName, "binPath=", binPath, "start=", "delayed-auto", "DisplayName=", DisplayName);
        Run("sc.exe", "description", AgentPaths.ServiceName, Description);
        Run("sc.exe", "failure", AgentPaths.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/none/0");
        Run("sc.exe", "start", AgentPaths.ServiceName);

        Console.WriteLine();
        Console.WriteLine($"설치 완료: {targetExe}");
        Console.WriteLine($"데이터 폴더: {paths.Root}");
        Console.WriteLine("서비스는 '지연된 자동 시작'으로 등록되어 부팅 직후 부하를 주지 않습니다.");
        Console.WriteLine("2주 이상 수집 후  PcAgent.exe export  로 결과를 내보내세요.");
        return 0;
    }

    public static int Uninstall(AgentPaths paths, bool purge)
    {
        if (!IsAdmin()) return Fail("관리자 권한으로 실행해 주세요.");

        if (ServiceExists())
        {
            StopService();
            Run("sc.exe", "delete", AgentPaths.ServiceName);
        }

        if (purge && Directory.Exists(paths.Root))
        {
            try
            {
                Directory.Delete(paths.Root, recursive: true);
                Console.WriteLine($"수집 데이터 삭제: {paths.Root}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"데이터 폴더 삭제 실패: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"수집 데이터는 유지됩니다: {paths.Root} (삭제하려면 --purge)");
        }

        var running = Environment.ProcessPath ?? "";
        if (running.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            // 실행 중인 exe 는 지울 수 없으므로 잠시 후 삭제
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        else if (Directory.Exists(InstallDir))
        {
            try { Directory.Delete(InstallDir, recursive: true); } catch { /* 무시 */ }
        }

        Console.WriteLine("제거 완료");
        return 0;
    }

    public static string GetServiceStatus()
    {
        try
        {
            if (!ServiceExists()) return "미설치";
            using var sc = new ServiceController(AgentPaths.ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => "실행 중",
                ServiceControllerStatus.Stopped => "중지됨",
                ServiceControllerStatus.StartPending => "시작 중",
                ServiceControllerStatus.StopPending => "중지 중",
                _ => sc.Status.ToString(),
            };
        }
        catch (Exception ex)
        {
            return $"확인 불가 ({ex.Message})";
        }
    }

    private static bool ServiceExists() =>
        ServiceController.GetServices().Any(s =>
        {
            using (s) return string.Equals(s.ServiceName, AgentPaths.ServiceName, StringComparison.OrdinalIgnoreCase);
        });

    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(AgentPaths.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"서비스 중지 실패: {ex.Message}");
        }
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} 실행 실패");
        p.WaitForExit();
        if (p.ExitCode != 0) Console.Error.WriteLine($"  ({file} {args.FirstOrDefault()} 종료 코드 {p.ExitCode})");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
