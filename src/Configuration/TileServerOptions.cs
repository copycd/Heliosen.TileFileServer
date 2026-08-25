namespace Heliosen.TileFileServer.Configuration;

/// <summary>appsettings.json 의 "TileServer" 섹션.</summary>
public sealed class TileServerOptions
{
    public const string SectionName = "TileServer";

    /// <summary>
    /// 레이어 폴더들이 들어있는 루트. 이 밑의 폴더 하나가 URL 첫 조각 하나가 된다.
    ///   {Root}/aaa  ->  /aaa/12/345/4455.jpg
    /// 상대경로면 실행 폴더 기준으로 절대화하고 %ENV% 도 풀어준다.
    /// </summary>
    public string Root { get; set; } = "tiles";

    /// <summary>
    /// 레이어 이름이 가질 수 있는 폴더 깊이. 1 이면 최상위 폴더만 레이어가 된다.
    ///
    ///   MaxLayerDepth = 1 :  {Root}/aaa            -> /aaa/12/345/4455.jpg
    ///   MaxLayerDepth = 2 :  {Root}/korea/seoul    -> /korea/seoul/12/345/4455.jpg
    ///
    /// 루트 바로 밑의 폴더 하나하나가 독립된 "가지" 이고, 가지 안에서는 이 규칙을 따른다.
    ///  - 폴더가 RocksDB 면 그게 마지막이다. **DB 안으로는 절대 들어가지 않는다.**
    ///  - 가지 안에 RocksDB 가 하나라도 있으면 그 가지는 통째로 RocksDB 가지다.
    ///    중간 폴더는 이름만 빌려주는 묶음 폴더가 되고, 그 가지의 파일 폴더는 서비스하지 않는다.
    ///  - 가지 안에 RocksDB 가 전혀 없으면 가지의 맨 윗 폴더 하나가 파일 레이어다.
    ///
    /// 즉 한 가지는 RocksDB 아니면 파일, 한 종류로만 쓴다.
    /// 마지막 항목 덕분에 z/x/y 로 된 파일 타일 폴더가 레벨별로 쪼개지지 않는다.
    ///
    /// 또 이름이 숫자뿐인 폴더는 타일 레벨(z)로 보고 파고들지 않는다.
    /// 그게 없으면 파일 타일 폴더를 훑을 때마다 z/x 폴더 수만큼 디스크를 뒤진다.
    /// </summary>
    public int MaxLayerDepth { get; set; } = 3;

    /// <summary>
    /// 루트를 다시 훑는 주기(초). 0 이면 끈다.
    ///
    /// FileSystemWatcher 는 네트워크 드라이브 / 컨테이너 볼륨 / NFS 에서 이벤트를 놓치는 일이 흔하다.
    /// 그래서 감시는 "빠르게 반응하는 수단"으로만 쓰고, 실제로 믿는 건 이 주기적 재훑기다.
    /// </summary>
    public int RescanSeconds { get; set; } = 30;

    /// <summary>루트 폴더 감시로 변경을 즉시 감지할지. 실패해도 주기적 재훑기가 받쳐준다.</summary>
    public bool WatchFileSystem { get; set; } = true;

    /// <summary>
    /// 열려있는 모든 RocksDB 가 **함께 쓰는** 블록 캐시 크기(MB).
    ///
    /// 이걸 지정하지 않으면 RocksDB 는 DB 마다 자기 캐시를 따로 만든다.
    /// 레이어가 수십 개면 메모리가 그만큼 배로 늘어나서 상한이 없어진다.
    /// 하나를 공유하면 레이어 수와 무관하게 캐시 메모리 총량이 여기서 고정된다.
    /// </summary>
    public int BlockCacheMB { get; set; } = 512;

    /// <summary>
    /// DB 하나가 동시에 열어둘 SST 파일 수 상한. -1 은 무제한.
    ///
    /// 리눅스에서 레이어가 많으면 무제한이 파일 디스크립터를 바로 말려버린다
    /// (DB 하나가 수백 개를 열 수 있다). 상한을 두면 대신 열고 닫는 비용이 조금 붙는다.
    /// </summary>
    public int MaxOpenFilesPerDb { get; set; } = 256;

    /// <summary>
    /// "없는 타일" 을 기억해두는 레이어별 항목 수. 0 이면 끈다.
    ///
    /// 빌더가 기본 옵션으로 DB 를 만들기 때문에 SST 에 블룸 필터가 없다.
    /// 블룸 필터가 없으면 없는 키 조회를 미리 끊을 수 없어서 매번 인덱스를 뒤져야 한다.
    /// 지도 클라이언트는 자료 경계 밖 타일을 쉬지 않고 요청하므로 이 캐시가 실제로 잘 먹는다.
    /// </summary>
    public int NegativeCacheEntries { get; set; } = 100_000;

    /// <summary>Cache-Control 의 max-age(초). 0 이면 헤더를 안 붙인다.</summary>
    public int CacheMaxAgeSeconds { get; set; } = 3600;

    /// <summary>
    /// Cache-Control 에 immutable 을 붙인다.
    /// 타일 내용이 절대 안 바뀌는 배포라면 켜라. 클라이언트가 재검증조차 안 한다.
    /// </summary>
    public bool CacheImmutable { get; set; }

    /// <summary>
    /// 요청 확장자가 DB 에 없는 포맷이면 DB 의 실제 포맷으로 대신 찾아준다.
    /// (jpg 로 만든 DB 에 .png 로 요청이 와도 타일이 나간다. Content-Type 은 실제 바이트에 맞춰 보낸다.)
    /// 끄면 nginx 처럼 딱 맞지 않으면 404 다.
    /// </summary>
    public bool ExtensionFallback { get; set; } = true;

    /// <summary>RocksDB 체크섬 검증. 끄면 조금 빨라지지만 손상된 블록을 그대로 내보낼 수 있다.</summary>
    public bool VerifyChecksums { get; set; } = true;

    /// <summary>레이어 열기에 실패했을 때 다시 시도하기까지 기다리는 초. 매 요청마다 재시도하는 걸 막는다.</summary>
    public int OpenRetrySeconds { get; set; } = 30;

    /// <summary>기동할 때 모든 레이어를 미리 열어본다. 문제를 첫 요청이 아니라 로그에서 먼저 보게 된다.</summary>
    public bool WarmupOnStart { get; set; } = true;

    /// <summary>
    /// 레이어에서 내려간 DB 핸들을 실제로 닫기 전에 기다리는 초.
    ///
    /// 진행 중인 요청은 참조 카운트로 이미 보호되지만, 폴더가 잠깐 사라졌다 돌아오는
    /// (rsync, 재배포) 경우에 핸들을 곧바로 버리지 않도록 하는 여유분이다.
    /// </summary>
    public int RetireGraceSeconds { get; set; } = 10;

    /// <summary>이름이 여기 있으면 레이어로 만들지 않는다.</summary>
    public string[] IgnoreFolders { get; set; } = [];

    /// <summary>CORS 허용. 웹 지도 클라이언트가 다른 오리진에서 붙으려면 필요하다.</summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>비우면 모든 오리진 허용(자격증명 없음). 채우면 그 오리진만 허용한다.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>/admin/* 사용 여부.</summary>
    public bool EnableAdmin { get; set; } = true;

    /// <summary>
    /// 채우면 /admin/* 이 X-Admin-Token 헤더를 요구한다.
    /// 비워두면 루프백에서 온 요청만 받는다(외부에 그냥 열지 않기 위한 기본값).
    /// </summary>
    public string? AdminToken { get; set; }

    /// <summary>실행 폴더 기준으로 Root 를 절대경로로 만든다.</summary>
    public string ResolveRoot(string contentRootPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(Root) ? "tiles" : Root);

        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(contentRootPath, expanded));
    }

    /// <summary>말이 안 되는 값을 안전한 값으로 끌어올린다. 설정 오타로 서버가 죽지 않게.</summary>
    public void Normalize()
    {
        // 라우팅에서 깊이마다 경로를 등록하므로 상한을 둔다.
        MaxLayerDepth = Math.Clamp(MaxLayerDepth, 1, 4);

        if (BlockCacheMB < 8) BlockCacheMB = 8;
        if (MaxOpenFilesPerDb == 0 || MaxOpenFilesPerDb < -1) MaxOpenFilesPerDb = 256;
        if (NegativeCacheEntries < 0) NegativeCacheEntries = 0;
        if (CacheMaxAgeSeconds < 0) CacheMaxAgeSeconds = 0;
        if (OpenRetrySeconds < 1) OpenRetrySeconds = 1;
        if (RescanSeconds < 0) RescanSeconds = 0;
        if (RetireGraceSeconds < 0) RetireGraceSeconds = 0;
        IgnoreFolders ??= [];
        AllowedOrigins ??= [];
    }
}
