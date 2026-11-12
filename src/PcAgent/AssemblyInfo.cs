using System.Runtime.InteropServices;

// P/Invoke 대상 DLL(pdh, tbs, wevtapi 등)은 System32 에서만 불러온다.
// exe 와 같은 폴더(예: 다운로드 폴더)에 심어 둔 가짜 DLL 이 관리자 권한으로 실행되는 것을 막기 위함.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
