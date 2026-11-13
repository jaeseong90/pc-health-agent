using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using PcAgent.Analysis;
using PcAgent.Export;
using PcAgent.Hosting;
using PcAgent.Inventory;

namespace PcAgent.Tests;

/// <summary>직원 배포(메뉴·업데이트·바로가기)와 취합 기능 테스트.</summary>
public sealed class DistributionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcagent-dist-" + Guid.NewGuid().ToString("N"));

    public DistributionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 무시 */ }
    }

    // ---------------- 버전 / 메뉴 ----------------

    [Theory]
    [InlineData(null, "0.3.0", -1)]
    [InlineData("0.2.0", "0.3.0", -1)]
    [InlineData("0.3.0", "0.3.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]   // 문자열이 아닌 숫자 비교
    [InlineData("garbage", "0.3.0", -1)]
    public void Versions_compare_numerically(string? installed, string current, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(ServiceInstaller.CompareVersions(installed, current)));

    [Fact]
    public void Install_message_describes_update_and_downgrade()
    {
        Assert.Contains("설치합니다", ServiceInstaller.DescribeChange(null, "0.3.0"));
        Assert.Contains("v0.2.0 → v0.3.0", ServiceInstaller.DescribeChange("0.2.0", "0.3.0"));
        Assert.Contains("다시 설치", ServiceInstaller.DescribeChange("0.3.0", "0.3.0"));
        Assert.Contains("되돌립니다", ServiceInstaller.DescribeChange("0.4.0", "0.3.0"));
    }

    [Fact]
    public void Menu_offers_install_only_when_not_installed()
    {
        var menu = InteractiveMenu.BuildMenu(installed: false, versionCompare: -1);
        Assert.Equal([InteractiveMenu.MenuAction.Install, InteractiveMenu.MenuAction.Close], menu.Select(m => m.Action));
        Assert.Equal(['1', '0'], menu.Select(m => m.Key));
    }

    [Fact]
    public void Menu_offers_update_first_when_installed_version_is_older()
    {
        var menu = InteractiveMenu.BuildMenu(installed: true, versionCompare: -1);
        Assert.Equal(InteractiveMenu.MenuAction.Update, menu[0].Action);
        Assert.Equal("12340", new string(menu.Select(m => m.Key).ToArray()));
    }

    [Fact]
    public void Menu_has_no_update_when_up_to_date()
    {
        var menu = InteractiveMenu.BuildMenu(installed: true, versionCompare: 0);
        Assert.DoesNotContain(menu, m => m.Action == InteractiveMenu.MenuAction.Update);
        Assert.Equal(InteractiveMenu.MenuAction.Export, menu[0].Action);
    }

    [Theory]
    [InlineData(new[] { "install", "--pause" }, "install --pause")]
    [InlineData(new[] { "merge", @"C:\My Files\zips" }, "merge \"C:\\My Files\\zips\"")]
    [InlineData(new[] { "x", @"C:\dir with space\" }, "x \"C:\\dir with space\\\\\"")]
    [InlineData(new[] { "say \"hi\"" }, "\"say \\\"hi\\\"\"")]
    [InlineData(new[] { "" }, "\"\"")]
    public void Arguments_are_quoted_for_relaunch(string[] args, string expected) =>
        Assert.Equal(expected, Elevation.JoinArguments(args));

    // ---------------- 실행 파일 교체 ----------------

    [Fact]
    public void Swap_replaces_a_running_executable_and_keeps_it_running()
    {
        var target = Path.Combine(_dir, "running.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), target);
        var source = Path.Combine(_dir, "new.bin");
        File.WriteAllText(source, "new version");

        using var p = Process.Start(new ProcessStartInfo(target, "/d /c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var writeError = Record.Exception(() => File.WriteAllText(target, "x"));   // 실행 중이라 덮어쓰기 불가
            Assert.True(writeError is IOException or UnauthorizedAccessException, writeError?.GetType().Name);

            var old = ServiceInstaller.SwapFile(source, target);

            Assert.Equal("new version", File.ReadAllText(target));
            Assert.NotNull(old);
            Assert.True(File.Exists(old));          // 실행 중인 이전 파일은 옆으로 옮겨짐
            Assert.False(p.HasExited);              // 실행 중인 프로세스는 영향 없음
            Assert.False(File.Exists(target + ".new"));
        }
        finally
        {
            p.Kill();
            p.WaitForExit();
        }
    }

    [Fact]
    public void Swap_installs_fresh_file_without_old_copy()
    {
        var target = Path.Combine(_dir, "fresh.exe");
        var source = Path.Combine(_dir, "src.bin");
        File.WriteAllText(source, "v1");

        Assert.Null(ServiceInstaller.SwapFile(source, target));
        Assert.Equal("v1", File.ReadAllText(target));
    }

    [Fact]
    public void Copy_strips_download_mark()
    {
        var source = Path.Combine(_dir, "downloaded.exe");
        File.WriteAllText(source, "exe");
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var target = Path.Combine(_dir, "installed.exe");

        ServiceInstaller.CopyDataOnly(source, target);

        Assert.Equal("exe", File.ReadAllText(target));
        Assert.False(File.Exists(target + ":Zone.Identifier"));
    }

    [Fact]
    public void Shortcut_is_created_with_target()
    {
        var link = Path.Combine(_dir, "menu", "Test.lnk");
        var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        Shortcut.Create(link, target, "테스트 바로가기");

        var bytes = File.ReadAllBytes(link);
        Assert.Equal(0x4C, bytes[0]);   // ShellLinkHeader 크기(0x4C)로 시작
        bool hasTarget = Contains(bytes, Encoding.Unicode.GetBytes("notepad.exe")) || Contains(bytes, Encoding.ASCII.GetBytes("notepad.exe"));
        Assert.True(hasTarget);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    // ---------------- 에이전트 자체 사용량 ----------------

    [Fact]
    public void Agent_usage_round_trips_and_computes_average_cpu()
    {
        AgentUsage.Write(_dir);
        var read = AgentUsage.Read(_dir);
        Assert.NotNull(read);
        Assert.Equal(AppInfo.Version, read.Version);
        Assert.True(read.PrivateMb > 0);

        var usage = new AgentUsageInfo
        {
            StartedUtc = TestData.Monday,
            UpdatedUtc = TestData.Monday.AddHours(10),
            CpuSeconds = 36,                 // 10시간에 36초
            LogicalProcessors = 8,
        };
        Assert.Equal(0.0125, usage.CpuAvgPercent);   // 36 / (36000 × 8) × 100
        Assert.Null(new AgentUsageInfo { StartedUtc = TestData.Monday, UpdatedUtc = TestData.Monday.AddMinutes(5), CpuSeconds = 1, LogicalProcessors = 8 }.CpuAvgPercent);
    }

    // ---------------- 배터리 ----------------

    [Fact]
    public void Battery_health_uses_capacity_sums_and_ignores_invalid_values()
    {
        var inv = new InventoryInfo();
        Assert.Null(inv.BatteryHealthPercent);   // 데스크톱

        inv.Batteries.Add(new BatteryInfo { DesignedCapacity = 50000, FullChargedCapacity = 40000 });
        inv.Batteries.Add(new BatteryInfo { DesignedCapacity = 30000, FullChargedCapacity = 12000 });
        inv.Batteries.Add(new BatteryInfo { DesignedCapacity = 0, FullChargedCapacity = 5000 });          // 설계 용량 없음
        inv.Batteries.Add(new BatteryInfo { DesignedCapacity = 1000, FullChargedCapacity = 90000 });      // 비정상
        Assert.Equal(65, inv.BatteryHealthPercent);   // (40000+12000)/(50000+30000)
    }

    [Theory]
    [InlineData(35, "High")]
    [InlineData(55, "Medium")]
    [InlineData(75, null)]
    [InlineData(95, null)]
    public void Worn_battery_recommends_replacement(double health, string? expectedPriority)
    {
        var inv = TestData.Inventory();
        inv.Batteries.Add(new BatteryInfo { DesignedCapacity = 100000, FullChargedCapacity = (long)(health * 1000), CycleCount = 600 });
        var r = UpgradeAdvisor.Analyze(new AnalysisInput
        {
            Inventory = inv, Metrics = TestData.WorkdayMetrics(5), Events = [], Logs = [], Config = new AgentConfig(), TimeZone = TimeZoneInfo.Utc,
        });

        var rec = r.Recommendations.SingleOrDefault(x => x.Code == "BATTERY_REPLACE");
        Assert.Equal(expectedPriority, rec?.Priority);
        if (rec is not null) Assert.Contains("충전 횟수 600회", rec.Detail);
        if (health is > 60 and <= 80) Assert.Contains(r.Findings, f => f.Contains("배터리 수명"));
    }

    // ---------------- 취합 ----------------

    private void WriteZip(string name, ExportSummary s, bool withJson = true)
    {
        using var zip = ZipFile.Open(Path.Combine(_dir, name), ZipArchiveMode.Create);
        if (withJson)
        {
            using var w = new StreamWriter(zip.CreateEntry("summary.json").Open(), new UTF8Encoding(false));
            w.Write(Json.Serialize(s, AgentJsonContext.Default.ExportSummary, indented: true));
        }
    }

    private static ExportSummary Summary(string host, int score, string verdict, DateTimeOffset generated, params string[] codes) => new()
    {
        AgentVersion = "0.3.0",
        GeneratedUtc = generated,
        TimeZone = "UTC",
        Inventory = new InventoryInfo { Hostname = host, CpuName = "CPU", RamInstalledMb = 8192 },
        Analysis = new AnalysisResult
        {
            Score = score,
            Verdict = verdict,
            VerdictLabel = verdict,
            Recommendations = codes.Select(c => new Recommendation { Code = c, Priority = "High", Title = c }).ToList(),
        },
    };

    [Fact]
    public void Merge_keeps_latest_per_pc_sorts_by_score_and_writes_html()
    {
        WriteZip("PcHealth_A_1.zip", Summary("PC-A", 10, "Watch", TestData.Monday));
        WriteZip("PcHealth_A_2.zip", Summary("PC-A", 55, "Upgrade", TestData.Monday.AddDays(7), "RAM_UPGRADE"));
        WriteZip("PcHealth_B_1.zip", Summary("PC-<b>B</b>", 20, "Watch", TestData.Monday, "RAM_UPGRADE", "SSD_REPLACE"));
        WriteZip("PcHealth_C_1.zip", Summary("PC-C", 0, "Ok", TestData.Monday), withJson: false);   // summary.json 없음
        File.WriteAllText(Path.Combine(_dir, "PcHealth_D_1.zip"), "not a zip");

        var r = FleetMerge.Run(_dir, null);

        Assert.Equal(2, r.Count);
        Assert.Equal(2, r.Skipped.Count);
        var lines = File.ReadAllLines(r.CsvPath, Encoding.UTF8);
        Assert.Equal(3, lines.Length);   // 헤더 + 2대
        Assert.StartsWith("PC명,", lines[0].TrimStart('\uFEFF'));
        Assert.StartsWith("PC-A,", lines[1]);   // 최신(점수 55) 이 먼저
        Assert.Contains(",55,", lines[1]);
        Assert.Equal(Exporter.SummaryHeader.Length, Csv.ParseLine(lines[1]).Count);

        var html = File.ReadAllText(r.HtmlPath);
        Assert.Contains("PC-&lt;b&gt;B&lt;/b&gt;", html);   // 받은 값은 HTML 인코딩
        Assert.DoesNotContain("<b>B</b>", html);
        Assert.Contains("메모리 증설", html);
    }

    [Fact]
    public void Merge_reads_summary_from_older_versions_without_new_fields()
    {
        // 0.2.0 형식(배터리·에이전트 사용량 없음)
        const string oldJson = """
            {"agentVersion":"0.2.0","generatedUtc":"2026-11-10T00:00:00+00:00","timeZone":"Korea Standard Time",
             "inventory":{"hostname":"OLD-PC","ramInstalledMb":4096,"ramSlotsLabel":"1/2","ramSoldered":false},
             "analysis":{"verdict":"Upgrade","verdictLabel":"업그레이드 권장","score":40,
               "recommendations":[{"code":"RAM_UPGRADE","priority":"High","title":"메모리 증설","detail":"..."}]}}
            """;
        using (var zip = ZipFile.Open(Path.Combine(_dir, "PcHealth_OLD_1.zip"), ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("summary.json").Open()))
            w.Write(oldJson);

        var r = FleetMerge.Run(_dir, Path.Combine(_dir, "out.csv"));

        Assert.Equal(1, r.Count);
        Assert.Equal(Path.Combine(_dir, "out.html"), r.HtmlPath);
        var row = Csv.ParseLine(File.ReadAllLines(r.CsvPath)[1]);
        Assert.Equal("OLD-PC", row[0]);
        Assert.Equal(Exporter.SummaryHeader.Length, row.Count);
    }
}
