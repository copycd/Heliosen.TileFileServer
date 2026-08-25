# Heliosen TileFileServer

`DTB.RocksTileStore` 로 만든 타일 DB 를 **nginx 처럼** HTTP 로 내보내는 서버.
Windows / Linux 양쪽에서 같은 코드로 돈다.

루트 폴더 밑에 DB 폴더를 넣으면 **폴더 이름이 그대로 URL 첫 조각**이 된다. 설정에 레이어를 적을 필요가 없다.

```
/srv/tiles/               <- TileServer.Root
├── aaa/                  RocksDB (jpg 래스터)   ->  /aaa/12/345/4455.jpg
├── bbb/                  RocksDB (terrain)      ->  /bbb/4/222/5555.terrain
├── ccc/                  그냥 파일 폴더          ->  /ccc/7/12/99.png
└── korea/                (묶음 폴더, 레이어 아님)
    ├── seoul/            RocksDB                ->  /korea/seoul/12/345/4455.jpg
    └── busan/            RocksDB                ->  /korea/busan/4/222/5555.terrain
```

폴더를 여러 단으로 묶어도 된다. 자세한 규칙은 [폴더 여러 단으로 묶기](#폴더-여러-단으로-묶기) 참고.

---

## 빠르게 띄우기

```bash
dotnet run --project src
```

타일 루트를 지정해서:

```bash
TileServer__Root=/srv/tiles dotnet run --project src
```

기본 포트는 `7080`. 브라우저로 `http://localhost:7080/` 을 열면 현재 서비스 중인 레이어 목록이 나온다.

표본 DB 로 시험해보려면:

```bash
dotnet run --project tools/SampleStore -- ./tiles
```

`tiles/aaa`(jpg)와 `tiles/bbb`(gzip terrain)를 만든다. 빌더와 **같은 키 포맷 + 같은 기본 옵션**으로 만들기 때문에, 이걸로 되면 실제 DB 로도 된다.

---

## URL 규칙

| 요청 | 동작 |
|---|---|
| `/{layer}/{z}/{x}/{y}.{ext}` | 타일. 확장자가 키의 포맷 바이트를 결정한다 |
| `/{layer}/{z}/{x}/{y}` | 타일. DB 의 대표 포맷으로 내보낸다 |
| `/{layer}/layer.json` | 문자열 키로 저장된 부가 파일 |
| `/{layer}/tilemapresource.xml` | 위와 같음 |
| `/{layer}/0/0/0.b3dm` | 3D Tiles 조각(문자열 키) |
| `/` | 레이어 목록 (HTML) |
| `/healthz` | 헬스체크 |
| `/version` | 버전 |

**좌표에 임의의 상한을 두지 않는다.** 잘못 넣은 좌표는 키가 없으니 그냥 404 다.
예전에는 `z<=30`, `x/y<2^24` 로 미리 걸렀는데, 타일 스킴이 여러 가지라 정상 요청을 막을 위험만 있었다.

다만 **키 포맷이 표현할 수 없는 값은 반드시 걸러낸다.** 키의 레벨이 1 바이트라서
`z=256` 을 그냥 넘기면 `(byte)256 == 0` 으로 감싸돌아 **z=0 타일을 엉뚱하게 내보낸다.**
못 찾는 것과 다른 것을 주는 것은 전혀 다른 문제라, 이건 레이어에서 막아 404 로 만든다.
음수도 같은 이유로 막는다(`(uint)-1` 은 42 억이 된다).

확장자 → 포맷: `jpg`/`jpeg`→1, `png`→2, `terrain`→5, `raw`→6, `pbf`/`mvt`→10.

이 숫자와 판정은 **`DTB.RocksTileStore` 것을 그대로 쓴다** — 열거형(`TileLayerFormatKind`),
키 인코딩(`RocksTileStore.EncodeTileKey`), 확장자 판정(`RocksTileStoreUtils.GetLayerFormatKind`) 모두
DB 를 만드는 쪽과 같은 코드다. 그래서 포맷 바이트가 서버와 빌더 사이에서 어긋날 수 없다.
`pbf`/`mvt`/`vector` 만 그쪽에 없어서 서버가 보탠다(빌더가 아직 벡터 타일을 만들지 않는다).

**`ExtensionFallback`**(기본 켜짐): jpg 로 만든 DB 에 `.png` 로 요청이 와도 타일이 나간다.
단 `Content-Type` 은 실제 바이트의 포맷(`image/jpeg`)으로 정직하게 보낸다. 끄면 nginx 처럼 딱 맞지 않으면 404.

### 파일 폴더는 일반 웹 서버다

RocksDB 가 아닌 파일 폴더에는 **타일 규칙이 없다.** URL 경로가 곧 파일 경로다.

```
{Root}/site/css/style.css       ->  /site/css/style.css
{Root}/site/img/sub/photo.jpg   ->  /site/img/sub/photo.jpg
{Root}/site/99/1/2.png          ->  /site/99/1/2.png     (z 상한 같은 건 없다)
```

- **URL 을 그대로 파일 경로로 쓴다.** 라우트에서 파싱한 숫자로 경로를 다시 조립하지 않는다.
  그러면 `07` 이 `7` 로 정규화되어 실제로 있는 `07/12/99.png` 를 못 찾는다.
  앞자리 0, 공백(`%20`), 한글, `+` 가 들어간 이름 모두 그대로 찾는다.
- **좌표 범위 제한 없음.** 애초에 파일 폴더에는 좌표라는 개념이 없다. 그냥 경로다.
- **확장자를 추측하지 않는다.** 없는 파일에 대해 `.jpg`, `.png` 를 뒤지지 않고 그대로 404 다.
  확장자 없는 파일이 실제로 있으면 그건 그대로 내보낸다.
- **MIME 은 ASP.NET 정적 파일 표(약 380 종)를 쓴다.** css, js, woff2, svg, mp4, wasm 다 제대로 나간다.
  거기에 타일 전용 확장자(`.terrain`, `.pbf`, `.b3dm`, `.glb` ...)만 얹었다.
- ETag / 304 / Range / HEAD 는 RocksDB 레이어와 똑같이 동작한다.

반대로 **RocksDB 는 숫자로 정규화된다.** 타일 좌표는 숫자이므로
`/aaa/012/345/4455.jpg` 와 `/aaa/12/345/4455.jpg` 는 같은 타일이다.
(라우트의 `{z:int}` 가 하는 일이다. 이걸 404 로 만들려면 정규 표기 검사를 **더 넣어야** 하는데,
 그런 URL 을 만드는 클라이언트가 없어서 그냥 둔다.)

디렉터리 색인(`/site/` -> `index.html`)은 하지 않는다. 필요하면 넣을 수 있다.

**레이어 이름은 대소문자를 구분하지 않는다.** Linux 에서 폴더가 `ccc` 여도 `/CCC/...` 로 받는다.
(nginx on Linux 와 다른 점이다. 플랫폼 간 동작을 같게 맞추려고 일부러 이렇게 했다.)

---

## 폴더 여러 단으로 묶기

레이어가 많아지면 최상위에 다 늘어놓기보다 지역·제품별로 묶는 게 낫다.
`MaxLayerDepth`(기본 3) 까지 폴더를 겹쳐도 되고, **폴더 경로가 그대로 URL 이 된다.**

```
{Root}/korea/seoul/          ->  /korea/seoul/12/345/4455.jpg
{Root}/asia/japan/tokyo/     ->  /asia/japan/tokyo/12/345/4455.jpg
{Root}/korea/seoul/layer.json ->  /korea/seoul/layer.json
```

### 어디까지가 레이어인지 정하는 규칙

**루트 바로 밑의 폴더 하나하나가 독립된 "가지" 다.** 가지 안에서는 두 규칙만 있다.

1. **폴더가 RocksDB 면 그게 마지막이다.** 레이어로 확정하고 그 안으로는 절대 들어가지 않는다.
   DB 안에 무엇이 있어도(파일이든 또 다른 DB 든) 서비스되지 않는다.
2. **가지 안에 RocksDB 가 하나라도 있으면 그 가지는 통째로 RocksDB 가지다.**
   중간 폴더들은 이름만 빌려주는 묶음 폴더가 되고, **그 가지의 파일 폴더는 서비스하지 않는다.**
3. **가지 안에 RocksDB 가 전혀 없으면 가지의 맨 윗 폴더 하나가 파일 레이어다.** 더 내려가지 않는다.

즉 **한 가지는 RocksDB 아니면 파일, 한 가지로만 쓴다.** 섞이지 않는다.

3번이 파일 타일 폴더를 지킨다. 파일 타일 폴더는 안이 `7/12/99.png` 처럼 z/x/y 로 되어 있어서,
계속 내려가면 `filelayer/7/12` 같은 이름으로 쪼개져 버린다.
그래서 RocksDB 가 없는 가지는 **가장 얕은 곳에서** 레이어로 확정하고 멈춘다.

> **루트는 왜 예외인가**
> 루트까지 한 가지로 묶으면 `{Root}/aaa`(DB) 와 `{Root}/ccc`(파일 폴더)를 함께 두는 조합이
> 통째로 죽는다. 그게 이 서버의 기본 사용법이라 루트 바로 밑은 서로 독립된 가지로 둔다.
> 최상위에서는 DB 와 파일 폴더를 얼마든지 섞어도 된다.

가지 안의 파일 폴더가 빠질 때는 경고 로그를 남긴다. 조용히 404 가 나는 상황을 만들지 않는다.

```
RocksDB 가지 안의 파일 폴더는 서비스하지 않습니다. Skipped=korea/jeju (같은 가지에 RocksDB 가 있어서).
서비스하려면 이 폴더를 RocksDB 가지 밖으로 옮기세요.
```

### 폴더 이름을 숫자만으로 짓지 말 것

**이름이 숫자뿐인 폴더는 묶음 폴더가 될 수 없다.** 타일 레벨(z)로 보고 파고들기를 멈춘다.

이게 없으면 파일 타일 폴더를 훑을 때 z/x 폴더 수만큼(레벨 14 면 수만 개) 매번 디스크를 뒤진다.
z/x/y 는 전부 숫자이고 사람이 짓는 묶음 이름(`korea`, `asia`)은 그렇지 않으니,
이 한 줄짜리 검사로 "여기부터는 타일 트리다" 를 공짜로 구분한다.

그래서 `{Root}/2024/seoul/` 처럼 **숫자만으로 된 묶음 폴더는 인식되지 않는다.**
`y2024`, `2024y` 처럼 글자를 하나 섞으면 된다.

### 요약: 가지 하나는 한 종류

| 두는 곳 | 서비스됨? |
|---|---|
| RocksDB 폴더 안의 파일 | 안 됨 (DB 가 마지막 노드) |
| RocksDB 폴더 안의 또 다른 RocksDB | 안 됨 |
| RocksDB 가지 안의 파일 폴더 | 안 됨 (경고 로그 남음) |
| 파일 가지 안의 RocksDB | 상황에 따라 — 중간 폴더 이름이 숫자면 안 되고, 아니면 그 가지가 DB 가지로 바뀐다 |
| 최상위에서 DB 와 파일 폴더를 섞기 | **됨** |

한 폴더 밑에서 두 종류를 섞어 쓰려면 최상위로 분리하면 된다.

실제 동작을 확인한 표다.

| 폴더 구성 | 결과 |
|---|---|
| `aaa/` (RocksDB) | 레이어 `aaa` |
| `aaa/nested/` (DB 안의 DB) | **서비스 안 됨** — DB 가 마지막 노드 |
| `korea/seoul/`, `korea/busan/` (둘 다 RocksDB) | 레이어 `korea/seoul`, `korea/busan` (`korea` 는 레이어 아님) |
| `asia/japan/tokyo/` (RocksDB) | 레이어 `asia/japan/tokyo` |
| `ccc/7/12/9.png` (최상위 파일 트리) | 레이어 `ccc` 하나 (쪼개지지 않음) |
| `korea/jeju/` 파일 트리, 형제 `korea/busan` 이 RocksDB | **`korea/jeju` 서비스 안 됨** — DB 가지라서 |
| `photos/7/...` 파일 트리 + `photos/deep/aaa` RocksDB | `photos/deep/aaa` 만 서비스, `photos/7` 은 안 됨 |
| 최상위에 `aaa`(DB) + `ccc`(파일) | 둘 다 서비스 (루트는 예외) |

### 성능은 떨어지지 않는다

같은 내용의 DB 를 깊이만 바꿔 두고 재봤다 (동시성 64, 8 초, 교차 측정).

| 라운드 | 깊이 1 | 깊이 2 | 깊이 3 |
|---|---|---|---|
| 1 | 218,565 | 171,143 | 172,263 |
| 2 | 168,078 | 168,451 | 174,872 |
| 3 | 172,399 | 170,724 | 166,125 |

오차 범위 안에서 같다 (라운드 1 의 깊이 1 은 첫 실행 이상치다).

이유는 조회 경로가 깊이와 무관하기 때문이다. 레이어를 찾는 것은 `FrozenDictionary` 조회 **한 번**이고,
사전의 키가 `"aaa"` 든 `"korea/seoul"` 든 비용이 같다. 깊이마다 라우트를 미리 등록해 두므로
경로 해석도 ASP.NET 의 라우팅 트리가 처리한다.

깊이 2 이상에서만 레이어 이름 문자열을 하나 만든다(요청당 수십 바이트).
깊이 1 은 경로 조각을 그대로 쓰므로 예전과 완전히 같다.

### 재훑기 비용

요청 처리와 별개로, 주기적 재훑기가 디스크를 훑는 비용은 신경 써야 한다.
z/x 폴더가 3,084 개인 파일 타일 트리로 재훑기 한 번을 재봤다
(같은 curl 연결로 측정 — 새 curl 프로세스를 띄우면 그 기동 비용 250 ms 가 섞여서 못 쓴다).

| | 재훑기 1 회 |
|---|---|
| 숫자 폴더 가드 없음 | **450 ms** |
| 가드 적용 (현재) | **1.4 ms** |

기준선인 `/healthz` 가 0.26 ms 다. 가드가 없으면 30 초마다 0.45 초씩 디스크를 뒤지는데,
레벨 14 짜리 실제 트리면 이게 초 단위로 커진다.

> 안 쓰겠다면 `MaxLayerDepth: 1` 로 두면 최상위 폴더만 레이어가 된다(예전 동작).
> 상한은 4 다.

---

## nginx 에서 넘어올 때 알아둘 것

정적 파일 서버로서 기대하는 동작은 다 들어있다. 직접 만들지 않고 ASP.NET 의 파일 결과 처리기에 넘겼다.

- `ETag` + `If-None-Match` → **304**
- `Last-Modified` + `If-Modified-Since` → **304**
- `Range` → **206**
- `HEAD`
- `Cache-Control: public, max-age=…` (`CacheImmutable` 로 `immutable` 추가)
- CORS (기본 모든 오리진 GET/HEAD 허용)

ETag 는 응답마다 내용을 해시하지 않는다.
조회 전용 RocksDB 핸들은 **열린 시점의 스냅샷**을 보므로 핸들이 사는 동안 내용이 불변이다.
그래서 `(DB 지문 + 좌표 + 길이)` 만으로 강한 ETag 를 만들 수 있고, CPU 를 거의 쓰지 않는다.
DB 를 갈아끼우면 지문이 바뀌어 ETag 도 바뀐다 → 클라이언트 캐시가 자동으로 무효화된다.

### gzip 으로 저장된 terrain

빌더(`DrvHeightTerrain.writeGZipped`)는 terrain 타일을 gzip 으로 넣는 경우가 있다.
그때 `Content-Encoding: gzip` 을 붙이지 않으면 Cesium 이 압축된 바이트를 그대로 파싱하다 깨진다.
그래서 **매직 넘버(1F 8B 08)로 판별해서** 헤더를 붙인다. jpg/png 는 애초에 검사하지 않는다.

저장된 바이트가 gzip 이면 `Accept-Encoding` 과 무관하게 gzip 으로 보낸다
(압축 안 된 원본이 없으므로. nginx `gzip_static always` 와 같은 동작이다).

### 압축은 하지 않는다

타일은 이미 압축된 포맷이다. 응답 압축을 켜면 CPU 만 쓰고 얻는 게 없다.

### 요청 로그

`appsettings.json` 에서 `Microsoft.AspNetCore` 를 `Warning` 으로 낮춰뒀다.
`Information` 으로 두면 요청마다 로그가 몇 줄씩 찍혀서 **그 로깅이 가장 큰 병목이 된다**.
nginx 의 `access_log off` 에 해당한다. 요청 단위 추적이 필요하면 그때만 올린다.

---

## 설정

`appsettings.json` 의 `TileServer` 섹션. 전부 환경변수로도 덮어쓸 수 있다 (`TileServer__Root=...`).

| 키 | 기본값 | 설명 |
|---|---|---|
| `Root` | `tiles` | 레이어 폴더들이 있는 루트 |
| `MaxLayerDepth` | `3` | 레이어 이름의 폴더 깊이. `1` 이면 최상위만 |
| `RescanSeconds` | `30` | 루트 재훑기 주기. `0` 이면 끔 |
| `WatchFileSystem` | `true` | 폴더 감시로 즉시 반응 |
| `BlockCacheMB` | `512` | **모든 DB 가 공유하는** 블록 캐시 |
| `MaxOpenFilesPerDb` | `256` | DB 하나가 열어둘 SST 수. `-1` 무제한 |
| `NegativeCacheEntries` | `100000` | "없는 타일" 기억 개수. `0` 이면 끔 |
| `CacheMaxAgeSeconds` | `3600` | `Cache-Control: max-age` |
| `CacheImmutable` | `false` | `immutable` 추가 |
| `ExtensionFallback` | `true` | 확장자가 DB 포맷과 달라도 내보냄 |
| `VerifyChecksums` | `true` | RocksDB 체크섬 검증 |
| `OpenRetrySeconds` | `30` | 열기 실패 후 재시도 간격 |
| `WarmupOnStart` | `true` | 기동 때 전부 열어봄 |
| `RetireGraceSeconds` | `10` | 사라진 레이어를 닫기까지의 여유 |
| `IgnoreFolders` | `[]` | 레이어로 만들지 않을 폴더 이름 |
| `EnableCors` / `AllowedOrigins` | `true` / `[]` | 비우면 모든 오리진 |
| `EnableAdmin` / `AdminToken` | `true` / `null` | 아래 참고 |

포트는 `Kestrel` 섹션에서:

```bash
Kestrel__Endpoints__Http__Url=http://0.0.0.0:7080
```

> 포트는 `appsettings.json` **한 곳에서만** 정한다.
> `launchSettings.json` 에 `applicationUrl` 을 적으면 `ASPNETCORE_URLS` 가 설정되고,
> Kestrel 은 두 소스가 겹치는 것만 보고 (값이 같아도) 이런 경고를 낸다:
> `Overriding address(es) ... Binding to endpoints defined via IConfiguration instead.`
> 그래서 `launchSettings.json` 에는 일부러 `applicationUrl` 을 두지 않았다.
>
> `launchSettings.json` 은 `appsettings.json` 과 달리 **JSON 주석을 지원하지 않는다.**
> 주석을 넣으면 파싱이 조용히 실패해서 프로필이 통째로 무시되고
> (`ASPNETCORE_ENVIRONMENT=Development` 도 안 먹어서 Production 으로 뜬다).

---

## 메모리와 파일 디스크립터 (중요)

### 블록 캐시는 반드시 공유해야 한다

빌더는 DB 를 **기본 옵션**으로 만든다. 우리가 캐시를 지정하지 않으면 RocksDB 는
**DB 마다 자기 블록 캐시를 따로** 만든다. 레이어가 30 개면 캐시도 30 개가 되어 메모리 상한이 사라진다.
이 서버는 LRU 캐시 하나를 만들어 모든 DB 에 물려주므로, 레이어 수와 무관하게
캐시 메모리 총량이 `BlockCacheMB` 에서 고정된다. 인덱스/필터 블록도 같은 캐시에 넣어 함께 상한을 받게 했다.

대략적인 메모리: `BlockCacheMB` + 레이어당 수 MB + .NET 런타임.

### 파일 디스크립터 (Linux)

RocksDB 는 DB 하나가 SST 파일을 수백 개 열어둔다.

```
레이어 수 × MaxOpenFilesPerDb + 여유분  <  ulimit -n
```

기본 `ulimit -n` 이 1024 인 환경에서는 **레이어 서너 개가 한계**다.
(실측: 레이어 2 개에 216 개 fd 사용.)
`deploy/heliosen-tileserver.service` 는 `LimitNOFILE=65535` 로 잡아뒀다.
기동할 때 한도가 빠듯하면 서버가 직접 경고 로그를 남긴다.

### 없는 타일 캐시

빌더가 기본 옵션으로 DB 를 만들기 때문에 SST 에 **블룸 필터가 없다.**
블룸 필터가 있으면 없는 키를 인덱스도 안 보고 끊을 수 있는데, 없으면 매번 레벨별 인덱스를 뒤져야 한다.
즉 **miss 가 hit 보다 비싸다.** 그런데 지도 클라이언트는 자료 경계 밖 타일을 화면 이동마다 계속 요청한다.
그래서 레이어마다 "없는 타일" 을 기억하는 캐시를 둔다(항목당 8 바이트, 락/할당 없음).

> 빌더 쪽에서 DB 를 만들 때 블룸 필터를 켜면(`BlockBasedTableOptions.SetFilterPolicy`)
> miss 성능이 근본적으로 좋아진다. 이 서버는 없어도 동작하도록 만들어져 있다.

---

## DB 를 바꿔 넣기

### 레이어 추가 / 삭제

폴더를 넣거나 빼면 된다. 감시 + 주기적 재훑기가 잡는다 (실측 1~3 초).

복사가 진행 중인 폴더는 등록하지 않는다. `CURRENT` 가 없는데 `MANIFEST-*` / `*.sst` 가 보이면
"아직 준비 안 된 DB" 로 보고 건너뛰므로, 복사 중에 반쪽짜리로 서비스되는 일이 없다.

### 같은 이름으로 내용만 교체

조회 전용 핸들은 스냅샷을 본다. 그래서 폴더 내용이 바뀌면 **다시 열어야** 한다.
`CURRENT` 의 시각·크기와 `MANIFEST` 이름을 지문으로 삼아 변화를 감지하고 자동으로 다시 연다.

**Linux** — 그냥 바꿔치기하면 된다. 열린 핸들이 있어도 폴더를 지울 수 있고(unlink 의미),
지문이 바뀌면 자동으로 다시 연다. (실측 1 초)

```bash
rm -rf /srv/tiles/aaa && cp -r /staging/aaa /srv/tiles/aaa
```

**Windows** — 열려있는 RocksDB 가 SST 파일을 잠그므로 폴더를 지우거나 이름을 바꿀 수 **없다**.
핸들을 먼저 놓게 해야 한다.

```bash
curl -X POST "http://localhost:7080/admin/detach/aaa?seconds=60"
# 이제 폴더를 교체한다
curl -X POST "http://localhost:7080/admin/attach/aaa"
```

`detach` 는 핸들을 놓고 지정한 시간 동안 그 이름을 다시 등록하지 않는다.
`attach` 를 안 불러도 시간이 지나면 자동으로 돌아온다.

진행 중인 요청은 참조 카운트가 지켜준다. 마지막 요청이 끝난 뒤에 핸들이 닫히므로
부하 중에 떼어내도 안전하다 (아래 검증 참고).

---

## 관리 엔드포인트

`AdminToken` 을 설정하면 `X-Admin-Token` 헤더를 요구한다.
설정하지 않으면 **루프백에서 온 요청만** 받는다(설정 없이 외부에 열리지 않게 하려고).

| 엔드포인트 | 설명 |
|---|---|
| `GET /admin/layers` | 레이어 상태, 포맷, 캐시 사용량 |
| `POST /admin/reload` | 지금 바로 재훑기 |
| `POST /admin/reopen` | 모든 DB 핸들을 강제로 다시 열기 |
| `POST /admin/detach/{layer}?seconds=60` | 핸들을 놓고 잠시 등록 중지 |
| `POST /admin/attach/{layer}` | 떼어낸 레이어 즉시 복귀 |

전부 끄려면 `EnableAdmin: false`.

---

## 배포

빌드하는 OS 와 배포 대상 OS 는 **달라도 된다.** `dotnet publish -r <RID>` 는 크로스 컴파일이라
윈도우에서 리눅스용을, 리눅스에서 윈도우용을 만들 수 있다. 네이티브 RocksDB 도 RID 에 맞는 것이
NuGet 패키지에서 골라져 들어간다.

| 만드는 곳 | 명령 | 결과 |
|---|---|---|
| Windows | `.\publish.ps1` | `publish\win-x64` |
| Windows | `.\publish.ps1 -Rid linux-x64` | `publish\linux-x64` |
| Linux | `./publish-linux.sh` | `publish/linux-x64` |

`publish-linux.sh` 는 bash 스크립트라 PowerShell 에서는 실행되지 않는다.
윈도우에서 리눅스용을 만들려면 `publish.ps1 -Rid linux-x64` 를 쓴다(WSL 도 필요 없다).

### Linux

```bash
./publish-linux.sh
sudo cp -r publish/linux-x64 /opt/heliosen-tileserver
sudo cp deploy/heliosen-tileserver.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now heliosen-tileserver
journalctl -u heliosen-tileserver -f
```

유닛 파일은 조회 전용 서버에 맞춰 강화해뒀다(`ProtectSystem=strict`, 타일 폴더는 `ReadOnlyPaths`).

> **윈도우에서 만들어 옮겼다면 실행 권한을 다시 줘야 한다.**
> NTFS 에는 실행 비트가 없어서 zip / scp 로 옮기면 `-rw-r--r--` 로 떨어지고
> `./Heliosen.TileFileServer` 가 `Permission denied` 로 죽는다.
>
> ```bash
> chmod +x Heliosen.TileFileServer
> ```
>
> chmod 를 못 하는 상황이면 dll 을 직접 실행해도 된다(실행 비트가 필요 없다).
> systemd 유닛의 `ExecStart` 도 이렇게 쓸 수 있다.
>
> ```bash
> dotnet ./Heliosen.TileFileServer.dll
> ```
>
> 리눅스에서 `./publish-linux.sh` 로 직접 만들면 이 문제가 없다(`chmod +x` 까지 해준다).

### Windows

```powershell
.\publish.ps1
```

> `publish.ps1` 은 **UTF-8 BOM 으로 저장해야 한다.**
> Windows PowerShell 5.1 은 BOM 이 없으면 스크립트를 시스템 코드페이지(한국어 Windows = 949)로
> 읽어서 한글 메시지가 `?꾨즺` 처럼 깨진다. PowerShell 7 은 BOM 없이도 UTF-8 로 읽는다.
> 편집기에서 다시 저장할 때 BOM 이 빠지지 않게 주의할 것.
>
> 반대로 `publish-linux.sh` 에는 **BOM 을 넣으면 안 된다.**
> `#!/usr/bin/env bash` 앞에 BOM 이 붙으면 shebang 을 인식하지 못해 실행 자체가 실패한다.

프레임워크 의존이 기본이라 대상 서버에 .NET 10 런타임이 필요하다.
런타임을 깔 수 없으면 `-SelfContained` 를 준다.

배포본에 들어가는 어셈블리 (Windows/Linux 동일):

```
Heliosen.TileFileServer.dll   서버
DTB.RocksTileStore.dll        타일 키 / 포맷 정의 (빌더와 공유)
CCd.IOs.dll, CCd.Core.dll     DTB.RocksTileStore 의 의존성
Polly.dll, Polly.Core.dll     CCd.Core 의 의존성
RocksDbSharp.dll              RocksDB 관리형 바인딩
rocksdb.dll / librocksdb.so   RocksDB 네이티브 (약 12 MB)
```

`DTB.RocksTileStore` 는 `lib/net10.0` 에 IL 어셈블리 하나로 들어있어서(RID 중립)
Windows 와 Linux 에서 같은 파일이 쓰인다. 난독화된 Release 빌드도 리눅스에서 확인했다.

서비스로 돌리려면 **NSSM** 을 권한다. 이 서버는 콘솔 앱이라 Windows 서비스 제어 신호를 직접 처리하지 않는다.

```powershell
nssm install HeliosenTileServer C:\heliosen-tileserver\Heliosen.TileFileServer.exe
nssm set HeliosenTileServer AppEnvironmentExtra TileServer__Root=D:\tiles
nssm start HeliosenTileServer
```

정식 Windows 서비스 지원이 필요하면 `Microsoft.Extensions.Hosting.WindowsServices` 패키지를 추가하고
`Program.cs` 에 `builder.Host.UseWindowsService();` 한 줄을 넣으면 된다.
(패키지를 받을 수 없는 환경도 있어서 기본 의존성에서는 빼뒀다.)

### 앞단에 nginx 를 두는 경우

굳이 필요하진 않지만, TLS 종료나 기존 구성 유지를 위해 앞에 둘 수 있다.

```nginx
location / {
    proxy_pass http://127.0.0.1:7080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;

    # 이 서버가 이미 Content-Encoding 을 정확히 붙인다. 손대지 않는다.
    proxy_set_header Accept-Encoding "";
}
```

---

## 실측 (검증 결과)

Windows 11, Release 빌드, 동시성 64, 로컬 루프백.

| 시나리오 | RPS | p50 | p99 | 실패 |
|---|---|---|---|---|
| hit (있는 타일) | 224,974 | 0.17 ms | 0.78 ms | 0 |
| miss (전부 없는 타일) | 168,847 | 0.23 ms | 1.76 ms | 0 |
| mixed (70% hit) | 172,549 | 0.24 ms | 1.27 ms | 0 |
| **chaos** (부하 중 detach/attach 34 회) | 233,463 | 0.20 ms | 0.93 ms | **0** |

키 인코딩을 `RocksTileStore.EncodeTileKey` 로 바꾸면서 요청마다 10 바이트 배열이 하나 생기지만
(예전에는 스택에 직접 썼다), 위 수치는 바꾸기 전과 측정 오차 범위 안에서 같다.
초당 20 만 요청이면 2 MB/s 인데 gen0 이 그냥 흡수한다.

chaos 는 20 초 동안 468 만 요청을 넣으면서 레이어를 계속 떼었다 붙인 것이다.
참조 카운트가 깨지면 해제된 메모리를 만져서 프로세스가 그대로 죽는데(관리 예외가 아니라 try/catch 로도 못 막는다),
프로세스는 같은 PID 로 살아있었고 오류 로그도 0 줄이었다.

Linux(WSL2 Ubuntu, .NET 10)에서도 같은 바이너리로 전부 확인했다:
타일/부가파일 서비스, gzip 헤더, 304(양쪽 레이어), HEAD, 경로 탈출 차단,
핫 리로드 1 초, **detach 없이 제자리 DB 교체 1 초**, 오류 0 건.

---

## 설계 메모

**참조 카운트로 핸들 수명 관리** — 요청이 RocksDB 를 읽는 중에 `Dispose` 하면 네이티브 크래시다.
카탈로그가 몫 하나를 들고, 요청이 잠깐 하나를 더 든다. 0 이 될 때만 닫는다.
빠른 경로는 `Interlocked` 두 번뿐이라 락 경합이 없다.

**조회 경로에 락이 없다** — 레이어 사전은 `FrozenDictionary` 로 만들어 두고,
갱신할 때 새 사전을 참조 하나만 바꿔 끼운다. 읽는 쪽은 항상 일관된 스냅샷을 본다.

**요청당 할당 최소화** — 타일 키(10 바이트)는 `stackalloc`,
`Cache-Control` 문자열은 기동 때 한 번 만들어 재사용, 404 결과 객체도 재사용한다.
조회는 확장자·포맷을 먼저 정리해서 **항상 한 번**만 한다.

**레이어 하나가 깨져도 나머지는 돈다** — 열기는 첫 요청까지 미루고,
실패하면 그 레이어만 실패로 두고 백오프한다. 재훑기는 어떤 예외도 밖으로 내보내지 않는다.

**루트가 사라져도 버틴다** — 네트워크 마운트가 끊겨서 루트가 안 보일 때
레이어를 전부 내려버리면 마운트가 돌아올 때까지 서비스가 완전히 죽는다.
그래서 경고만 남기고 기존 핸들을 유지한다.

**감시는 보조 수단** — `FileSystemWatcher` 는 네트워크 드라이브·컨테이너 볼륨에서 이벤트를 놓친다.
버퍼가 넘치면 통째로 잃는다. 그래서 실제로 믿는 건 주기적 재훑기이고, 감시는 반응을 빠르게 하는 용도다.

감시 범위는 **루트 + 묶음 폴더를 각각 비재귀로** 잡는다. 셋 중 하나를 골라야 했다.

- 재귀 감시(`IncludeSubdirectories=true`)는 DB 하나 복사에 SST 파일 수천 개 이벤트가 쏟아져
  버퍼를 넘기고, 넘치는 순간 이벤트를 통째로 잃는다.
- 루트만 감시하면 중첩 레이어(`korea/seoul`)의 추가·삭제를 놓친다.
  `korea` 안의 변화는 루트의 변화가 아니라서 주기적 재훑기(기본 30 초)까지 기다려야 한다.
- 묶음 폴더는 안에 DB 파일이 없고 폴더만 있다. 비재귀로 감시하면 폴더 생성/삭제/이름변경만
  몇 건 들어오므로 넘칠 일이 없다. 새 묶음 폴더가 생기면 재훑기가 감시 대상에 추가한다.

주기적 재훑기를 **완전히 끈 상태**(`RescanSeconds: 0`)로 확인했다.
중첩 폴더 안의 레이어 삭제·복귀, 새 묶음 폴더째 추가, 그 안의 레이어 추가 모두 2 초에 잡힌다.
감시 폴더 수는 64 개로 제한한다(핸들 보호).

**포맷 정의는 DTB.RocksTileStore 것을 쓰고, DB 여는 것은 직접 한다** — 이 분담에는 이유가 있다.

포맷 쪽(열거형, `EncodeTileKey`, `IsRocksDBDirectory`, `DbContentsType`, 확장자 판정)은
빌더와 어긋나면 DB 를 아예 못 읽는 부분이라 반드시 한 곳에만 있어야 한다. 그래서 전부 위임했다.
`EncodeTileKey` 가 서버가 쓰던 인코딩과 바이트 단위로 같은지 확인한 뒤에 바꿨다.

반면 DB 를 여는 것은 `RocksTileStore` 를 쓰지 않고 `RocksDb.OpenReadOnly` 를 직접 호출한다.
`RocksTileStore` 의 생성자가 `DbOptions` 를 내부에서 만들고 밖으로 열어주지 않기 때문에,
그걸 쓰면 아래 세 가지를 지정할 방법이 없어진다.

- **공유 블록 캐시** — 없으면 DB 마다 자기 캐시가 생겨서 메모리 상한이 사라진다
- **`MaxOpenFiles`** — 없으면(기본 무제한) 리눅스에서 파일 디스크립터가 마른다
- **`cache_index_and_filter_blocks`** — 없으면 인덱스/필터 블록이 캐시 밖에서 무제한으로 쌓인다

레이어를 수십 개 얹는 서버에서는 이 셋이 OOM 과 "Too many open files" 를 가르는 부분이라
포기할 수 없었다. (`RocksTileStore` 에 옵션을 받는 생성자가 생기면 이쪽도 위임할 수 있다.)

또 하나, 조회에 쓰는 `TileLayerFormatKind` 값은 **바꾸면 기존 DB 를 못 읽는다.**
이제 그 열거형은 빌더 쪽 코드이므로, 거기서 숫자를 바꾸면 양쪽이 함께 깨진다는 뜻이다.

---

## 구조

```
src/
├── Program.cs                     기동, Kestrel/CORS 설정
├── Configuration/
│   ├── TileServerOptions.cs       설정
│   └── ResourceLimits.cs          fd 한도 확인/경고
├── Tiles/
│   └── TileFormat.cs              MIME / gzip 판별 (포맷·키는 DTB.RocksTileStore 위임)
├── Layers/
│   ├── ITileLayer.cs              레이어 인터페이스
│   ├── RocksDbTileLayer.cs        RocksDB 조회
│   ├── FileSystemTileLayer.cs     파일 폴더 조회
│   ├── LayerSlot.cs               참조 카운트 + 지연 열기 + 백오프
│   ├── LayerCatalog.cs            탐색, 핫 리로드, 은퇴
│   ├── LayerProbe.cs              폴더 판정 + 지문
│   ├── RocksDbEnvironment.cs      공유 블록 캐시 / DB 옵션
│   └── NegativeTileCache.cs       없는 타일 캐시
└── Endpoints/
    ├── TileEndpoints.cs           타일 경로
    └── AdminEndpoints.cs          상태/관리 경로

tools/SampleStore/                 시험용 표본 DB 생성기
deploy/                            systemd 유닛
```
