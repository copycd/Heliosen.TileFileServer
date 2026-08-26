using System.Net;
using Heliosen.TileFileServer.Configuration;
using Heliosen.TileFileServer.Layers;

namespace Heliosen.TileFileServer.Endpoints;

/// <summary>
/// 상태 확인과 운영용 경로.
///
///   /healthz          로드밸런서 헬스체크
///   /                 레이어 목록 (사람이 보는 용)
///   /admin/layers     레이어 상태 (JSON)
///   /admin/reload     지금 바로 다시 훑기
///   /admin/reopen     모든 DB 핸들을 강제로 다시 열기
///
/// /admin/* 은 토큰이 설정돼 있으면 토큰을 요구하고, 없으면 루프백에서 온 요청만 받는다.
/// 아무 설정 없이 외부에 열려버리는 상황을 만들지 않기 위한 기본값이다.
/// </summary>
internal static class AdminEndpoints
{
    public static void Map(WebApplication app, TileServerOptions options, string version)
    {
        // 헬스체크는 HEAD 로 찌르는 로드밸런서가 흔하다.
        string[] getAndHead = ["GET", "HEAD"];

        app.MapMethods("/healthz", getAndHead, () => Results.Text("ok", "text/plain"));

        app.MapMethods("/version", getAndHead, () => Results.Text(version, "text/plain"));

        app.MapMethods("/", getAndHead, (LayerCatalog catalog) => Results.Content(
            BuildIndexHtml(catalog, version),
            "text/html; charset=utf-8"));

        if (!options.EnableAdmin)
            return;

        var admin = app.MapGroup("/admin")
            .AddEndpointFilter((context, next) =>
                IsAuthorized(context.HttpContext, options)
                    ? next(context)
                    : ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden)));

        admin.MapGet("/layers", (LayerCatalog catalog) => Results.Json(new
        {
            root = catalog.Root,
            count = catalog.Count,
            blockCacheMB = catalog.Environment.BlockCacheMB,
            blockCacheUsedMB = catalog.Environment.BlockCacheUsedBytes / (1024 * 1024),
            layers = catalog.Describe(),
        }));

        // 폴더가 추가/삭제된 것을 지금 바로 반영한다.
        admin.MapPost("/reload", (LayerCatalog catalog) =>
        {
            catalog.Rescan();
            return Results.Json(new { reloaded = true, count = catalog.Count });
        });

        // DB 를 같은 폴더에 덮어썼는데 지문이 그대로여서 자동 감지가 안 될 때 쓴다.
        admin.MapPost("/reopen", (LayerCatalog catalog) =>
        {
            catalog.ReopenAll();
            return Results.Json(new { reopened = true, count = catalog.Count });
        });

        // 윈도우에서 DB 폴더를 제자리에 갈아끼우기 위한 것.
        // 열려있는 RocksDB 는 SST 파일을 잠그므로 폴더를 지우거나 이름을 바꿀 수 없다.
        // 떼어내서 핸들을 놓게 하고, 그 사이에 폴더를 교체한다.
        admin.MapPost("/detach/{**layer}", (string layer, int? seconds, LayerCatalog catalog) =>
        {
            var window = seconds is > 0 ? seconds.Value : 60;
            var found = catalog.Detach(layer, window);

            return Results.Json(new
            {
                detached = found,
                layer,
                seconds = window,
                note = found
                    ? "핸들을 놓았습니다. 이제 폴더를 교체하세요. 시간이 지나면 자동으로 다시 등록됩니다."
                    : "그런 이름의 레이어가 없습니다. 그래도 해당 이름은 지정한 시간 동안 등록되지 않습니다.",
            });
        });

        // 떼어낸 레이어를 기다리지 않고 지금 되돌린다.
        admin.MapPost("/attach/{**layer}", (string layer, LayerCatalog catalog) =>
        {
            catalog.Attach(layer);
            return Results.Json(new { attached = true, layer, count = catalog.Count });
        });
    }

    private static bool IsAuthorized(HttpContext context, TileServerOptions options)
    {
        if (!string.IsNullOrEmpty(options.AdminToken))
            return string.Equals(context.Request.Headers["X-Admin-Token"], options.AdminToken, StringComparison.Ordinal);

        // 토큰을 설정하지 않았다면 루프백만 허용한다.
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
            return false;

        return IPAddress.IsLoopback(remote);
    }

    private static string BuildIndexHtml(LayerCatalog catalog, string version)
    {
        var names = catalog.LayerNames();

        var body = new System.Text.StringBuilder();
        body.Append("<!doctype html><meta charset=\"utf-8\">");
        body.Append("<title>Heliosen TileFileServer</title>");
        body.Append("<style>body{font:14px/1.6 system-ui,sans-serif;margin:2rem;max-width:60rem}");
        body.Append("code{background:#f4f4f5;padding:.1rem .3rem;border-radius:3px}");
        body.Append("li{margin:.2rem 0}</style>");
        body.Append("<h1>Heliosen TileFileServer</h1>");
        body.Append("<p>version ").Append(WebUtility.HtmlEncode(version));
        body.Append(" &middot; root <code>").Append(WebUtility.HtmlEncode(catalog.Root)).Append("</code></p>");

        if (names.Count == 0)
        {
            body.Append("<p>인식된 RocksDB 레이어가 없습니다. 루트 폴더 밑에 타일 DB 폴더를 넣으면 자동으로 인식합니다.</p>");
        }
        else
        {
            body.Append("<h2>RocksDB 레이어 ").Append(names.Count).Append("개</h2><ul>");

            foreach (var name in names)
            {
                var encoded = WebUtility.HtmlEncode(name);
                body.Append("<li><code>/").Append(encoded).Append("/{z}/{x}/{y}.{ext}</code></li>");
            }

            body.Append("</ul>");
        }

        // 파일은 목록에 담지 않는다. 왜 안 보이는지 여기서 알려준다.
        body.Append("<h2>파일</h2><p>루트 밑의 파일은 <b>목록에 담지 않고</b> ");
        body.Append("요청이 올 때 디스크에서 바로 찾습니다(일반 웹 서버처럼). ");
        body.Append("URL 경로가 곧 파일 경로라서 미리 훑을 이유가 없고, 그만큼 재훑기도 가벼워집니다.</p>");

        return body.ToString();
    }
}
