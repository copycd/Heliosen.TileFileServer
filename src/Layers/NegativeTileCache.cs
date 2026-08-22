using Heliosen.TileFileServer.Tiles;

namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// "이 좌표에 타일이 없다" 를 기억한다.
///
/// 왜 필요한가:
/// 빌더가 DB 를 기본 옵션으로 만들기 때문에 SST 에 **블룸 필터가 없다**.
/// 블룸 필터가 있으면 없는 키를 인덱스도 안 보고 바로 끊을 수 있는데, 없으면
/// 레벨마다 인덱스를 이진탐색해서 "정말 없다" 를 확인해야 한다. 즉 miss 가 hit 보다 비싸다.
/// 그런데 지도 클라이언트는 자료 경계 밖 타일을 화면 이동마다 계속 요청한다.
/// 그 반복 요청을 여기서 끊는다.
///
/// 정확한 LRU 가 아니다. 상한만 지키면 되고, 넘으면 통째로 비운다.
/// 어차피 "없다" 는 정보라 잃어도 손해가 없고(다음에 다시 조회할 뿐),
/// 이렇게 하면 조회 경로에 락도 할당도 없다.
/// </summary>
internal sealed class NegativeTileCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, bool> _missing;
    private readonly int _capacity;

    /// <summary>ConcurrentDictionary.Count 는 모든 락을 잡아서 비싸다. 따로 센다.</summary>
    private int _count;

    public NegativeTileCache(int capacity)
    {
        _capacity = capacity;
        _missing = new System.Collections.Concurrent.ConcurrentDictionary<long, bool>(
            concurrencyLevel: Environment.ProcessorCount,
            capacity: Math.Min(capacity, 4096));
    }

    public long Count => Volatile.Read(ref _count);

    public bool Contains(TileFormatKind kind, byte level, uint col, uint row) =>
        TryPack(kind, level, col, row, out var key) && _missing.ContainsKey(key);

    public void Add(TileFormatKind kind, byte level, uint col, uint row)
    {
        if (!TryPack(kind, level, col, row, out var key))
            return;

        if (Volatile.Read(ref _count) >= _capacity)
        {
            // 상한 초과. 통째로 비운다.
            _missing.Clear();
            Volatile.Write(ref _count, 0);
        }

        if (_missing.TryAdd(key, true))
            Interlocked.Increment(ref _count);
    }

    public void Clear()
    {
        _missing.Clear();
        Volatile.Write(ref _count, 0);
    }

    /// <summary>
    /// (kind, level, col, row) 를 long 하나에 담는다. 할당도 박싱도 없다.
    ///   kind 8bit | level 8bit | col 24bit | row 24bit
    /// col/row 24 비트는 레벨 23 까지 커버한다. 그보다 크면 캐시하지 않는다(그럴 일은 없다).
    /// </summary>
    private static bool TryPack(TileFormatKind kind, byte level, uint col, uint row, out long packed)
    {
        if (col > 0xFFFFFF || row > 0xFFFFFF)
        {
            packed = 0;
            return false;
        }

        packed = ((long)(byte)kind << 56)
               | ((long)level << 48)
               | ((long)col << 24)
               | row;
        return true;
    }
}
