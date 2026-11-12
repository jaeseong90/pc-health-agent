<#
.SYNOPSIS
  배포용 PcAgent.exe 단일 파일을 만듭니다. (.NET 런타임 설치 불필요)

.DESCRIPTION
  - Visual Studio "C++를 사용한 데스크톱 개발" 워크로드(또는 Build Tools)가 있으면 Native AOT 로 빌드합니다.
    → 가장 가볍습니다(메모리 사용량 최소, 시작 즉시).
  - 없으면 트리밍된 자체 포함 단일 exe 로 빌드합니다(동작은 같고 메모리를 조금 더 씀).

.EXAMPLE
  .\publish.ps1                  # win-x64
  .\publish.ps1 -Runtime win-arm64
  .\publish.ps1 -NoAot           # AOT 없이 빌드
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$NoAot
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $PSScriptRoot 'src\PcAgent\PcAgent.csproj'
$out = Join-Path $PSScriptRoot "artifacts\$Runtime"

function Test-CppToolchain([string]$rid) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $false }
    # 대상 아키텍처의 링커가 있어야 AOT 빌드 가능
    $component = if ($rid -eq 'win-arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $found = & $vswhere -products * -latest -requires $component -property installationPath
    return [bool]$found
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }

$useAot = -not $NoAot -and (Test-CppToolchain $Runtime)
if ($useAot) {
    Write-Host "Native AOT 로 빌드합니다 ($Runtime)..." -ForegroundColor Cyan
    dotnet publish $project -c Release -r $Runtime -o $out -nologo -p:DebugType=none
}
else {
    if (-not $NoAot) {
        Write-Host "C++ 빌드 도구가 없어 Native AOT 대신 단일 exe 로 빌드합니다." -ForegroundColor Yellow
        Write-Host "  (가장 가벼운 AOT 빌드를 원하면 Visual Studio Installer 에서 'C++를 사용한 데스크톱 개발' 설치)" -ForegroundColor Yellow
    }
    dotnet publish $project -c Release -r $Runtime -o $out -nologo `
        -p:PublishAot=false -p:SelfContained=true -p:PublishSingleFile=true -p:PublishTrimmed=true `
        -p:DebugType=none
}
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (exit $LASTEXITCODE)" }

$exe = Join-Path $out 'PcAgent.exe'
Get-ChildItem $out -Filter '*.pdb' | Remove-Item -Force
$extra = Get-ChildItem $out -Exclude 'PcAgent.exe'
if ($extra) { Write-Host "참고: exe 외 파일이 함께 생성됐습니다(같이 배포 필요): $($extra.Name -join ', ')" -ForegroundColor Yellow }
Write-Host ""
Write-Host ("완료: {0} ({1:N1} MB, {2})" -f $exe, ((Get-Item $exe).Length / 1MB), $(if ($useAot) { 'Native AOT' } else { '단일 exe' })) -ForegroundColor Green
Write-Host "설치: 관리자 권한 cmd 에서  PcAgent.exe install"
