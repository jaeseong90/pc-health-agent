namespace PcAgent;

/// <summary>에이전트가 사용하는 경로. 기본값은 %ProgramData%\PcAgent.</summary>
public sealed record AgentPaths(string Root, string DataDir, string ConfigFile, string LogFile)
{
    public const string ServiceName = "PcAgent";

    /// <summary>CLI 용: --data-dir → PCAGENT_HOME 환경 변수 → 기본 경로 순.</summary>
    public static AgentPaths Resolve(string? rootOverride) =>
        Create(rootOverride ?? Environment.GetEnvironmentVariable("PCAGENT_HOME") ?? DefaultRoot);

    /// <summary>서비스·설치·제거용: 항상 기본 경로(%ProgramData%\PcAgent). 덮어쓰기 옵션을 받지 않음.</summary>
    public static AgentPaths Default() => Create(DefaultRoot);

    public static bool HasOverride(string? rootOverride) =>
        rootOverride is not null || Environment.GetEnvironmentVariable("PCAGENT_HOME") is not null;

    private static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcAgent");

    private static AgentPaths Create(string root)
    {
        root = Path.GetFullPath(root);
        return new AgentPaths(
            root,
            Path.Combine(root, "data"),
            Path.Combine(root, "config.json"),
            Path.Combine(root, "agent.log"));
    }
}

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
