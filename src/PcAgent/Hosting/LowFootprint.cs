using PcAgent.Interop;

namespace PcAgent.Hosting;

/// <summary>
/// 에이전트가 사용자 작업을 방해하지 않도록 자기 자신의 우선순위를 낮춥니다.
/// - CPU 우선순위: 낮음(Below Normal)
/// - 효율 모드(EcoQoS): Windows 11 작업 관리자에 '효율 모드'로 표시, 저전력 코어/클럭에서 실행
/// - 메모리 우선순위: 낮음 (메모리가 부족하면 에이전트 페이지가 먼저 정리됨)
/// </summary>
internal static unsafe class LowFootprint
{
    public static void Apply()
    {
        var self = Native.GetCurrentProcess();
        Native.SetPriorityClass(self, Native.BELOW_NORMAL_PRIORITY_CLASS);

        var power = new Native.PROCESS_POWER_THROTTLING_STATE
        {
            Version = Native.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        };
        Native.SetProcessInformation(self, Native.ProcessPowerThrottling, &power, (uint)sizeof(Native.PROCESS_POWER_THROTTLING_STATE));

        var memory = new Native.MEMORY_PRIORITY_INFORMATION { MemoryPriority = Native.MEMORY_PRIORITY_LOW };
        Native.SetProcessInformation(self, Native.ProcessMemoryPriority, &memory, (uint)sizeof(Native.MEMORY_PRIORITY_INFORMATION));
    }
}
