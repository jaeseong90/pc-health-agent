using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using Microsoft.Win32;
using PcAgent.Interop;

namespace PcAgent.Hosting;

/// <summary>
/// 설치·업데이트·제거. 관리자 권한이 필요하며, 권한이 없으면 Cli 가 UAC 로 자동 재실행합니다.
/// 항상 기본 경로(%ProgramData%\PcAgent)를 사용합니다.
/// 같은 명령으로 업데이트도 됩니다(새 exe 로 install → 수집 데이터·설정 유지).
/// </summary>
internal static class ServiceInstaller
{
    private const string DisplayName = "PC Health Agent";
    private const string Description = "PC 성능 진단 에이전트 (로컬 저장, 외부 전송 없음)";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PcAgent";

    internal static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PcAgent");
    internal static string InstalledExe => Path.Combine(InstallDir, "PcAgent.exe");
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "PC Health Agent.lnk");

    public static int Install(AgentPaths paths)
    {
        if (!Elevation.IsAdmin()) return Fail("관리자 권한이 필요합니다.");

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(source)!, "PcAgent.dll")))
            return Fail("개발용 빌드에서는 설치할 수 없습니다. publish.ps1 로 만든 단일 exe 로 설치하세요.");

        bool exists = ServiceExists();
        Console.WriteLine(DescribeChange(exists ? InstalledVersion() : null, AppInfo.Version));

        if (exists && !StopService())
            return Fail("기존 서비스를 중지하지 못했습니다. 잠시 후 다시 시도하거나 PC 를 다시 시작한 뒤 설치하세요.");

        Directory.CreateDirectory(InstallDir);
        if (!SamePath(source, InstalledExe)) ReplaceFile(source, InstalledExe);

        // 데이터 폴더: SYSTEM/관리자만 쓰기, 일반 사용자는 읽기(본인 PC 데이터 확인·내보내기 가능).
        // 일반 사용자가 미리 만들어 둔 폴더나 링크는 치우고 원자적으로(생성과 동시에 권한 적용) 다시 만든다.
        SecureDirectory.PrepareForInstall(paths, Console.WriteLine);
        if (!File.Exists(paths.ConfigFile)) File.WriteAllText(paths.ConfigFile, new AgentConfig().ToJson());

        var binPath = $"\"{InstalledExe}\" --service";
        int rc = exists
            ? Run("sc.exe", "config", AgentPaths.ServiceName, "binPath=", binPath, "start=", "delayed-auto")
            : Run("sc.exe", "create", AgentPaths.ServiceName, "binPath=", binPath, "start=", "delayed-auto", "DisplayName=", DisplayName);
        if (rc != 0) return Fail("서비스 등록에 실패했습니다.");
        Run("sc.exe", "description", AgentPaths.ServiceName, Description);
        Run("sc.exe", "failure", AgentPaths.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/none/0");

        // 직원이 명령어 없이 찾을 수 있도록: 설정 > 앱 목록(제거), 시작 메뉴(내보내기·상태·제거)
        TryStep("설치된 앱 목록 등록", RegisterUninstallEntry);
        TryStep("시작 메뉴 바로가기", () => Shortcut.Create(ShortcutPath, InstalledExe, "PC 성능 진단: 결과 내보내기, 상태 확인, 제거"));

        if (Run("sc.exe", "start", AgentPaths.ServiceName) != 0)
            return Fail("서비스를 시작하지 못했습니다. 이벤트 뷰어 > Windows 로그 > 응용 프로그램에서 'PcAgent' 오류를 확인하세요.");

        Console.WriteLine();
        Console.WriteLine($"설치 완료: v{AppInfo.Version} ({InstalledExe})");
        Console.WriteLine($"데이터 폴더: {paths.Root}");
        Console.WriteLine("평소처럼 PC 를 사용하시면 됩니다. 부팅 직후에는 부하를 주지 않도록 잠시 뒤에 시작됩니다.");
        Console.WriteLine("결과 내보내기·상태 확인·제거: 시작 메뉴 > PC Health Agent");
        return 0;
    }

    public static int Uninstall(AgentPaths paths, bool purge)
    {
        if (!Elevation.IsAdmin()) return Fail("관리자 권한이 필요합니다.");

        if (ServiceExists())
        {
            if (!StopService()) Console.Error.WriteLine("서비스가 중지되지 않았지만 제거를 계속합니다.");
            Run("sc.exe", "delete", AgentPaths.ServiceName);
        }
        TryStep("설치된 앱 목록에서 삭제", () => Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));
        TryStep("시작 메뉴 바로가기 삭제", () => File.Delete(ShortcutPath));

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
        else if (!purge)
        {
            Console.WriteLine($"수집 데이터는 유지됩니다: {paths.Root} (삭제하려면 --purge)");
        }

        RemoveInstallDir();
        Console.WriteLine("제거 완료");
        return 0;
    }

    // ---------------- 버전 ----------------

    /// <summary>설치된 exe 의 버전("0.2.0"). 설치돼 있지 않거나 읽을 수 없으면 null.</summary>
    internal static string? InstalledVersion()
    {
        try
        {
            if (!File.Exists(InstalledExe)) return null;
            var v = FileVersionInfo.GetVersionInfo(InstalledExe);
            return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>설치된 버전과 이 exe 버전 비교. 설치된 버전을 모르면 낮은 것으로 봅니다(재설치 권장).</summary>
    internal static int CompareVersions(string? installed, string current)
    {
        if (!Version.TryParse(installed, out var a)) return -1;
        return Version.TryParse(current, out var b) ? a.CompareTo(b) : 1;
    }

    internal static string DescribeChange(string? installed, string current)
    {
        if (installed is null) return $"PC Health Agent v{current} 를 설치합니다.";
        return CompareVersions(installed, current) switch
        {
            < 0 => $"업데이트: v{installed} → v{current} (수집 데이터와 설정은 유지됩니다)",
            0 => $"다시 설치: v{current} (수집 데이터와 설정은 유지됩니다)",
            _ => $"주의: 설치된 v{installed} 보다 낮은 v{current} 로 되돌립니다.",
        };
    }

    // ---------------- 파일 ----------------

    /// <summary>데이터만 복사합니다. 다운로드 표시(Zone.Identifier) 같은 대체 데이터 스트림은 복사하지 않습니다.</summary>
    internal static void CopyDataOnly(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    /// <summary>
    /// 실행 파일 교체. 실행 중인 exe(종료 중인 이전 서비스, 열려 있는 메뉴 창)는 덮어쓸 수 없지만 이름 변경은 되므로
    /// 기존 파일을 .old 로 옮긴 뒤 새 파일을 놓습니다. 실패하면 원래 파일을 되돌립니다.
    /// </summary>
    internal static void ReplaceFile(string source, string target)
    {
        var old = SwapFile(source, target);
        // 이전 exe 가 아직 실행 중이면 지금은 못 지우므로 재부팅 때 삭제
        if (old is not null && File.Exists(old) && !TryDelete(old))
            Native.MoveFileEx(old, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
    }

    /// <summary>새 파일을 제자리에 놓고, 옆으로 옮긴 이전 파일 경로(아직 남아 있을 수 있음)를 돌려줍니다.</summary>
    internal static string? SwapFile(string source, string target)
    {
        var temp = target + ".new";
        string? old = null;
        CopyDataOnly(source, temp);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target))
                    {
                        old = target + ".old";
                        // 지난 업데이트의 .old 가 아직 실행 중이라 못 지우면 다른 이름 사용
                        if (File.Exists(old) && !TryDelete(old)) old = $"{target}.{Path.GetRandomFileName()}.old";
                        File.Move(target, old);
                    }
                    File.Move(temp, target);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(500);
                }
            }
        }
        catch
        {
            if (!File.Exists(target) && old is not null && File.Exists(old)) File.Move(old, target);
            TryDelete(temp);
            throw;
        }
        return old;
    }

    /// <summary>
    /// 설치 폴더 삭제. 이 exe 가 설치 폴더에서 실행 중이면(시작 메뉴·설정 앱에서 제거) 삭제는 안 되지만 이동은 되므로
    /// 임시 폴더로 옮기고 재부팅 때 지우도록 예약합니다(cmd 로 자기 삭제하는 방식은 백신이 의심할 수 있어 쓰지 않음).
    /// </summary>
    private static void RemoveInstallDir()
    {
        if (!Directory.Exists(InstallDir)) return;
        var self = Environment.ProcessPath;
        if (self is not null && IsUnder(self, InstallDir))
        {
            try
            {
                var moved = Path.Combine(Path.GetTempPath(), $"PcAgent-removed-{Path.GetRandomFileName()}.exe");
                File.Move(self, moved);
                Native.MoveFileEx(moved, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  실행 중인 파일을 옮기지 못했습니다: {ex.Message}");
            }
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(InstallDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 10)
                {
                    Console.Error.WriteLine($"설치 폴더를 삭제하지 못했습니다({ex.Message}). 열려 있는 PC Health Agent 창을 닫고 {InstallDir} 를 직접 삭제하세요.");
                    return;
                }
                Thread.Sleep(500);   // 서비스 프로세스가 완전히 끝날 때까지
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // ---------------- 설정 > 앱 목록 ----------------

    private static void RegisterUninstallEntry()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", AppInfo.Version);
        key.SetValue("Comments", Description);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        // 설정 앱의 [제거]: 데이터 삭제 여부를 묻고 진행. 무인 제거(배포 도구)는 데이터 유지
        key.SetValue("UninstallString", $"\"{InstalledExe}\" uninstall --ask");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    // ---------------- 서비스 ----------------

    public static string GetServiceStatus()
    {
        try
        {
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
        catch (InvalidOperationException)
        {
            return "미설치";
        }
        catch (Exception ex)
        {
            return $"확인 불가 ({ex.Message})";
        }
    }

    internal static bool ServiceExists()
    {
        try
        {
            using var sc = new ServiceController(AgentPaths.ServiceName);
            _ = sc.Status; // 서비스가 없으면 InvalidOperationException
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool StopService()
    {
        try
        {
            using var sc = new ServiceController(AgentPaths.ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"서비스 중지 실패: {ex.Message}");
            return false;
        }
    }

    private static int Run(string file, params string[] args)
    {
        // 항상 System32 의 도구를 실행(현재 폴더의 같은 이름 exe 가 실행되지 않도록)
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file)) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} 실행 실패");
        p.WaitForExit();
        if (p.ExitCode != 0) Console.Error.WriteLine($"  ({file} {args.FirstOrDefault()} 종료 코드 {p.ExitCode})");
        return p.ExitCode;
    }

    private static void TryStep(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  {name} 실패(설치는 계속): {ex.Message}");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
