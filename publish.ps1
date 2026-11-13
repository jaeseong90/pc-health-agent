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

# ---------------- 직원 배포 패키지 (git 저장소 없이 전달) ----------------
$fv = (Get-Item $exe).VersionInfo
$ver = '{0}.{1}.{2}' -f $fv.FileMajorPart, $fv.FileMinorPart, $fv.FileBuildPart
$suffix = if ($Runtime -eq 'win-arm64') { '-arm64' } else { '' }
$dist = Join-Path $PSScriptRoot 'artifacts\dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$distName = "PcAgent-$ver$suffix.exe"
$distExe = Join-Path $dist $distName
Copy-Item $exe $distExe -Force
$hash = (Get-FileHash $distExe -Algorithm SHA256).Hash
$guidePath = Join-Path $dist "설치안내-$ver$suffix.txt"
$guide = @"
PC 성능 진단 에이전트 설치 안내 (v$ver)

PC 업그레이드(메모리·SSD)·교체 대상을 정하기 위해 2주 정도 PC 성능을 기록합니다.
데이터는 각자 PC 에만 저장되고 외부로 전송되지 않습니다.

■ 설치 (1분)
 1. $distName 파일을 내려받아 더블클릭합니다.
    - "Windows의 PC 보호" 창이 뜨면 [추가 정보] → [실행]을 누릅니다.
 2. 안내를 읽고 1(설치하기)을 누른 뒤, 관리자 권한 확인 창에서 [예]를 누릅니다.
 3. "설치 완료"가 보이면 아무 키나 눌러 창을 닫습니다. 이후 평소처럼 사용하면 됩니다.
 ※ 관리자 권한이 없는 PC 는 IT 담당자에게 설치를 요청해 주세요.

■ 2주 뒤 결과 보내기
 1. 시작 메뉴에서 "PC Health Agent"를 실행합니다.
 2. 1(결과 내보내기)을 누르면 바탕화면에 PcHealth_<PC이름>_<날짜>.zip 파일이 만들어집니다.
 3. 이 zip 파일을 담당자에게 보내 주세요.

■ 기록하는 것 / 하지 않는 것
 - 기록: CPU·메모리·디스크 사용률, 자원이 부족했던 순간의 실행 프로그램 이름, PC 사양, 블루스크린·앱 오류 기록
 - 기록하지 않음: 창 제목, 문서·파일 이름, 웹 주소, 키 입력, 화면
 - 에이전트 자체는 메모리 약 20MB만 쓰고 CPU 는 거의 쓰지 않습니다.

■ 업데이트
 새 버전 파일을 받아 더블클릭한 뒤 1(업데이트)을 누르면 됩니다. 수집 데이터와 설정은 그대로 유지됩니다.

■ 제거
 설정 > 앱 > 설치된 앱 > "PC Health Agent" > 제거   (또는 시작 메뉴 > PC Health Agent > 제거)

파일 확인용 SHA-256: $hash
"@
[IO.File]::WriteAllText($guidePath, $guide, (New-Object System.Text.UTF8Encoding $true))

Write-Host ""
Write-Host ("완료: {0} ({1:N1} MB, {2})" -f $exe, ((Get-Item $exe).Length / 1MB), $(if ($useAot) { 'Native AOT' } else { '단일 exe' })) -ForegroundColor Green
Write-Host "배포용: $distExe"
Write-Host "안내문: $guidePath  (메신저·메일 본문에 붙여 넣기)"
Write-Host "SHA-256: $hash"
