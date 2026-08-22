using Heliosen.TileServer.Configuration;
using Heliosen.TileServer.Layers;
using Heliosen.TileServer.Tiles;

namespace Heliosen.TileServer.Endpoints;

/// <summary>
/// 타일 서비스 경로.
///
///   /{layer}/{z}/{x}/{y}.{ext}   타일
///   /{layer}/{z}/{x}/{y}         타일 (DB 의 대표 포맷으로)
///   /{layer}/{경로}              그 밖의 것 (layer.json, tilemapresource.xml, tileset.json, 3D Tiles 조각)
///
/// 라우팅 우선순위상 제약이 붙은 앞의 두 경로가 마지막 포괄 경로보다 먼저 잡힌다.
/// </summary>
internal static class TileEndpoints
{
    /// <summary>상태가 없는 결과라 요청마다 새로 만들 필요가 없다. 404 는 타일 서버에서 가장 흔한 응답이다.</summary>
    private static readonly IResult NotFound = Results.NotFound();

    private static readonly IResult ServiceUnavailable = Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// z 상한. 키에서 레벨은 1 바이트지만 현실적인 상한을 둔다.
    /// x/y 상한은 일부러 넉넉하게 둔다 - 타일 스킴이 여러 가지라(2-base 4326 등)
    /// 지나치게 조이면 정상 요청을 400 으로 막게 된다. 어차피 못 찾으면 404 다.
    /// </summary>
    private const int MaxLevel = 30;

    private const int MaxIndex = 1 << 24;

    public static void Map(WebApplication app, TileServerOptions options)
    {
        // Cache-Control 값은 프로세스가 사는 동안 고정이다.
        // 요청마다 타입 헤더 객체를 만들어 문자열로 다시 조립하면 그게 그대로 낭비다.
        // 한 번 만들어 두고 헤더에 그 문자열만 꽂는다.
        var cacheControl = BuildCacheControl(options);

        // GET 과 HEAD 를 함께 받는다.
        // MapGet 만 쓰면 HEAD 요청이 405 로 떨어진다. nginx 는 HEAD 를 정상 처리하고,
        // 타일 존재 여부만 확인하려고 HEAD 를 쓰는 클라이언트가 실제로 있다.
        // (본문을 빼는 것은 ASP.NET 의 파일 결과 처리기가 알아서 한다.)
        string[] getAndHead = ["GET", "HEAD"];

        // 확장자가 있는 타일.
        app.MapMethods("/{layer}/{z:int}/{x:int}/{y:int}.{ext}", getAndHead, (
            string layer, int z, int x, int y, string ext,
            LayerCatalog catalog, HttpContext context) =>
                ServeTile(catalog, cacheControl, context, layer, z, x, y, ext));

        // 확장자 없는 타일. DB 의 대표 포맷으로 내보낸다.
        app.MapMethods("/{layer}/{z:int}/{x:int}/{y:int}", getAndHead, (
            string layer, int z, int x, int y,
            LayerCatalog catalog, HttpContext context) =>
                ServeTile(catalog, cacheControl, context, layer, z, x, y, extension: null));

        // 나머지 전부. layer.json, tilemapresource.xml, tileset.json, 3D Tiles 조각 등.
        app.MapMethods("/{layer}/{**path}", getAndHead, (
            string layer, string path,
            LayerCatalog catalog, HttpContext context) =>
                ServeBlob(catalog, cacheControl, context, layer, path));
    }

    private static string? BuildCacheControl(TileServerOptions options)
    {
        if (options.CacheMaxAgeSeconds <= 0)
            return null;

        return options.CacheImmutable
            ? $"public, max-age={options.CacheMaxAgeSeconds}, immutable"
            : $"public, max-age={options.CacheMaxAgeSeconds}";
    }

    private static IResult ServeTile(
        LayerCatalog catalog,
        string? cacheControl,
        HttpContext context,
        string layerName,
        int z, int x, int y,
        string? extension)
    {
        if (z < 0 || z > MaxLevel || x < 0 || y < 0 || x >= MaxIndex || y >= MaxIndex)
            return NotFound;

        if (!catalog.TryGetSlot(layerName, out var slot))
            return NotFound;

        // 여기서 얻은 권한은 finally 에서 반드시 놓아야 한다.
        // 놓지 않으면 그 레이어의 DB 핸들이 영원히 닫히지 않는다.
        if (!slot.TryAcquire(out var layer, out var openError))
            return openError is null ? NotFound : ServiceUnavailable;

        try
        {
            var payload = LookupTile(layer, z, x, y, extension);
            return payload is null ? NotFound : Write(context, cacheControl, payload);
        }
        catch (Exception ex)
        {
            // 이 타일 하나만 실패로 끝낸다. 예외를 밖으로 내보내면 요청은 어차피 500 이지만,
            // 여기서 잡으면 어느 레이어의 어느 좌표였는지 로그에 남는다.
            LogReadFailure(context, ex, layerName, $"{z}/{x}/{y}");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            slot.Release();
        }
    }

    private static TilePayload? LookupTile(ITileLayer layer, int z, int x, int y, string? extension)
    {
        // 타일 포맷으로 아는 확장자면 곧바로 타일 키로 찾는다. 조회 한 번이다.
        if (extension is null || TileFormat.FromExtension(extension) != TileFormatKind.Unknown)
            return layer.GetTile(z, x, y, extension);

        // 타일 포맷이 아닌 확장자다(.b3dm, .pnts, .json ...).
        // 경로가 타일처럼 생겼어도 실제로는 경로 문자열 키로 저장된 3D Tiles 조각일 수 있다.
        // 그쪽을 먼저 보고, 없으면 확장자 대체 규칙에 맡긴다.
        return layer.GetBlob($"{z}/{x}/{y}.{extension}")
            ?? layer.GetTile(z, x, y, extension);
    }

    private static IResult ServeBlob(
        LayerCatalog catalog,
        string? cacheControl,
        HttpContext context,
        string layerName,
        string path)
    {
        if (string.IsNullOrEmpty(path))
            return NotFound;

        // 상위 경로 이동은 무조건 거절한다.
        // (RocksDB 는 키 조회라 애초에 위험하지 않지만, 파일 레이어와 같은 규칙으로 두는 게 낫다.)
        if (path.Contains("..", StringComparison.Ordinal))
            return NotFound;

        if (!catalog.TryGetSlot(layerName, out var slot))
            return NotFound;

        if (!slot.TryAcquire(out var layer, out var openError))
            return openError is null ? NotFound : ServiceUnavailable;

        try
        {
            var payload = layer.GetBlob(path);
            return payload is null ? NotFound : Write(context, cacheControl, payload);
        }
        catch (Exception ex)
        {
            LogReadFailure(context, ex, layerName, path);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>
    /// 응답을 만든다.
    ///
    /// 조건부 요청(If-None-Match / If-Modified-Since -> 304), Range(206), HEAD 처리는
    /// 직접 하지 않고 ASP.NET 의 파일 결과 처리기에 맡긴다. ETag 와 Last-Modified 만 넘겨주면
    /// 나머지는 이미 검증된 코드가 규격대로 해준다. 여기가 nginx 와 체감이 갈리는 지점이라
    /// 손으로 만들지 않는 게 맞다.
    /// </summary>
    private static IResult Write(HttpContext context, string? cacheControl, TilePayload payload)
    {
        if (cacheControl is not null)
            context.Response.Headers.CacheControl = cacheControl;

        if (payload.ContentEncoding is not null)
        {
            // 저장된 바이트가 이미 gzip 이다. 다시 압축하지 않고 그대로 내보내면서 헤더만 붙인다.
            context.Response.Headers.ContentEncoding = payload.ContentEncoding;
        }

        if (payload.PhysicalPath is not null)
        {
            // 경로만 넘기면 커널 sendfile 로 나간다.
            // ETag/Last-Modified 는 레이어가 파일 정보에서 만들어 넘겨준 값을 그대로 쓴다
            // (Results.File 이 알아서 채워주지 않는다).
            return Results.File(
                path: payload.PhysicalPath,
                contentType: payload.ContentType,
                fileDownloadName: null,
                lastModified: payload.LastModified,
                entityTag: payload.ETag,
                enableRangeProcessing: true);
        }

        return Results.Bytes(
            contents: payload.Bytes!,
            contentType: payload.ContentType,
            fileDownloadName: null,
            enableRangeProcessing: true,
            lastModified: payload.LastModified,
            entityTag: payload.ETag);
    }

    private static void LogReadFailure(HttpContext context, Exception ex, string layerName, string what)
    {
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(TileEndpoints));

        log.LogError(ex, "조회에 실패했습니다. Layer={Layer} Target={Target}", layerName, what);
    }
}
