using Heliosen.TileFileServer;
using Heliosen.TileFileServer.Configuration;
using Heliosen.TileFileServer.Endpoints;
using Heliosen.TileFileServer.Layers;

// 로그를 UTF-8 로 내보낸다.
//
// 윈도우에서 stdout 을 파일로 받으면(NSSM, `> log.txt`) .NET 이 콘솔 코드페이지로 기록한다.
// 한국어 윈도우면 CP949 가 되어서, UTF-8 을 전제하는 편집기·수집기에서 한글이 깨져 보인다.
// 여기서 한 번 못박아 두면 윈도우/리눅스 로그가 같은 인코딩으로 남는다.
try
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (Exception)
{
    // 콘솔이 없는 환경(서비스로 실행 등)에서는 설정하지 못할 수 있다. 로그 인코딩뿐이라 그냥 넘어간다.
}

Console.WriteLine($"Heliosen TileFileServer {Define.Version}");

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 설정
// ---------------------------------------------------------------------------
var options = builder.Configuration
    .GetSection(TileServerOptions.SectionName)
    .Get<TileServerOptions>() ?? new TileServerOptions();

options.Normalize();
builder.Services.AddSingleton(options);

// ---------------------------------------------------------------------------
// Kestrel
// ---------------------------------------------------------------------------
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // 응답마다 Server 헤더를 붙일 이유가 없다. 바이트도 아끼고 버전도 숨긴다.
    kestrel.AddServerHeader = false;

    // 타일 요청은 본문이 없다. 잘못된/악의적인 큰 본문을 일찍 끊는다.
    kestrel.Limits.MaxRequestBodySize = 0;

    // 헤더만 보내고 붙어있는 연결이 쌓이지 않게.
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});

// ---------------------------------------------------------------------------
// CORS
// ---------------------------------------------------------------------------
const string CorsPolicy = "TileCors";

if (options.EnableCors)
{
    builder.Services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy =>
    {
        policy.AllowAnyHeader().WithMethods("GET", "HEAD", "OPTIONS");

        if (options.AllowedOrigins.Length > 0)
            policy.WithOrigins(options.AllowedOrigins);
        else
            policy.AllowAnyOrigin();
    }));
}

// ---------------------------------------------------------------------------
// 레이어
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<RocksDbEnvironment>();
builder.Services.AddSingleton<LayerCatalog>();
builder.Services.AddHostedService<LayerCatalogWorker>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// 기동
// ---------------------------------------------------------------------------
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var catalog = app.Services.GetRequiredService<LayerCatalog>();

// 여기서 첫 훑기와 워밍업이 끝난다. 그래서 app.Run() 이후의 첫 요청부터 이미 빠르다.
catalog.Start();

log.LogInformation(
    "타일 루트={Root} 레이어={Count}개 블록캐시={CacheMB}MB DB당최대열기={MaxOpen} 재훑기={Rescan}초",
    catalog.Root,
    catalog.Count,
    options.BlockCacheMB,
    options.MaxOpenFilesPerDb,
    options.RescanSeconds);

// 리눅스에서 레이어가 많을 때 가장 먼저 터지는 것이 파일 디스크립터다. 미리 알려준다.
ResourceLimits.WarnIfTight(log, options, catalog.Count);

if (options.EnableCors)
    app.UseCors(CorsPolicy);

AdminEndpoints.Map(app, options, Define.Version);

// 타일 경로는 반드시 마지막에 등록한다. 포괄 경로(/{layer}/{**path})가
// /healthz, /admin/* 같은 고정 경로를 삼키지 않게 하기 위해서다.
// (라우팅 우선순위상 고정 경로가 이기지만, 등록 순서까지 맞춰두면 읽는 사람이 헷갈리지 않는다.)
TileEndpoints.Map(app, options);

app.Run();
