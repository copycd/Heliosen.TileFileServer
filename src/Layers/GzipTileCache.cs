using System.Diagnostics.CodeAnalysis;
using DTB.RocksTileStore;
using Heliosen.TileFileServer.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// 응답할 때 gzip 으로 압축한 타일을 들고 있는다. 싱글턴으로 하나만 산다.
///
/// 압축 자체는 싸지만(타일당 0.05~0.17ms), 같은 타일을 요청마다 다시 압축할 이유는 없다.
/// 여기서 꺼내 쓰면 압축뿐 아니라 DB 조회까지 건너뛴다.
///
/// 키에 레이어 이름이 아니라 **레이어 인스턴스 번호**를 쓴다.
/// DB 를 갈아끼우면 카탈로그가 핸들을 다시 열면서 레이어 인스턴스가 새로 생기므로,
/// 옛 DB 로 압축해둔 타일은 다시는 조회되지 않는다(쓰이지 않다가 만료되거나 상한에 밀려 나간다).
/// 조회 전용 핸들은 열린 시점의 스냅샷만 보므로 인스턴스가 같으면 내용도 같다. ETag 와 같은 근거다.
///
/// 블록 캐시와 마찬가지로 레이어 수와 무관하게 총량이 <see cref="TileServerOptions.GzipCacheMB"/> 에서 고정된다.
/// </summary>
internal sealed class GzipTileCache : IDisposable
{
    /// <summary>항목 하나당 키/메타데이터 몫(추정). 상한을 바이트로 셀 때 같이 넣는다.</summary>
    private const int EntryOverhead = 128;

    /// <summary>
    /// 이 시간 동안 안 쓰인 타일은 내보낸다.
    /// 내용이 바뀌어서가 아니라(바뀌지 않는다) 안 쓰는 메모리를 돌려주려는 것이다.
    /// </summary>
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(10);

    private readonly MemoryCache? _cache;

    public GzipTileCache(TileServerOptions options)
    {
        LimitMB = options.GzipCacheMB;

        if (LimitMB > 0)
        {
            _cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = (long)LimitMB * 1024 * 1024,

                // 상태 화면의 사용량/적중 수에 쓴다.
                // 카운터가 스레드마다 따로(ThreadLocal) 있어서 코어 사이에서 캐시 라인을 튕기지 않는다.
                TrackStatistics = true,
            });
        }
    }

    /// <summary>설정한 상한(MB). 0 이면 캐시를 쓰지 않는다.</summary>
    public int LimitMB { get; }

    private readonly record struct Key(long Layer, TileLayerFormatKind Kind, byte Level, uint Col, uint Row);

    public bool TryGet(
        long layer, TileLayerFormatKind kind, byte level, uint col, uint row,
        [NotNullWhen(true)] out byte[]? compressed)
    {
        compressed = null;

        return _cache is not null
            && _cache.TryGetValue(new Key(layer, kind, level, col, row), out compressed)
            && compressed is not null;
    }

    public void Set(long layer, TileLayerFormatKind kind, byte level, uint col, uint row, byte[] compressed)
    {
        _cache?.Set(new Key(layer, kind, level, col, row), compressed, new MemoryCacheEntryOptions
        {
            Size = compressed.Length + EntryOverhead,
            SlidingExpiration = SlidingExpiration,
        });
    }

    /// <summary>진단용. 캐시를 끈 상태면 null.</summary>
    public MemoryCacheStatistics? GetStatistics() => _cache?.GetCurrentStatistics();

    public void Dispose() => _cache?.Dispose();
}
