namespace PcAgent;

/// <summary>에이전트가 사용하는 경로. 기본값은 %ProgramData%\PcAgent.</summary>
public sealed record AgentPaths(string Root, string DataDir, string ConfigFile, string LogFile)
{
    public const string ServiceName = "PcAgent";

    public static AgentPaths Resolve(string? rootOverride)
    {
        var root = rootOverride
            ?? Environment.GetEnvironmentVariable("PCAGENT_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PcAgent");
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
