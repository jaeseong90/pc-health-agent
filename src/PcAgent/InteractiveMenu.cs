using System.Diagnostics;
using PcAgent.Export;
using PcAgent.Hosting;
using PcAgent.Storage;

namespace PcAgent;

/// <summary>
/// 인자 없이 실행(탐색기 더블클릭·시작 메뉴 바로가기)했을 때의 메뉴.
/// 직원이 명령어 없이 설치·업데이트·결과 내보내기·제거를 할 수 있게 합니다. 숫자 키만 사용(한/영 상태 무관).
/// </summary>
internal static class InteractiveMenu
{
    internal enum MenuAction { Install, Update, Export, Status, Uninstall, Close }

    internal sealed record MenuItem(char Key, string Label, MenuAction Action);

    /// <param name="installed">서비스 설치 여부</param>
    /// <param name="versionCompare">설치된 버전 ↔ 이 파일 버전 비교(음수: 설치된 쪽이 낮음)</param>
    internal static List<MenuItem> BuildMenu(bool installed, int versionCompare)
    {
        var entries = new List<(string Label, MenuAction Action)>();
        if (!installed)
        {
            entries.Add(("설치하기", MenuAction.Install));
        }
        else
        {
            if (versionCompare < 0) entries.Add(("업데이트 (수집 데이터와 설정은 그대로 유지)", MenuAction.Update));
            entries.Add(("결과 내보내기 (바탕화면에 zip 파일 생성)", MenuAction.Export));
            entries.Add(("상태 보기", MenuAction.Status));
            entries.Add(("제거", MenuAction.Uninstall));
        }
        var menu = entries.Select((e, i) => new MenuItem((char)('1' + i), e.Label, e.Action)).ToList();
        menu.Add(new MenuItem('0', "닫기", MenuAction.Close));
        return menu;
    }

    public static int Run()
    {
        ConsoleHost.TrySetTitle($"PC Health Agent v{AppInfo.Version}");
        var paths = AgentPaths.Default();
        while (true)
        {
            bool installed = ServiceInstaller.ServiceExists();
            string? installedVersion = installed ? ServiceInstaller.InstalledVersion() : null;
            int compare = ServiceInstaller.CompareVersions(installedVersion, AppInfo.Version);

            PrintHeader(installed, installedVersion, compare);
            var menu = BuildMenu(installed, compare);
            Console.WriteLine();
            foreach (var item in menu) Console.WriteLine($"  {item.Key}. {item.Label}");
            char key = ConsoleHost.Choose("\n번호를 누르세요: ", new string(menu.Select(m => m.Key).ToArray()));
            var action = menu.First(m => m.Key == key).Action;
            Console.WriteLine();

            try
            {
                switch (action)
                {
                    case MenuAction.Close:
                        return 0;
                    case MenuAction.Install:
                    case MenuAction.Update:
                        // 관리자 창에서 끝까지 진행(결과 확인 후 닫힘)하면 이 창도 닫음. 권한 확인을 취소하면 메뉴로
                        if (Cli.AdminCommand(["install", "--pause"]) is { } installResult) return installResult;
                        Console.WriteLine("관리자 권한 확인이 취소되었습니다.");
                        break;
                    case MenuAction.Uninstall:
                        if (Cli.AdminCommand(["uninstall", "--ask", "--pause"]) is { } uninstallResult) return uninstallResult;
                        Console.WriteLine("관리자 권한 확인이 취소되었습니다.");
                        break;
                    case MenuAction.Export:
                        Export(paths);
                        break;
                    case MenuAction.Status:
                        Cli.Status(paths);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"오류: {ex.Message}");
            }
            Console.WriteLine();
        }
    }

    private static void PrintHeader(bool installed, string? installedVersion, int compare)
    {
        Console.WriteLine("────────────────────────────────────────────────────");
        Console.WriteLine($" PC Health Agent v{AppInfo.Version} — PC 성능 진단 에이전트");
        Console.WriteLine("────────────────────────────────────────────────────");
        if (!installed)
        {
            Console.WriteLine(" 상태: 설치되어 있지 않음");
            Console.WriteLine();
            Console.WriteLine(ConsentText);
            return;
        }

        string version = installedVersion is null ? "버전 확인 불가"
            : compare < 0 ? $"설치 v{installedVersion} → 이 파일 v{AppInfo.Version} 로 업데이트 가능"
            : compare > 0 ? $"설치 v{installedVersion} (이 파일보다 최신)"
            : $"v{installedVersion}";
        Console.WriteLine($" 상태: {ServiceInstaller.GetServiceStatus()} · {version}");
        try
        {
            var paths = AgentPaths.Default();
            var progress = Cli.Progress(paths, new DataStore(paths.DataDir));
            if (progress.Days > 0) Console.WriteLine($" {progress.Describe()}");
        }
        catch
        {
            // 진행 상황은 참고용
        }
    }

    internal const string ConsentText = """
         PC 업그레이드·교체 대상을 정하기 위해 이 PC 의 성능을 기록합니다.

         기록하는 것
           · CPU·메모리·디스크 사용률 (5초마다 측정해 1분 단위로 저장)
           · 자원이 부족했던 순간에 실행 중이던 프로그램 이름과 사용량
           · PC 사양(CPU, 메모리, 디스크, Windows 버전), 블루스크린·앱 오류 기록
         기록하지 않는 것
           · 창 제목, 문서·파일 이름, 웹 주소, 키 입력, 화면

         데이터는 이 PC(C:\ProgramData\PcAgent)에만 저장되며 외부로 전송되지 않습니다.
         결과는 직접 '결과 내보내기'로 만든 파일로만 전달됩니다.
         에이전트 자체는 메모리 약 20MB만 쓰고 CPU 는 거의 쓰지 않습니다.
         제거: 설정 > 앱, 또는 시작 메뉴 > PC Health Agent
        """;

    private static void Export(AgentPaths paths)
    {
        Console.WriteLine("결과 파일을 만드는 중입니다(최근 30일)...");
        var config = AgentConfig.Load(paths.ConfigFile, _ => { });
        var file = Exporter.Run(paths, config, 30, Cli.DefaultExportDir(paths), msg => Console.WriteLine("  " + msg));
        Console.WriteLine();
        Console.WriteLine($"완료: {file}");
        Console.WriteLine("이 zip 파일을 파일럿 담당자에게 보내 주세요. 압축을 풀고 report.html 을 열면 결과를 직접 볼 수 있습니다.");
        if (!Console.IsInputRedirected) SelectInExplorer(file);
    }

    private static void SelectInExplorer(string file)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, $"/select,\"{file}\"") { UseShellExecute = false })?.Dispose();
        }
        catch
        {
            // 탐색기를 못 열어도 경로는 이미 안내함
        }
    }
}
