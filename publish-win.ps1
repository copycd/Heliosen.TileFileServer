# Windows x64 배포본을 만든다.
#   .\publish-win.ps1              프레임워크 의존 (대상 서버에 .NET 10 런타임 필요)
#   .\publish-win.ps1 -SelfContained   자체 포함 (런타임 설치 불필요, 용량 큼)
param(
    [switch]$SelfContained,
    [string]$Output = "publish\win-x64"
)

$ErrorActionPreference = "Stop"

$args = @(
    "publish", "src\Heliosen.TileFileServer.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "-o", $Output,
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" })
)

# 기동 시간을 줄인다. 타일 서버는 오래 떠 있으니 필수는 아니지만 재시작이 빨라진다.
$args += "-p:PublishReadyToRun=true"

dotnet @args
if ($LASTEXITCODE -ne 0) { throw "publish 실패" }

Write-Host ""
Write-Host "완료: $Output"
Write-Host ""
Write-Host "실행:"
Write-Host "  cd $Output"
Write-Host "  .\Heliosen.TileFileServer.exe"
Write-Host ""
Write-Host "타일 루트를 바꿔서 실행:"
Write-Host "  `$env:TileServer__Root='D:\tiles'; .\Heliosen.TileFileServer.exe"
Write-Host ""
Write-Host "윈도우 서비스로 등록 (관리자 권한 필요):"
Write-Host "  sc.exe create HeliosenTileServer binPath= `"$(Resolve-Path $Output)\Heliosen.TileFileServer.exe`" start= auto"
Write-Host "  참고: 콘솔 앱이라 서비스 제어 신호를 직접 처리하지 않는다."
Write-Host "        정식 서비스로 쓰려면 NSSM 을 권장한다:  nssm install HeliosenTileServer ..."
