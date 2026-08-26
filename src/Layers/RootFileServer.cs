using Heliosen.TileFileServer.Tiles;
using Microsoft.Net.Http.Headers;

namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// 루트 밑의 파일을 **일반 웹 서버처럼** 내보낸다. URL 경로가 곧 파일 경로다.
///
/// 레이어 목록에 담지 않는다. 파일은 요청이 올 때 디스크에서 바로 찾으면 되므로
/// 미리 폴더를 훑어 이름을 모아둘 이유가 없다(그게 재훑기 비용의 대부분이었다).
/// 그래서 이건 레이어가 아니라 루트 하나짜리 정적 파일 서버다.
///
/// 바이트를 읽어서 넘기지 않고 **파일 경로만** 넘긴다.
/// 그러면 ASP.NET 이 커널 sendfile 로 바로 내보내므로 파일 내용이 관리 힙을 거치지 않는다.
/// 단 ETag / Last-Modified 는 직접 만들어 넘겨야 한다(Results.File 은 채워주지 않는다).
/// </summary>
internal sealed class RootFileServer
{
    /// <summary>경로에 들어오면 안 되는 문자들. SearchValues 는 벡터화되어 있어 문자 하나씩 보는 것보다 빠르다.</summary>
    private static readonly System.Buffers.SearchValues<char> ForbiddenChars =
        System.Buffers.SearchValues.Create("\0|*?<>\"");

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _root;

    /// <summary>루트 밖으로 나가는지 검사할 때 쓸 접두사. 뒤에 구분자를 붙여둔다.</summary>
    private readonly string _rootPrefix;

    public RootFileServer(string root)
    {
        _root = Path.GetFullPath(root);
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
    }

    public string Root => _root;

    /// <summary>URL 경로를 그대로 파일로 찾는다. 없으면 null.</summary>
    public TilePayload? TryGet(string relativePath)
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

        return new TilePayload
        {
            PhysicalPath = full,
            ContentType = TileFormat.ContentTypeForPath(relativePath),

            // 바이트를 읽지 않으므로 gzip 매직 넘버를 볼 수 없다. nginx 관례대로 .gz 접미사로만 판단한다.
            ContentEncoding = relativePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? "gzip" : null,

            // 방식은 정적 파일 미들웨어와 같다: (수정시각, 길이).
            LastModified = lastModified,
            ETag = new EntityTagHeaderValue($"\"{lastModified.ToFileTime():x}-{info.Length:x}\""),
        };
    }

    /// <summary>
    /// 사용자가 준 상대경로를 루트 안쪽 절대경로로 바꾼다. 조금이라도 수상하면 null 이다.
    ///
    /// 경로 탈출은 이 서버에서 유일하게 남는 실질적 공격면이다.
    /// 문자 검사와 정규화 후 접두사 검사를 둘 다 한다.
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

        if (relativePath.AsSpan().ContainsAny(ForbiddenChars))
            return null;

        // RocksDB 내부 파일은 절대 내보내지 않는다.
        //
        // 등록된 DB 폴더는 카탈로그가 먼저 가져가므로 여기까지 오지 않지만,
        // 복사가 덜 끝난 DB 나 MaxLayerDepth 보다 깊은 곳의 DB 는 등록되지 않은 채 남는다.
        // 그 폴더의 SST 를 그대로 내려받게 두면 DB 전체를 복원할 수 있다.
        if (IsRocksDbInternalFile(relativePath))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_root, relativePath));
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

    /// <summary>RocksDB 가 쓰는 파일 이름인지. 파일 이름만 보므로 디스크를 건드리지 않는다.</summary>
    private static bool IsRocksDbInternalFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath.AsSpan());

        if (name.IsEmpty)
            return false;

        return name.Equals("CURRENT", StringComparison.Ordinal)
            || name.Equals("LOCK", StringComparison.Ordinal)
            || name.Equals("IDENTITY", StringComparison.Ordinal)
            || name.StartsWith("LOG", StringComparison.Ordinal)
            || name.StartsWith("MANIFEST-", StringComparison.Ordinal)
            || name.StartsWith("OPTIONS-", StringComparison.Ordinal)
            || name.EndsWith(".sst", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".blob", StringComparison.OrdinalIgnoreCase);
    }
}
