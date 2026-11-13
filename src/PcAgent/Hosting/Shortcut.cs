using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PcAgent.Interop;

namespace PcAgent.Hosting;

/// <summary>
/// 바로가기(.lnk) 생성. Windows Shell 의 ShellLink COM 객체를 소스 생성 COM 으로 호출합니다(Native AOT 호환).
/// </summary>
internal static partial class Shortcut
{
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    public static void Create(string linkPath, string targetPath, string description)
    {
        // 이미 초기화된 스레드면 S_FALSE/RPC_E_CHANGED_MODE — 어느 쪽이든 ShellLink(Both) 사용 가능
        Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED | Native.COINIT_DISABLE_OLE1DDE);
        Marshal.ThrowExceptionForHR(Native.CoCreateInstance(CLSID_ShellLink, 0, Native.CLSCTX_INPROC_SERVER, IID_IShellLinkW, out nint unknown));
        object? obj = null;
        try
        {
            obj = ComWrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            var link = (IShellLinkW)obj;
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(targetPath, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)obj).Save(linkPath, true);
        }
        finally
        {
            (obj as ComObject)?.FinalRelease();
            Marshal.Release(unknown);
        }
    }
}

/// <summary>IShellLinkW — vtable 순서대로 선언(사용하지 않는 메서드는 포인터 인자로 자리만 차지).</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint file, int maxChars, nint findData, uint flags);
    void GetIDList(out nint idList);
    void SetIDList(nint idList);
    void GetDescription(nint name, int maxChars);
    void SetDescription(string name);
    void GetWorkingDirectory(nint dir, int maxChars);
    void SetWorkingDirectory(string dir);
    void GetArguments(nint args, int maxChars);
    void SetArguments(string args);
    void GetHotkey(out ushort hotkey);
    void SetHotkey(ushort hotkey);
    void GetShowCmd(out int showCmd);
    void SetShowCmd(int showCmd);
    void GetIconLocation(nint iconPath, int maxChars, out int iconIndex);
    void SetIconLocation(string iconPath, int iconIndex);
    void SetRelativePath(string relativePath, uint reserved);
    void Resolve(nint hwnd, uint flags);
    void SetPath(string file);
}

/// <summary>IPersistFile (IPersist::GetClassID 포함, vtable 순서).</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid classId);
    [PreserveSig]
    int IsDirty();
    void Load(string fileName, uint mode);
    void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted(string fileName);
    void GetCurFile(out nint fileName);
}
