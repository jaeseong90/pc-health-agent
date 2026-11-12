using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcAgent.Interop;

/// <summary>
/// Win32 API 선언. WMI(COM) 대신 가벼운 커널 API 를 직접 호출합니다.
/// LibraryImport(소스 생성) 를 사용해 Native AOT 와 호환됩니다.
/// </summary>
internal static unsafe partial class Native
{
    // ---------------- CPU / 메모리 ----------------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;   // 커밋 한도
        public ulong ullAvailPageFile;   // 남은 커밋
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    // ---------------- 프로세스 ----------------

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(
        SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessIoCounters(SafeProcessHandle process, out IO_COUNTERS counters);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    // ---------------- 자기 자신 부하 최소화 ----------------

    internal const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    internal const int ProcessMemoryPriority = 0;
    internal const int ProcessPowerThrottling = 4;
    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    internal const uint MEMORY_PRIORITY_LOW = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(nint process, int infoClass, void* info, uint size);

    // ---------------- 펌웨어 / TPM ----------------

    internal const uint RSMB = 0x52534D42; // 'RSMB' (SMBIOS)

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetSystemFirmwareTable(uint provider, uint tableId, byte* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFirmwareType(out int firmwareType);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TPM_DEVICE_INFO
    {
        public uint structVersion;
        public uint tpmVersion;
        public uint tpmInterfaceType;
        public uint tpmImpRevision;
    }

    internal const uint TBS_E_TPM_NOT_FOUND = 0x8028400F;

    [LibraryImport("tbs.dll")]
    internal static partial uint Tbsi_GetDeviceInfo(uint size, out TPM_DEVICE_INFO info);

    // ---------------- 디스크 ----------------

    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;
    internal const uint OPEN_EXISTING = 3;
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;   // 0 = StorageDeviceProperty, 7 = StorageDeviceSeekPenaltyProperty
        public int QueryType;    // 0 = PropertyStandardQuery
        public byte AdditionalParameters;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    // ---------------- Windows 이벤트 로그 쓰기 (advapi32) ----------------

    internal const ushort EVENTLOG_ERROR_TYPE = 0x0001;

    [LibraryImport("advapi32.dll", EntryPoint = "RegisterEventSourceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint RegisterEventSource(string? uncServerName, string sourceName);

    [LibraryImport("advapi32.dll", EntryPoint = "ReportEventW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReportEvent(
        nint eventLog, ushort type, ushort category, uint eventId, nint userSid, ushort numStrings, uint dataSize, char** strings, nint rawData);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeregisterEventSource(nint eventLog);

    // ---------------- 이벤트 로그 읽기 (wevtapi) ----------------

    internal const uint EvtQueryChannelPath = 0x1;
    internal const uint EvtQueryReverseDirection = 0x200;
    internal const uint EvtRenderEventXml = 1;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const int ERROR_NO_MORE_ITEMS = 259;

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint EvtQuery(nint session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EvtNext(nint resultSet, uint eventsSize, nint* events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EvtRender(
        nint context, nint fragment, uint flags, uint bufferSize, char* buffer, out uint bufferUsed, out uint propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EvtClose(nint handle);
}
