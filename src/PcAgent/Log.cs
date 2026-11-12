using System.Globalization;

namespace PcAgent;

/// <summary>
/// 최소한의 파일 로거. 1MB 를 넘으면 agent.log.1 로 교체합니다(최대 2MB 사용).
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;
    private static bool _console;

    public static void Init(string? path, bool console)
    {
        _path = path;
        _console = console;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{level}] {message}";
        lock (Gate)
        {
            if (_console)
            {
                Console.WriteLine(line);
            }

            if (_path is null) return;
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }
                // Encoding.UTF8 은 새 파일에만 BOM 을 써서 메모장/PowerShell 에서도 한글이 깨지지 않음
                File.AppendAllText(_path, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch
            {
                // 로깅 실패로 에이전트가 멈추면 안 됨
            }
        }
    }
}
