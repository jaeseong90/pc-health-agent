using PcAgent.Interop;

namespace PcAgent.Hosting;

/// <summary>
/// Windows 이벤트 로그(응용 프로그램)에 오류를 남깁니다.
/// 데이터 폴더를 신뢰할 수 없어 파일 로그를 쓸 수 없는 경우 등에 사용합니다.
/// </summary>
internal static unsafe class WindowsEventLog
{
    public static void Error(string message)
    {
        try
        {
            nint source = Native.RegisterEventSource(null, AgentPaths.ServiceName);
            if (source == 0) return;
            try
            {
                fixed (char* text = message)
                {
                    char** strings = stackalloc char*[1];
                    strings[0] = text;
                    Native.ReportEvent(source, Native.EVENTLOG_ERROR_TYPE, 0, 1000, 0, 1, 0, strings, 0);
                }
            }
            finally
            {
                Native.DeregisterEventSource(source);
            }
        }
        catch
        {
            // 이벤트 로그 기록 실패는 무시
        }
    }
}
