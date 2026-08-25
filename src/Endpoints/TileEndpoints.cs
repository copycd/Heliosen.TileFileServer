using DTB.RocksTileStore;
using Heliosen.TileFileServer.Configuration;
using Heliosen.TileFileServer.Layers;
using Heliosen.TileFileServer.Tiles;

namespace Heliosen.TileFileServer.Endpoints;

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
    /// 레이어 경로 조각의 라우트 키. 요청마다 $"p{i}" 로 문자열을 만들지 않으려고 미리 잡아둔다.
    /// (MaxLayerDepth 를 나중에 올려도 되게 여유를 둔다. 현재 상한은 4 다.)
    /// </summary>
    private static readonly string[] SegmentKeys = ["p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8"];

    public static void Map(WebApplication app, TileServerOptions options)
    {
        var maxDepth = Math.Clamp(options.MaxLayerDepth, 1, SegmentKeys.Length);

        // Cache-Control 값은 프로세스가 사는 동안 고정이다.
        // 요청마다 타입 헤더 객체를 만들어 문자열로 다시 조립하면 그게 그대로 낭비다.
        // 한 번 만들어 두고 헤더에 그 문자열만 꽂는다.
        var cacheControl = BuildCacheControl(options);

        // GET 과 HEAD 를 함께 받는다.
        // MapGet 만 쓰면 HEAD 요청이 405 로 떨어진다. nginx 는 HEAD 를 정상 처리하고,
        // 타일 존재 여부만 확인하려고 HEAD 를 쓰는 클라이언트가 실제로 있다.
        // (본문을 빼는 것은 ASP.NET 의 파일 결과 처리기가 알아서 한다.)
        string[] getAndHead = ["GET", "HEAD"];

        // 레이어 이름이 폴더 여러 단이 될 수 있으므로(korea/seoul) 깊이마다 경로를 등록한다.
        //
        // 포괄 경로(/{**path}) 하나로 받아서 직접 쪼개는 방법도 있지만, 그러면 z/x/y 를
        // 손으로 파싱해야 하고 {z:int} 제약이 주는 조기 거절도 잃는다.
        // 깊이마다 등록하면 깊이 1(대부분의 경우)은 예전과 완전히 같은 경로를 탄다.
        for (var depth = 1; depth <= maxDepth; depth++)
        {
            var prefix = BuildLayerTemplate(depth);
            var captured = depth;

            // 확장자가 있는 타일.
            app.MapMethods($"{prefix}/{{z:int}}/{{x:int}}/{{y:int}}.{{ext}}", getAndHead, (
                int z, int x, int y, string ext,
                LayerCatalog catalog, HttpContext context) =>
                    ServeTile(catalog, cacheControl, context, ReadLayerName(context, captured), captured, z, x, y, ext));

            // 확장자 없는 타일. DB 의 대표 포맷으로 내보낸다.
            app.MapMethods($"{prefix}/{{z:int}}/{{x:int}}/{{y:int}}", getAndHead, (
                int z, int x, int y,
                LayerCatalog catalog, HttpContext context) =>
                    ServeTile(catalog, cacheControl, context, ReadLayerName(context, captured), captured, z, x, y, extension: null));
        }

        // 나머지 전부. layer.json, tilemapresource.xml, tileset.json, 3D Tiles 조각 등.
        // 레이어 이름 길이를 모르니 카탈로그에서 가장 긴 접두사를 찾아 떼어낸다.
        app.MapMethods("/{**path}", getAndHead, (
            string path,
            LayerCatalog catalog, HttpContext context) =>
                ServeBlob(catalog, cacheControl, context, path));
    }

    /// <summary>"/{p1}", "/{p1}/{p2}", ... 형태의 경로 접두사를 만든다.</summary>
    private static string BuildLayerTemplate(int depth)
    {
        var builder = new System.Text.StringBuilder(depth * 6);

        for (var i = 1; i <= depth; i++)
            builder.Append("/{").Append(SegmentKeys[i - 1]).Append('}');

        return builder.ToString();
    }

    /// <summary>
    /// 경로 조각들을 합쳐 레이어 이름을 만든다.
    /// 깊이 1 이면 조각 하나를 그대로 쓰므로 문자열을 새로 만들지 않는다(가장 흔한 경우).
    /// </summary>
    private static string? ReadLayerName(HttpContext context, int depth)
    {
        var values = context.Request.RouteValues;

        if (depth == 1)
            return values[SegmentKeys[0]] as string;

        var builder = new System.Text.StringBuilder(64);

        for (var i = 0; i < depth; i++)
        {
            if (i > 0)
                builder.Append('/');

            if (values[SegmentKeys[i]] is not string segment || segment.Length == 0)
                return null;

            builder.Append(segment);
        }

        return builder.ToString();
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
        string? layerName,
        int layerDepth,
        int z, int x, int y,
        string? extension)
    {
        if (string.IsNullOrEmpty(layerName))
            return NotFound;

        if (!catalog.TryGetSlot(layerName, out var slot))
            return NotFound;

        // 여기서 얻은 권한은 finally 에서 반드시 놓아야 한다.
        // 놓지 않으면 그 레이어의 DB 핸들이 영원히 닫히지 않는다.
        if (!slot.TryAcquire(out var layer, out var openError))
            return openError is null ? NotFound : ServiceUnavailable;

        try
        {
            TilePayload? payload;

            if (layer.ServesByPath)
            {
                // 파일 폴더는 **URL 을 그대로** 파일 경로로 쓴다.
                // 라우트에서 파싱한 z/x/y 로 다시 조립하면 "07" 이 "7" 이 되어
                // 실제로 있는 07/12/99.png 를 못 찾는다. 규칙을 두지 않는 게 요점이다.
                var relative = RelativePathAfterLayer(context.Request.Path, layerDepth);
                payload = relative is null ? null : layer.GetBlob(relative);
            }
            else
            {
                // 임의의 상한은 두지 않는다. 잘못 넣은 좌표는 키가 없으니 그냥 404 다.
                // (키 포맷이 표현할 수 없는 값 - 음수, 레벨 255 초과 - 은 레이어에서 걸러 404 로 만든다.
                //  거기서 안 걸면 (byte)z 가 감싸돌아서 '다른 타일'을 내보내게 된다.)
                payload = LookupTile(layer, z, x, y, extension);
            }

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

    /// <summary>
    /// URL 경로에서 앞의 레이어 이름 조각들을 떼어내고 나머지를 그대로 돌려준다.
    ///
    /// 라우트 값으로 경로를 재조립하지 않는 이유가 이것이다.
    /// {z:int} 는 "07" 을 7 로 바꿔버려서 원래 파일 이름을 잃는다.
    /// Request.Path 는 이미 퍼센트 디코딩된 값이라 그대로 파일 경로로 쓸 수 있다.
    /// </summary>
    private static string? RelativePathAfterLayer(PathString path, int layerDepth)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
            return null;

        // value 는 '/' 로 시작한다. 레이어 이름 조각 수만큼 '/' 를 넘어간다.
        var cut = 0;
        for (var i = 0; i < layerDepth; i++)
        {
            cut = value.IndexOf('/', cut + 1);
            if (cut < 0)
                return null;
        }

        return cut + 1 < value.Length ? value[(cut + 1)..] : null;
    }

    private static TilePayload? LookupTile(ITileLayer layer, int z, int x, int y, string? extension)
    {
        // 타일 포맷으로 아는 확장자면 곧바로 타일 키로 찾는다. 조회 한 번이다.
        if (extension is null || TileFormat.FromExtension(extension) != TileLayerFormatKind.Unknown)
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
        string path)
    {
        if (string.IsNullOrEmpty(path))
            return NotFound;

        // 상위 경로 이동은 무조건 거절한다.
        // (RocksDB 는 키 조회라 애초에 위험하지 않지만, 파일 레이어와 같은 규칙으로 두는 게 낫다.)
        if (path.Contains("..", StringComparison.Ordinal))
            return NotFound;

        // 레이어 이름이 몇 단인지 모르니 가장 긴 접두사부터 맞춰본다.
        if (!catalog.TryResolveBlob(path, out var slot, out var relative))
            return NotFound;

        if (!slot.TryAcquire(out var layer, out var openError))
            return openError is null ? NotFound : ServiceUnavailable;

        try
        {
            var payload = layer.GetBlob(relative);
            return payload is null ? NotFound : Write(context, cacheControl, payload);
        }
        catch (Exception ex)
        {
            LogReadFailure(context, ex, slot.Name, relative);
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
