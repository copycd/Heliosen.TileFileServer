namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// 레이어 하나의 수명을 쥐고 있는 칸.
///
/// 여기서 반드시 지켜야 하는 것이 하나 있다.
/// **요청이 RocksDB 를 읽고 있는 동안 핸들을 Dispose 하면 네이티브 크래시로 프로세스가 죽는다.**
/// 관리되는 예외가 아니라 해제된 메모리 접근이라 try/catch 로도 못 막는다.
/// 폴더가 없어졌다고 곧바로 닫으면 이 사고가 난다.
///
/// 그래서 참조 카운트를 둔다. 카탈로그가 몫 하나(=1)를 들고 시작하고, 요청은 잠깐 하나를 더 든다.
/// 카운트가 0 이 되는 건 "카탈로그가 내려놓았고 + 진행 중인 요청도 없다" 는 뜻이고, 그때만 닫는다.
///
/// 빠른 경로는 Interlocked 두 번뿐이라 락 경합이 없다. 락은 처음 열 때만 잡는다.
/// </summary>
internal sealed class LayerSlot
{
    public string Name { get; }
    public string Path { get; }

    /// <summary>이 칸을 만들 때의 폴더 지문. 재훑기에서 바뀌었으면 DB 가 교체된 것이다.</summary>
    public LayerFingerprint Fingerprint { get; }

    private readonly Func<LayerSlot, ITileLayer> _open;
    private readonly ILogger _log;
    private readonly int _retryMs;
    private readonly object _openGate = new();

    private ITileLayer? _layer;

    /// <summary>카탈로그의 몫 1 에서 시작한다.</summary>
    private int _refCount = 1;

    private int _retired;
    private int _disposed;
    private string? _openError;
    private long _nextRetryTicks;

    public LayerSlot(
        string name,
        string path,
        LayerFingerprint fingerprint,
        Func<LayerSlot, ITileLayer> open,
        int retrySeconds,
        ILogger log)
    {
        Name = name;
        Path = path;
        Fingerprint = fingerprint;
        _open = open;
        _retryMs = Math.Max(1, retrySeconds) * 1000;
        _log = log;
    }

    public bool IsOpen => Volatile.Read(ref _layer) is not null;

    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public string? OpenError => Volatile.Read(ref _openError);

    /// <summary>
    /// 사용 권한을 얻는다. true 를 받았으면 <see cref="Release"/> 를 반드시 호출해야 한다
    /// (try/finally 로 감싸라).
    /// </summary>
    public bool TryAcquire(out ITileLayer layer, out string? error)
    {
        layer = null!;
        error = null;

        if (Volatile.Read(ref _retired) != 0)
            return false;

        var current = Volatile.Read(ref _layer) ?? OpenSlow(out error);
        if (current is null)
            return false;

        // 참조를 먼저 늘리고 그 다음에 은퇴 여부를 다시 확인한다(이중 확인).
        // Retire() 는 _retired 를 세운 뒤에 자기 몫을 놓는다. 따라서 여기서 다시 읽은
        // _retired 가 0 이면, 이 시점 이후로는 아무도 카운트를 0 으로 떨어뜨릴 수 없다.
        Interlocked.Increment(ref _refCount);

        if (Volatile.Read(ref _retired) != 0)
        {
            // 하필 그 사이에 은퇴했다. 방금 읽은 핸들은 이미 닫혔을 수 있으니 쓰지 않는다.
            Release();
            return false;
        }

        layer = current;
        return true;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
            DisposeCore();
    }

    /// <summary>
    /// 더 이상 쓰지 않겠다고 표시하고 카탈로그의 몫을 내려놓는다.
    /// 진행 중인 요청이 없으면 여기서 닫히고, 있으면 마지막 요청이 끝날 때 닫힌다.
    /// </summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return;

        Release();
    }

    /// <summary>기동 워밍업. 여기서 미리 열어보면 문제를 첫 요청이 아니라 로그에서 먼저 본다.</summary>
    public bool TryWarmup(out string? error)
    {
        if (!TryAcquire(out _, out error))
            return false;

        Release();
        return true;
    }

    private ITileLayer? OpenSlow(out string? error)
    {
        error = null;

        // 열기에 실패한 직후에는 잠깐 쉬어간다.
        // 요청마다 RocksDB 를 열려고 달려들면 그 자체로(디스크 IO + 락 경합) 서버가 주저앉는다.
        if (IsBackingOff(out error))
            return null;

        lock (_openGate)
        {
            var existing = Volatile.Read(ref _layer);
            if (existing is not null)
                return existing;

            if (Volatile.Read(ref _retired) != 0)
                return null;

            if (IsBackingOff(out error))
                return null;

            try
            {
                var opened = _open(this);
                Volatile.Write(ref _openError, null);
                Volatile.Write(ref _nextRetryTicks, 0);
                Volatile.Write(ref _layer, opened);
                return opened;
            }
            catch (Exception ex)
            {
                // 레이어 하나가 깨진 것 때문에 서버 전체가 멈추면 안 된다.
                // 이 레이어만 실패로 두고 나머지는 계속 서비스한다.
                Volatile.Write(ref _openError, ex.Message);
                Volatile.Write(ref _nextRetryTicks, Environment.TickCount64 + _retryMs);
                error = ex.Message;

                _log.LogError(ex,
                    "레이어를 열지 못했습니다. Layer={Layer} Path={Path} ({Retry}초 후 다시 시도)",
                    Name, Path, _retryMs / 1000);

                return null;
            }
        }
    }

    private bool IsBackingOff(out string? error)
    {
        var retryAt = Volatile.Read(ref _nextRetryTicks);
        if (retryAt != 0 && Environment.TickCount64 < retryAt)
        {
            error = Volatile.Read(ref _openError);
            return true;
        }

        error = null;
        return false;
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var layer = Interlocked.Exchange(ref _layer, null);
        if (layer is null)
            return;

        try
        {
            layer.Dispose();
            _log.LogInformation("레이어를 닫았습니다. Layer={Layer}", Name);
        }
        catch (Exception ex)
        {
            // 닫다가 실패해도 서비스는 계속 가야 한다.
            _log.LogWarning(ex, "레이어를 닫는 중 오류. Layer={Layer}", Name);
        }
    }

    public LayerDescription Describe()
    {
        var layer = Volatile.Read(ref _layer);
        var error = OpenError;

        var state =
            IsRetired ? "retired" :
            layer is not null ? "open" :
            error is not null ? "failed" :
            "closed";

        return layer?.Describe(state, error) ?? new LayerDescription
        {
            Name = Name,
            Source = "RocksDB",
            Path = Path,
            State = state,
            Error = error,
        };
    }
}
