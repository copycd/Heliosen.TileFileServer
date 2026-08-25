# Windows 에서 배포본을 만든다. 리눅스용도 여기서 만들 수 있다(크로스 컴파일).
#
#   .\publish.ps1                          win-x64
#   .\publish.ps1 -Rid linux-x64           리눅스용 (윈도우에서 만들어도 된다)
#   .\publish.ps1 -Rid linux-arm64
#   .\publish.ps1 -SelfContained           런타임 포함 (대상에 .NET 설치 불필요)
#
# 이 파일은 반드시 UTF-8 BOM 으로 저장해야 한다.
# Windows PowerShell 5.1 은 BOM 이 없으면 시스템 코드페이지(한국어=949)로 읽어서 한글이 깨진다.
param(
    [ValidateSet("win-x64", "linux-x64", "linux-arm64")]
    [string]$Rid = "win-x64",

    [switch]$SelfContained,

    [string]$Output
)

$ErrorActionPreference = "Stop"

if (-not $Output) { $Output = "publish\$Rid" }

$publishArgs = @(
    "publish", "src\Heliosen.TileFileServer.csproj",
    "-c", "Release",
    "-r", $Rid,
    "-o", $Output,
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),

    # 기동 시간을 줄인다. 타일 서버는 오래 떠 있으니 필수는 아니지만 재시작이 빨라진다.
    "-p:PublishReadyToRun=true"
)

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish 실패" }

$full = (Resolve-Path $Output).Path

Write-Host ""
Write-Host "완료: $Output   ($Rid)"
Write-Host ""

if ($Rid -eq "win-x64") {
    Write-Host "실행:"
    Write-Host "  cd $Output"
    Write-Host "  .\Heliosen.TileFileServer.exe"
    Write-Host ""
    Write-Host "타일 루트를 바꿔서 실행:"
    Write-Host "  `$env:TileServer__Root='D:\tiles'; .\Heliosen.TileFileServer.exe"
    Write-Host ""
    Write-Host "윈도우 서비스로 등록 (관리자 권한 필요):"
    Write-Host "  sc.exe create HeliosenTileServer binPath= `"$full\Heliosen.TileFileServer.exe`" start= auto"
    Write-Host "  참고: 콘솔 앱이라 서비스 제어 신호를 직접 처리하지 않는다."
    Write-Host "        정식 서비스로 쓰려면 NSSM 을 권장한다:  nssm install HeliosenTileServer ..."
}
else {
    Write-Host "리눅스 서버로 옮긴 뒤:"
    Write-Host ""
    Write-Host "  # 실행 권한이 필요하다. 윈도우(NTFS)에는 실행 비트가 없어서"
    Write-Host "  # zip/scp 로 옮기면 권한이 떨어져 나가고 'Permission denied' 가 난다."
    Write-Host "  chmod +x Heliosen.TileFileServer"
    Write-Host "  TileServer__Root=/srv/tiles ./Heliosen.TileFileServer"
    Write-Host ""
    Write-Host "  # chmod 를 못 하는 상황이면 dll 을 직접 실행해도 된다(실행 비트가 필요 없다)."
    Write-Host "  TileServer__Root=/srv/tiles dotnet ./Heliosen.TileFileServer.dll"
    Write-Host ""
    Write-Host "systemd 로 등록:"
    Write-Host "  sudo cp deploy/heliosen-tileserver.service /etc/systemd/system/"
    Write-Host "  sudo systemctl daemon-reload && sudo systemctl enable --now heliosen-tileserver"
}
