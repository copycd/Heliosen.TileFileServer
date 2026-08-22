#!/usr/bin/env bash
# Linux x64 배포본을 만든다.
#   ./publish-linux.sh              프레임워크 의존 (대상 서버에 .NET 10 런타임 필요)
#   ./publish-linux.sh --self-contained   자체 포함 (런타임 설치 불필요)
set -euo pipefail

SELF_CONTAINED=false
RID=linux-x64
OUT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --self-contained) SELF_CONTAINED=true ;;
    --arm64) RID=linux-arm64 ;;
    -o) shift; OUT="$1" ;;
    *) echo "알 수 없는 인자: $1"; exit 1 ;;
  esac
  shift
done

OUT="${OUT:-publish/$RID}"

dotnet publish src/Heliosen.TileFileServer.csproj \
  -c Release \
  -r "$RID" \
  -o "$OUT" \
  --self-contained "$SELF_CONTAINED" \
  -p:PublishReadyToRun=true

chmod +x "$OUT/Heliosen.TileFileServer"

echo
echo "완료: $OUT"
echo
echo "실행:"
echo "  cd $OUT && ./Heliosen.TileFileServer"
echo
echo "타일 루트를 바꿔서 실행:"
echo "  TileServer__Root=/srv/tiles ./Heliosen.TileFileServer"
echo
echo "systemd 로 등록:"
echo "  sudo cp deploy/heliosen-tileserver.service /etc/systemd/system/"
echo "  sudo systemctl daemon-reload && sudo systemctl enable --now heliosen-tileserver"
