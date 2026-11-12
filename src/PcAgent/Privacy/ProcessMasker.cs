using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PcAgent.Privacy;

/// <summary>
/// 개인정보 보호 처리.
/// - 설정된 패턴에 맞는 프로세스명은 [masked-xxxxxx] 로 치환 (경로·게시자도 제거)
/// - 경로의 사용자 계정 폴더(C:\Users\홍길동\)는 항상 %USERPROFILE%\ 로 치환
/// 마스킹 토큰은 PC 별 salt 로 해시하므로 같은 PC 안에서만 구분 가능하고 원래 이름은 드러나지 않습니다.
/// </summary>
public sealed partial class ProcessMasker
{
    private readonly string[] _patterns;
    private readonly bool _includePaths;
    private readonly byte[] _salt;

    public ProcessMasker(IEnumerable<string> patterns, bool includePaths, string salt)
    {
        _patterns = patterns.Select(StripExe).Where(p => p.Length > 0).ToArray();
        _includePaths = includePaths;
        _salt = Encoding.UTF8.GetBytes(salt);
    }

    public static ProcessMasker FromConfig(AgentConfig config) =>
        new(config.MaskProcessPatterns, config.IncludeProcessPaths, ReadMachineSalt());

    public bool IsMasked(string name)
    {
        var target = StripExe(name);
        foreach (var p in _patterns)
        {
            if (GlobMatch(p, target)) return true;
        }
        return false;
    }

    /// <summary>표시용 이름/경로를 반환합니다.</summary>
    public (string Name, string? Path, bool Masked) Apply(string name, string? path)
    {
        if (IsMasked(name) || (path is not null && IsMasked(System.IO.Path.GetFileName(path))))
        {
            return (Token(name), null, true);
        }
        return (name, _includePaths ? SanitizePath(path) : null, false);
    }

    /// <summary>이벤트 로그의 앱 이름 등 단일 이름에 적용합니다.</summary>
    public string MaskName(string name) => IsMasked(name) ? Token(name) : name;

    public static string? SanitizePath(string? path) =>
        path is null ? null : UserProfileRegex().Replace(path, @"%USERPROFILE%\");

    public static bool IsMaskedToken(string name) => name.StartsWith("[masked-", StringComparison.Ordinal);

    private string Token(string name)
    {
        if (IsMaskedToken(name)) return name;
        var input = Encoding.UTF8.GetBytes(StripExe(name).ToLowerInvariant());
        var hash = HMACSHA256.HashData(_salt, input);
        return $"[masked-{Convert.ToHexString(hash, 0, 3).ToLowerInvariant()}]";
    }

    private static string StripExe(string s)
    {
        s = s.Trim();
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    }

    /// <summary>*, ? 와일드카드 매칭(대소문자 무시).</summary>
    public static bool GlobMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static string ReadMachineSalt()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid;
        }
        catch
        {
            // 무시
        }
        return Environment.MachineName;
    }

    [GeneratedRegex(@"^(\\\\\?\\)?[A-Za-z]:\\Users\\[^\\]+\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserProfileRegex();
}
