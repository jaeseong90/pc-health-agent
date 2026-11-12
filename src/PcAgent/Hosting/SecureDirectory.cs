using System.Security.AccessControl;
using System.Security.Principal;

namespace PcAgent.Hosting;

/// <summary>
/// SYSTEM 서비스가 쓰는 데이터 폴더의 신뢰성을 보장합니다.
/// 일반 사용자가 미리 만들어 둔 폴더나 링크(junction/symlink)를 통해 서비스가 엉뚱한 파일을
/// 쓰기·이동·삭제하게 만드는 권한 상승 공격을 막습니다.
/// 신뢰 조건: 링크가 아님 + 소유자가 SYSTEM/Administrators + 그 외 계정에 쓰기 권한 없음.
/// </summary>
internal static class SecureDirectory
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    /// <summary>SYSTEM·Administrators 모든 권한, Users 읽기. 상위 폴더(ProgramData) 권한은 상속하지 않음.</summary>
    internal static DirectorySecurity CreateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static bool IsTrusted(string path, out string reason)
    {
        var info = new DirectoryInfo(path);
        if (IsReparsePoint(path))
        {
            reason = "링크(junction/symlink)입니다";
            return false;
        }
        if (!info.Exists)
        {
            reason = "폴더가 없습니다";
            return false;
        }

        var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (owner != LocalSystem && owner != Administrators))
        {
            reason = $"소유자가 SYSTEM/Administrators 가 아닙니다 ({owner?.Value ?? "알 수 없음"})";
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid == LocalSystem || sid == Administrators || sid == TrustedInstaller || sid == CreatorOwner) continue;
            int rights = (int)rule.FileSystemRights;
            if ((rule.FileSystemRights & WriteRights) != 0 || (rights & (GenericAll | GenericWrite)) != 0)
            {
                reason = $"관리자가 아닌 계정({sid.Value})에 쓰기 권한이 있습니다";
                return false;
            }
        }

        reason = "";
        return true;
    }

    /// <summary>서비스 시작 시 호출. 폴더가 없으면 안전하게 만들고, 신뢰할 수 없으면 예외를 던집니다.</summary>
    internal static void EnsureForService(AgentPaths paths)
    {
        foreach (var dir in new[] { paths.Root, paths.DataDir })
        {
            if (!Directory.Exists(dir) && !IsReparsePoint(dir)) CreateSecure(dir);
            if (!IsTrusted(dir, out var reason))
            {
                throw new InvalidOperationException(
                    $"데이터 폴더를 신뢰할 수 없어 수집을 중단합니다: {dir} — {reason}. 관리자 권한으로 'PcAgent.exe install' 을 다시 실행하세요.");
            }
        }
    }

    /// <summary>설치 시 호출(관리자). 신뢰할 수 없는 기존 폴더는 치우고 안전하게 다시 만듭니다.</summary>
    internal static void PrepareForInstall(AgentPaths paths, Action<string> info)
    {
        if (Directory.Exists(paths.Root) || IsReparsePoint(paths.Root))
        {
            string reason = "";
            bool trusted = IsTrusted(paths.Root, out reason)
                           && (!Directory.Exists(paths.DataDir) && !IsReparsePoint(paths.DataDir) || IsTrusted(paths.DataDir, out reason));
            if (!trusted)
            {
                info($"기존 데이터 폴더를 신뢰할 수 없어 새로 만듭니다 ({reason}).");
                RemoveUntrusted(paths.Root, info);
            }
        }

        if (!Directory.Exists(paths.Root)) CreateSecure(paths.Root);
        if (!Directory.Exists(paths.DataDir)) CreateSecure(paths.DataDir);

        foreach (var dir in new[] { paths.Root, paths.DataDir })
        {
            if (!IsTrusted(dir, out var reason)) throw new InvalidOperationException($"데이터 폴더 권한 설정에 실패했습니다: {dir} — {reason}");
        }
    }

    private static void CreateSecure(string path) => new DirectoryInfo(path).Create(CreateSecurity());

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void RemoveUntrusted(string path, Action<string> info)
    {
        try
        {
            // 링크는 링크 자체만 지운다(대상은 건드리지 않음). .NET 은 재귀 삭제 시 내부 링크를 따라가지 않음.
            if (IsReparsePoint(path)) Directory.Delete(path, recursive: false);
            else Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 소유자가 지우지 못하게 막아 둔 경우: 이름을 바꿔 치워 둔다(ProgramData 에 대한 관리자 권한으로 가능)
            var aside = $"{path}.untrusted-{DateTime.Now:yyyyMMddHHmmss}";
            Directory.Move(path, aside);
            info($"삭제할 수 없어 이름을 바꿔 두었습니다: {aside} (확인 후 직접 삭제하세요)");
        }
    }
}
