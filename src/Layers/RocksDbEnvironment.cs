using Heliosen.TileFileServer.Configuration;
using RocksDbSharp;

namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// 열려있는 모든 RocksDB 가 공유하는 자원. 싱글턴으로 하나만 산다.
///
/// 핵심은 **블록 캐시를 하나만 만들어 전부에게 물려주는 것**이다.
/// 빌더(DTB.RocksTileStore)는 DB 를 기본 옵션으로 만들기 때문에,
/// 우리가 캐시를 지정하지 않으면 RocksDB 가 DB 마다 자기 캐시를 따로 만든다.
/// 레이어가 30 개면 캐시도 30 개가 되어 메모리 상한이 사라진다.
/// 하나를 공유하면 레이어 수와 무관하게 캐시 총량이 설정값에서 고정된다.
/// </summary>
public sealed class RocksDbEnvironment
{
    private readonly TileServerOptions _options;

    /// <summary>
    /// 네이티브 LRU 캐시 핸들을 감싼 객체.
    /// **반드시 강한 참조로 살려둬야 한다** - 파이널라이저가 돌면 네이티브 캐시를 놓는다.
    /// </summary>
    private readonly Cache _blockCache;

    public RocksDbEnvironment(TileServerOptions options)
    {
        _options = options;
        _blockCache = Cache.CreateLru((ulong)options.BlockCacheMB * 1024UL * 1024UL);
    }

    public int BlockCacheMB => _options.BlockCacheMB;

    /// <summary>현재 블록 캐시가 실제로 쓰고 있는 바이트. 진단용.</summary>
    public ulong BlockCacheUsedBytes
    {
        get
        {
            try { return _blockCache.GetUsage(); }
            catch { return 0; }
        }
    }

    /// <summary>
    /// DB 를 열 때 쓸 옵션. DB 마다 새로 만들지만 블록 캐시만은 공유한다.
    /// (RocksDB 는 열 때 옵션 내용을 복사해가므로 옵션 객체 자체를 돌려쓸 필요는 없다.)
    /// </summary>
    public DbOptions CreateDbOptions()
    {
        var table = new BlockBasedTableOptions()
            .SetBlockCache(_blockCache)

            // 인덱스/필터 블록도 위 캐시에 넣어서 함께 상한을 받게 한다.
            // 끄면 이 블록들이 캐시 밖에서 무제한으로 쌓인다(레이어가 많을 때 이게 주범이다).
            .SetCacheIndexAndFilterBlocks(true)

            // L0 의 인덱스/필터는 자주 쓰이니 캐시에서 밀려나지 않게 고정한다.
            .SetPinL0FilterAndIndexBlocksInCache(true);

        var options = new DbOptions()
            .SetCreateIfMissing(false)
            .SetBlockBasedTableFactory(table)

            // 리눅스에서 레이어가 많을 때 파일 디스크립터가 말라버리는 걸 막는다.
            .SetMaxOpenFiles(_options.MaxOpenFilesPerDb)

            // 조회 전용이라 열 때 통계를 갱신할 이유가 없다. 큰 DB 의 기동 시간이 확 줄어든다.
            .SkipStatsUpdateOnOpen(true)

            // 타일 조회는 무작위 접근이다. 미리읽기(readahead)가 오히려 낭비다.
            .SetAdviseRandomOnOpen(false)

            // RocksDB 자체 LOG 가 무한히 쌓이지 않게.
            .SetInfoLogLevel(InfoLogLevel.Warn)
            .SetKeepLogFileNum(4);

        return options;
    }

    /// <summary>타일 조회에 쓸 읽기 옵션. 모든 조회가 이걸 공유한다(내용을 안 바꾸므로 안전).</summary>
    public ReadOptions CreateReadOptions() =>
        new ReadOptions()
            .SetVerifyChecksums(_options.VerifyChecksums)
            .SetFillCache(true);

    /// <summary>DB 를 훑을 때(포맷 탐지 등) 쓸 옵션. 블록 캐시를 더럽히지 않는다.</summary>
    public ReadOptions CreateScanOptions() =>
        new ReadOptions()
            .SetVerifyChecksums(_options.VerifyChecksums)
            .SetFillCache(false);
}
