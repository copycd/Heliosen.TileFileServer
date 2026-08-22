using Heliosen.TileServer.Tiles;
using Microsoft.Net.Http.Headers;

namespace Heliosen.TileServer.Layers;

/// <summary>
/// RocksDB 가 아닌 그냥 파일 폴더를 서비스한다. nginx 로 타일을 내보내던 방식 그대로다.
///
/// 바이트를 읽어서 넘기지 않고 **파일 경로만** 넘긴다.
/// 그러면 ASP.NET 이 커널 sendfile 로 바로 내보내므로, 파일 내용이 관리 힙을 거치지 않는다.
///
/// 단, ETag 와 Last-Modified 는 여기서 직접 만들어 넘겨야 한다.
/// Results.File 은 그것들을 알아서 채워주지 않는다(정적 파일 미들웨어가 하던 일이다).
/// 안 넘기면 조건부 요청이 전부 200 으로 떨어져서 매번 본문을 다시 보낸다.
/// </summary>
internal sealed class FileSystemTileLayer : ITileLayer
{
    /// <summary>확장자 없이 요청이 왔을 때 찾아볼 순서.</summary>
    private static readonly string[] ProbeExtensions = ["jpg", "png", "terrain", "pbf"];

    /// <summary>경로에 들어오면 안 되는 문자들. SearchValues 는 벡터화되어 있어 문자 하나씩 보는 것보다 빠르다.</summary>
    private static readonly System.Buffers.SearchValues<char> ForbiddenChars =
        System.Buffers.SearchValues.Create("\0|*?<>\"");

    private readonly string _root;

    /// <summary>루트 밖으로 나가는지 검사할 때 쓸 접두사. 뒤에 구분자를 붙여둔다.</summary>
    private readonly string _rootPrefix;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Name { get; }
    public string Source => "FileSystem";
    public string Path => _root;

    private FileSystemTileLayer(string name, string root)
    {
        Name = name;
        _root = root;
        _rootPrefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;
    }

    public static FileSystemTileLayer Open(string name, string path, ILogger log)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"폴더가 없습니다: {path}");

        var full = System.IO.Path.GetFullPath(path);
        log.LogInformation("파일 레이어를 열었습니다. Layer={Layer} Path={Path}", name, full);

        return new FileSystemTileLayer(name, full);
    }

    public TilePayload? GetTile(int z, int x, int y, string? extension)
    {
        if (z < 0 || x < 0 || y < 0)
            return null;

        if (!string.IsNullOrEmpty(extension))
            return Resolve($"{z}/{x}/{y}.{extension}");

        // 확장자를 안 준 요청은 흔한 것들을 순서대로 찾아본다.
        foreach (var ext in ProbeExtensions)
        {
            var found = Resolve($"{z}/{x}/{y}.{ext}");
            if (found is not null)
                return found;
        }

        return null;
    }

    public TilePayload? GetBlob(string relativePath) => Resolve(relativePath);

    private TilePayload? Resolve(string relativePath)
    {
        var full = ResolveSafePath(relativePath);
        if (full is null)
            return null;

        // File.Exists 로 확인하고 다시 stat 하면 syscall 이 두 번이다. FileInfo 한 번으로 끝낸다.
        var info = new FileInfo(full);
        if (!info.Exists)
            return null;

        // HTTP 날짜 헤더는 초 단위다. 밀리초를 남겨두면 If-Modified-Since 비교가 영원히 안 맞아서
        // 304 가 한 번도 안 나간다.
        var utc = info.LastWriteTimeUtc;
        var lastModified = new DateTimeOffset(utc, TimeSpan.Zero)
            .AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));

        var contentType = TileFormat.ContentTypeForPath(relativePath);

        // 여기서는 바이트를 읽지 않으므로 gzip 매직 넘버를 볼 수 없다.
        // 파일 방식은 nginx 관례대로 .gz 접미사로만 판단한다.
        var gzip = relativePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

        return new TilePayload
        {
            PhysicalPath = full,
            ContentType = contentType,
            ContentEncoding = gzip ? "gzip" : null,

            // Results.File 은 ETag / Last-Modified 를 알아서 채워주지 않는다
            // (그건 정적 파일 미들웨어가 하는 일이다). 안 넘기면 304 가 아예 안 나가고
            // 매 요청마다 본문을 다시 보내게 된다. 그래서 여기서 직접 만들어 넘긴다.
            // 방식은 정적 파일 미들웨어와 같다: (수정시각, 길이).
            LastModified = lastModified,
            ETag = new EntityTagHeaderValue($"\"{lastModified.ToFileTime():x}-{info.Length:x}\""),
        };
    }

    /// <summary>
    /// 사용자가 준 상대경로를 루트 안쪽 절대경로로 바꾼다. 조금이라도 수상하면 null 이다.
    ///
    /// 경로 탈출은 이 서버에서 유일하게 남는 실질적 공격면이다.
    /// (레이어 이름은 카탈로그 사전 조회라 애초에 조립되지 않는다.)
    /// 그래서 문자 검사와 정규화 후 접두사 검사를 둘 다 한다.
    /// </summary>
    private string? ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        // 절대경로, UNC, 드라이브 지정, 상위 이동, 윈도우 대체 데이터 스트림을 먼저 끊는다.
        if (relativePath.Contains("..", StringComparison.Ordinal))
            return null;

        if (relativePath.Contains(':', StringComparison.Ordinal))
            return null;

        if (relativePath[0] is '/' or '\\')
            return null;

        // 널 문자 등 경로에 들어갈 수 없는 문자.
        if (relativePath.AsSpan().ContainsAny(ForbiddenChars))
            return null;

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, relativePath));
        }
        catch
        {
            // 경로로 쓸 수 없는 입력. 예외를 밖으로 내보내지 않고 그냥 못 찾은 것으로 처리한다.
            return null;
        }

        // 정규화 뒤에도 반드시 루트 안이어야 한다. 심볼릭 링크 등으로 빠져나가는 경우까지 여기서 걸린다.
        if (!full.StartsWith(_rootPrefix, PathComparison))
            return null;

        return full;
    }

    public LayerDescription Describe(string state, string? error) => new()
    {
        Name = Name,
        Source = Source,
        Path = Path,
        State = state,
        Error = error,
    };

    public void Dispose()
    {
        // 들고 있는 자원이 없다.
    }
}
