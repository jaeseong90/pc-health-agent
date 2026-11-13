using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using PcAgent.Collection;
using PcAgent.Interop;

namespace PcAgent.Hosting;

/// <summary>관리자 권한 확인과 UAC 로 자기 자신을 다시 실행하는 기능.</summary>
internal static unsafe class Elevation
{
    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 같은 exe 를 관리자 권한(새 창)으로 실행하고 끝날 때까지 기다립니다.
    /// 종료 코드를 돌려주며, UAC 확인 창에서 취소하면 null.
    /// 네트워크 드라이브·공유 폴더·USB 에서 실행했다면 관리자 세션에서 그 경로가 보이지 않을 수 있어
    /// 로컬 임시 폴더의 사본으로 실행합니다.
    /// </summary>
    public static int? RunElevated(IReadOnlyList<string> args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 알 수 없습니다.");
        string? tempDir = null;
        if (!ProcessSnapshotter.IsLocalFixedDrivePath(exe))
        {
            tempDir = Path.Combine(Path.GetTempPath(), "PcAgent-setup-" + Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            var copy = Path.Combine(tempDir, "PcAgent.exe");
            ServiceInstaller.CopyDataOnly(exe, copy);
            exe = copy;
        }

        try
        {
            // ShellExecuteEx 는 COM 초기화를 요구함(이미 초기화돼 있으면 무시됨)
            Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED | Native.COINIT_DISABLE_OLE1DDE);
            var parameters = JoinArguments(args);
            fixed (char* verb = "runas")
            fixed (char* file = exe)
            fixed (char* p = parameters)
            {
                var info = new Native.SHELLEXECUTEINFOW
                {
                    cbSize = (uint)sizeof(Native.SHELLEXECUTEINFOW),
                    // 사용자가 이미 실행을 허락한 같은 파일이므로 다운로드 보안 경고(영역 검사)를 한 번 더 띄우지 않음
                    fMask = Native.SEE_MASK_NOCLOSEPROCESS | Native.SEE_MASK_NOZONECHECKS,
                    lpVerb = verb,
                    lpFile = file,
                    lpParameters = p,
                    nShow = Native.SW_SHOWNORMAL,
                };
                if (!Native.ShellExecuteEx(ref info))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == Native.ERROR_CANCELLED) return null;
                    throw new Win32Exception(error);
                }
                if (info.hProcess == 0) return 0;
                try
                {
                    Native.WaitForSingleObject(info.hProcess, Native.INFINITE);
                    return Native.GetExitCodeProcess(info.hProcess, out uint code) ? (int)code : 1;
                }
                finally
                {
                    Native.CloseHandle(info.hProcess);
                }
            }
        }
        finally
        {
            if (tempDir is not null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* 임시 폴더라 남아도 무해 */ }
            }
        }
    }

    /// <summary>Windows 명령줄 규칙(CommandLineToArgvW)에 맞게 인자를 따옴표 처리해 합칩니다.</summary>
    internal static string JoinArguments(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(" \t\"") < 0) return arg;
        var sb = new StringBuilder(arg.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // 따옴표 앞의 역슬래시는 두 배로, 따옴표는 \" 로
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
