using System.Text;
using DTB.RocksTileStore;
using Heliosen.TileFileServer.Configuration;
using Heliosen.TileFileServer.Tiles;
using Microsoft.Net.Http.Headers;
using RocksDbSharp;
using RocksStore = DTB.RocksTileStore.RocksTileStore;

namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// DTB.RocksTileStore 로 만든 RocksDB 폴더 하나를 서비스한다.
///
/// **조회 전용으로 연다(OpenReadOnly).** 이유가 두 가지다.
///  - 일반 Open 은 열기만 해도 원본을 바꾼다(WAL 복구, MANIFEST/CURRENT 재작성, LOG 회전).
///    서비스는 읽기만 하니 원본을 건드릴 이유가 없다.
///  - 일반 Open 은 폴더 락을 잡아서, 빌더가 아직 쓰고 있는 DB 를 열 수 없고
///    서버를 두 개 띄우지도 못한다. 조회 전용은 락을 잡지 않아 nginx 처럼 얼마든지 겹칠 수 있다.
///
/// 대신 조회 전용 핸들은 **열린 시점의 내용만 본다**. 열고 난 뒤에 빌더가 추가한 타일은 안 보인다.
/// 그래서 DB 를 갈아끼우면 카탈로그가 핸들을 다시 열어야 하고, 이 점이 ETag 를 싸게 만드는 근거도 된다
/// (핸들이 사는 동안 내용이 불변이므로 좌표+길이만으로 강한 ETag 를 만들 수 있다).
/// </summary>
internal sealed class RocksDbTileLayer : ITileLayer
{
    private readonly RocksDb _db;
    private readonly ReadOptions _readOptions;
    private readonly TileServerOptions _options;
    private readonly NegativeTileCache? _negative;

    private readonly DateTimeOffset _lastModified;
    private readonly string _etagSeed;

    /// <summary>DB 에 들어있는 포맷들의 비트마스크. (1u &lt;&lt; (byte)kind)</summary>
    private readonly uint _kindMask;

    public string Name { get; }
    public string Source => "RocksDB";
    public string Path { get; }

    /// <summary>DB 의 대표 포맷. 확장자 없는 요청과 확장자 대체에 쓴다.</summary>
    public TileLayerFormatKind PrimaryKind { get; }

    /// <summary>빌더가 넣어둔 "ContentsType" 값(TileRaster / TileTerrain / 3DTiles). 없을 수도 있다.</summary>
    public string? ContentsType { get; }

    /// <summary>타일 키가 하나도 없는 DB(3D Tiles 처럼 경로 문자열 키만 쓰는 저장소).</summary>
    public bool HasTileKeys => PrimaryKind != TileLayerFormatKind.Unknown;

    private RocksDbTileLayer(
        string name,
        string path,
        RocksDb db,
        RocksDbEnvironment env,
        TileServerOptions options)
    {
        Name = name;
        Path = path;
        _db = db;
        _options = options;
        _readOptions = env.CreateReadOptions();

        _kindMask = ProbeKinds(db, env, out var primary);
        PrimaryKind = primary;
        ContentsType = ReadContentsType(db);

        _negative = options.NegativeCacheEntries > 0
            ? new NegativeTileCache(options.NegativeCacheEntries)
            : null;

        // Last-Modified 는 DB 폴더의 마지막 쓰기 시각을 쓴다.
        // 폴더를 갈아끼우면 값이 바뀌므로 클라이언트 캐시가 자연히 무효화된다.
        _lastModified = ReadFolderTimestamp(path);

        // ETag 씨앗. (경로 + 폴더 시각) 으로 만들어서 같은 DB 를 다시 열면 같은 값이 나온다.
        // 즉 서버를 재시작해도 클라이언트가 들고 있던 캐시가 그대로 유효하다.
        _etagSeed = BuildETagSeed(path, _lastModified);
    }

    public static RocksDbTileLayer Open(
        string name,
        string path,
        RocksDbEnvironment env,
        TileServerOptions options,
        ILogger log)
    {
        if (!RocksDBUtils.IsRocksDBDirectory(path))
            throw new DirectoryNotFoundException($"RocksDB 폴더가 아닙니다: {path}");

        var db = RocksDb.OpenReadOnly(env.CreateDbOptions(), path, errorIfLogFileExists: false);

        try
        {
            var layer = new RocksDbTileLayer(name, path, db, env, options);

            log.LogInformation(
                "RocksDB 레이어를 열었습니다(조회 전용). Layer={Layer} Format={Format} Formats={Formats} ContentsType={ContentsType} Path={Path}",
                name,
                layer.PrimaryKind,
                string.Join(",", layer.AvailableKinds()),
                layer.ContentsType ?? "-",
                path);

            if (!layer.HasTileKeys)
            {
                log.LogInformation(
                    "Layer={Layer} 에는 타일 키가 없습니다. 경로 문자열 키만 서비스합니다(3D Tiles 등).",
                    name);
            }

            return layer;
        }
        catch
        {
            // 생성자에서 실패하면 방금 연 핸들이 새어나간다. 확실히 닫고 던진다.
            db.Dispose();
            throw;
        }
    }

    public TilePayload? GetTile(int z, int x, int y, string? extension)
    {
        // 레벨은 키에서 1 바이트다. 범위를 넘으면 조회 자체가 무의미하다.
        if (z < 0 || z > byte.MaxValue || x < 0 || y < 0)
            return null;

        var kind = ResolveKind(TileFormat.FromExtension(extension));
        if (kind == TileLayerFormatKind.Unknown)
            return null;

        return ReadTile(kind, (byte)z, (uint)x, (uint)y);
    }

    /// <summary>
    /// 요청 확장자를 실제로 조회할 포맷으로 바꾼다.
    ///
    /// 여기서 한 번에 정하기 때문에 조회는 **항상 한 번**이다.
    /// (없는 포맷으로 먼저 찾아보고 실패하면 다시 찾는 식으로 하면 miss 비용이 두 배가 된다.
    ///  블룸 필터가 없는 DB 라서 그 두 배가 그대로 두 배로 온다.)
    /// </summary>
    private TileLayerFormatKind ResolveKind(TileLayerFormatKind requested)
    {
        // 확장자가 없으면 DB 의 대표 포맷으로.
        if (requested == TileLayerFormatKind.Unknown)
            return PrimaryKind;

        // DB 에 실제로 있는 포맷이면 그대로.
        if ((_kindMask & (1u << (byte)requested)) != 0)
            return requested;

        // 없는 포맷을 요청했다. 대체를 허용하면 대표 포맷으로 내보낸다.
        // Content-Type 은 실제로 읽어낸 바이트의 포맷을 따라가므로 거짓말을 하지 않는다.
        return _options.ExtensionFallback ? PrimaryKind : TileLayerFormatKind.Unknown;
    }

    private TilePayload? ReadTile(TileLayerFormatKind kind, byte level, uint col, uint row)
    {
        if (_negative is not null && _negative.Contains(kind, level, col, row))
            return null;

        // 키 인코딩은 DB 를 만든 쪽과 반드시 같아야 하므로 DTB.RocksTileStore 것을 그대로 쓴다.
        // 10 바이트 배열이 요청마다 하나 생기지만(스택에 직접 쓰던 예전 방식과의 차이),
        // 포맷 정의가 한 곳에만 있는 값이 그보다 크다. 실측으로도 처리량 차이가 없었다.
        var key = RocksStore.EncodeTileKey(kind, level, col, row);

        var bytes = _db.Get(key, null, _readOptions);

        if (bytes is null || bytes.Length == 0)
        {
            _negative?.Add(kind, level, col, row);
            return null;
        }

        return new TilePayload
        {
            Bytes = bytes,
            ContentType = TileFormat.ContentTypeOf(kind),
            ContentEncoding = DetectEncoding(kind, bytes),
            ETag = MakeETag(kind, level, col, row, bytes.Length),
            LastModified = _lastModified,
        };
    }

    public TilePayload? GetBlob(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return null;

        var bytes = ReadRaw(relativePath);

        // 키는 빌더가 Path.GetRelativePath 로 만든 문자열이 그대로 들어간다.
        // 윈도우에서 만든 DB 의 하위 경로 키는 역슬래시로 저장돼 있어서,
        // URL 형태(슬래시)로 못 찾으면 구분자를 바꿔 한 번 더 본다.
        // 최상위 파일(layer.json)은 구분자가 없으니 첫 번에 맞는다.
        if (bytes is null && relativePath.Contains('/'))
            bytes = ReadRaw(relativePath.Replace('/', '\\'));

        if (bytes is null)
            return null;

        var contentType = TileFormat.ContentTypeForPath(relativePath);
        var gzip = TileFormat.IsGzip(bytes) && !contentType.StartsWith("image/", StringComparison.Ordinal);

        return new TilePayload
        {
            Bytes = bytes,
            ContentType = contentType,
            ContentEncoding = gzip ? "gzip" : null,
            ETag = MakeBlobETag(relativePath, bytes.Length),
            LastModified = _lastModified,
        };
    }

    private byte[]? ReadRaw(string key)
    {
        // 문자열 키는 길어질 수 있으니 적당한 크기까지만 스택을 쓴다.
        if (Encoding.UTF8.GetMaxByteCount(key.Length) <= 512)
        {
            Span<byte> buffer = stackalloc byte[512];
            var written = Encoding.UTF8.GetBytes(key, buffer);
            var result = _db.Get(buffer[..written], null, _readOptions);
            return result is { Length: > 0 } ? result : null;
        }

        var value = _db.Get(Encoding.UTF8.GetBytes(key), null, _readOptions);
        return value is { Length: > 0 } ? value : null;
    }

    private static string? DetectEncoding(TileLayerFormatKind kind, ReadOnlySpan<byte> bytes)
    {
        // jpg/png 는 이미 압축된 포맷이라 gzip 으로 감싸 저장하지 않는다. 검사도 건너뛴다.
        if (!TileFormat.MayBeGzipped(kind))
            return null;

        // terrain(quantized-mesh) 은 빌더가 gzip 으로 기록하는 경우가 있다.
        // 이때 Content-Encoding 을 안 붙이면 Cesium 이 압축된 바이트를 그대로 파싱하다 깨진다.
        // 반대로 압축이 아닌데 붙이면 브라우저가 에러를 낸다. 그래서 매직 넘버로 판단한다.
        return TileFormat.IsGzip(bytes) ? "gzip" : null;
    }

    /// <summary>
    /// DB 에 어떤 포맷의 타일이 들어있는지 알아낸다.
    ///
    /// 키가 [kind][level][col BE][row BE] 이고 RocksDB 가 바이트 사전순으로 정렬하므로,
    /// 포맷 하나마다 Seek 한 번씩(총 5 번)이면 끝난다. 전체를 훑을 필요가 없다.
    /// </summary>
    private static uint ProbeKinds(RocksDb db, RocksDbEnvironment env, out TileLayerFormatKind primary)
    {
        uint mask = 0;
        primary = TileLayerFormatKind.Unknown;

        using var iterator = db.NewIterator(readOptions: env.CreateScanOptions());

        // 반복문 안에서 stackalloc 하면 반복 횟수만큼 스택이 쌓인다. 밖에서 한 번만 잡는다.
        Span<byte> prefix = stackalloc byte[1];

        foreach (var kind in TileFormat.KnownKinds)
        {
            prefix[0] = (byte)kind;

            // [kind] 는 [kind][...] 보다 사전순으로 앞이다.
            // 따라서 Seek 결과는 이 kind 의 첫 타일이거나, 없으면 그 뒤 어딘가다.
            iterator.Seek(prefix);

            if (!iterator.Valid())
                break;

            var key = iterator.Key();
            if (key.Length == RocksStore.KeySize && key[0] == (byte)kind)
            {
                mask |= 1u << (byte)kind;
                if (primary == TileLayerFormatKind.Unknown)
                    primary = kind;
            }
        }

        return mask;
    }

    private static string? ReadContentsType(RocksDb db)
    {
        try
        {
            var value = db.Get(Encoding.UTF8.GetBytes(DbContentsType.HEAD_ContentsType));
            return value is { Length: > 0 } ? Encoding.UTF8.GetString(value) : null;
        }
        catch
        {
            // 있으면 좋은 정보일 뿐이다. 없거나 못 읽어도 서비스에는 아무 영향이 없다.
            return null;
        }
    }

    private static DateTimeOffset ReadFolderTimestamp(string path)
    {
        try
        {
            // CURRENT 는 DB 가 바뀔 때마다 갱신된다. 폴더 시각보다 내용 변경을 잘 반영한다.
            var current = System.IO.Path.Combine(path, "CURRENT");
            var utc = File.Exists(current)
                ? File.GetLastWriteTimeUtc(current)
                : Directory.GetLastWriteTimeUtc(path);

            // HTTP 날짜 헤더는 초 단위다. 밀리초를 잘라내지 않으면
            // If-Modified-Since 비교에서 항상 "더 최신" 으로 보여 304 가 안 나간다.
            return new DateTimeOffset(utc, TimeSpan.Zero).AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static string BuildETagSeed(string path, DateTimeOffset lastModified)
    {
        var hash = new HashCode();
        hash.Add(path, StringComparer.OrdinalIgnoreCase);
        hash.Add(lastModified.UtcTicks);
        return ((uint)hash.ToHashCode()).ToString("x8");
    }

    /// <summary>
    /// 강한 ETag.
    ///
    /// 내용 해시가 아니라 (DB 식별 씨앗 + 좌표 + 길이) 로 만든다.
    /// 조회 전용 핸들은 열린 시점의 스냅샷을 보므로, 이 값들이 같으면 내용도 같다.
    /// 응답마다 수십 KB 를 해시하지 않아도 되니 CPU 가 훨씬 덜 든다.
    /// </summary>
    private EntityTagHeaderValue MakeETag(TileLayerFormatKind kind, byte level, uint col, uint row, int length)
    {
        var tag = string.Concat(
            "\"", _etagSeed, "-",
            ((byte)kind).ToString("x2"), level.ToString("x2"), "-",
            col.ToString("x"), ".", row.ToString("x"), "-",
            length.ToString("x"), "\"");

        return new EntityTagHeaderValue(tag);
    }

    private EntityTagHeaderValue MakeBlobETag(string relativePath, int length)
    {
        var tag = string.Concat(
            "\"", _etagSeed, "-b",
            ((uint)StringComparer.Ordinal.GetHashCode(relativePath)).ToString("x8"), "-",
            length.ToString("x"), "\"");

        return new EntityTagHeaderValue(tag);
    }

    private IEnumerable<string> AvailableKinds()
    {
        foreach (var kind in TileFormat.KnownKinds)
        {
            if ((_kindMask & (1u << (byte)kind)) != 0)
                yield return TileFormat.ExtensionOf(kind);
        }
    }

    public LayerDescription Describe(string state, string? error) => new()
    {
        Name = Name,
        Source = Source,
        Path = Path,
        State = state,
        Format = PrimaryKind == TileLayerFormatKind.Unknown ? null : TileFormat.ExtensionOf(PrimaryKind),
        Formats = AvailableKinds().ToArray(),
        ContentsType = ContentsType,
        Error = error,
        NegativeCacheCount = _negative?.Count,
    };

    public void Dispose()
    {
        // 조회 전용 핸들은 flush 할 것이 없다. 그냥 닫는다.
        _db.Dispose();
    }
}
