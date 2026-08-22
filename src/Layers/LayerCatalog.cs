using System.Collections.Frozen;
using Heliosen.TileFileServer.Configuration;

namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// 루트 폴더를 훑어서 레이어 목록을 만들고 유지한다.
///
/// 조회 경로는 락이 없다. 사전을 <see cref="FrozenDictionary{TKey,TValue}"/> 로 만들어 두고
/// 갱신할 때는 새로 만든 사전을 참조 하나만 바꿔 끼운다(원자적 교체).
/// 읽는 쪽은 언제 읽어도 일관된 스냅샷을 본다.
/// </summary>
internal sealed class LayerCatalog : IDisposable
{
    /// <summary>감시 이벤트가 온 뒤 실제로 훑기까지 기다리는 시간. 복사 중 연달아 오는 이벤트를 뭉친다.</summary>
    private const int DebounceMs = 1000;

    private readonly TileServerOptions _options;
    private readonly RocksDbEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LayerCatalog> _log;

    private readonly object _rescanGate = new();

    /// <summary>서비스에 쓰이는 사전. 교체만 하고 절대 제자리에서 고치지 않는다.</summary>
    private FrozenDictionary<string, LayerSlot> _layers =
        FrozenDictionary<string, LayerSlot>.Empty;

    /// <summary>내려갔지만 아직 닫지 않은 칸들. _rescanGate 로 보호한다.</summary>
    private readonly List<PendingRetire> _pending = [];

    /// <summary>
    /// 잠시 등록을 막아둔 레이어 이름 -> 언제까지. _rescanGate 로 보호한다.
    ///
    /// 윈도우에서는 DB 를 열고 있는 동안 그 폴더를 지우거나 이름을 바꿀 수 없다(핸들이 잠근다).
    /// 그래서 "핸들을 놓고 잠깐 손대지 말고 있어" 라고 지시할 방법이 필요하다.
    /// </summary>
    private readonly Dictionary<string, long> _suppressedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>_suppressedUntil 의 개수. 매 초 락을 잡지 않고 빨리 판단하기 위한 것.</summary>
    private int _suppressedCount;

    private FileSystemWatcher? _watcher;
    private long _rescanDueTicks;
    private long _lastRescanTicks;
    private bool _rootMissingLogged;
    private bool _disposed;

    public string Root { get; }

    public int Count => Volatile.Read(ref _layers).Count;

    public RocksDbEnvironment Environment => _environment;

    public LayerCatalog(
        TileServerOptions options,
        RocksDbEnvironment environment,
        IHostEnvironment hostEnvironment,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _environment = environment;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<LayerCatalog>();

        Root = options.ResolveRoot(hostEnvironment.ContentRootPath);
    }

    /// <summary>기동할 때 한 번. 첫 훑기와 감시 시작.</summary>
    public void Start()
    {
        if (!Directory.Exists(Root))
        {
            // 루트가 없어도 서버는 뜬다. 나중에 만들어지면 재훑기가 잡는다.
            _log.LogWarning("타일 루트 폴더가 없습니다. 만들어지면 자동으로 인식합니다. Root={Root}", Root);
        }

        Rescan();

        if (_options.WatchFileSystem)
            StartWatcher();

        if (_options.WarmupOnStart)
            Warmup();
    }

    public bool TryGetSlot(string name, out LayerSlot slot) =>
        Volatile.Read(ref _layers).TryGetValue(name, out slot!);

    public IReadOnlyList<LayerDescription> Describe()
    {
        var snapshot = Volatile.Read(ref _layers);

        return snapshot.Values
            .Select(static slot => slot.Describe())
            .OrderBy(static d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> LayerNames()
    {
        var snapshot = Volatile.Read(ref _layers);

        return snapshot.Keys
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>감시 이벤트나 관리 요청으로 "곧 훑어달라" 고 표시한다.</summary>
    public void RequestRescan() =>
        Volatile.Write(ref _rescanDueTicks, System.Environment.TickCount64 + DebounceMs);

    /// <summary>
    /// 배경 작업이 1 초마다 부른다. 디바운스된 훑기와 주기적 훑기를 둘 다 여기서 처리한다.
    /// </summary>
    public void Tick()
    {
        if (_disposed)
            return;

        var now = System.Environment.TickCount64;

        // 떼어내기 시간이 끝난 레이어가 있으면 주기를 기다리지 않고 바로 되돌린다.
        // 이게 없으면 RescanSeconds=0 으로 꺼둔 설정에서 떼어낸 레이어가 영구히 안 돌아온다.
        if (Volatile.Read(ref _suppressedCount) > 0 && AnySuppressionExpired(now))
        {
            Rescan();
            return;
        }

        var due = Volatile.Read(ref _rescanDueTicks);
        if (due != 0 && now >= due)
        {
            Volatile.Write(ref _rescanDueTicks, 0);
            Rescan();
            return;
        }

        if (_options.RescanSeconds > 0 &&
            now - Volatile.Read(ref _lastRescanTicks) >= _options.RescanSeconds * 1000L)
        {
            Rescan();
        }
    }

    /// <summary>
    /// 모든 레이어를 강제로 다시 연다. DB 를 제자리에 덮어썼는데 지문이 안 바뀐 경우의 탈출구다.
    /// </summary>
    public void ReopenAll()
    {
        lock (_rescanGate)
        {
            var snapshot = Volatile.Read(ref _layers);

            // 현재 칸 전부를 내려보내고(진행 중인 요청은 참조 카운트가 지켜준다) 빈 사전으로 바꾼다.
            // 바로 다음 줄의 재훑기가 새 칸으로 다시 채운다.
            foreach (var slot in snapshot.Values)
                QueueRetire(slot, graceSeconds: 0);

            Volatile.Write(ref _layers, FrozenDictionary<string, LayerSlot>.Empty);
            _log.LogInformation("모든 레이어를 다시 엽니다. Count={Count}", snapshot.Count);
        }

        Rescan();
    }

    /// <summary>
    /// 레이어를 잠시 떼어낸다. 핸들을 놓고, 지정한 시간 동안 다시 등록하지 않는다.
    ///
    /// 윈도우에서 DB 를 제자리에 갈아끼우려면 이게 필요하다.
    /// 열려있는 RocksDB 는 SST 파일을 잠그기 때문에 폴더를 지우거나 이름을 바꿀 수 없다.
    /// 떼어낸 사이에 폴더를 교체하고, 시간이 지나면 자동으로 다시 등록된다.
    /// </summary>
    public bool Detach(string name, int seconds)
    {
        lock (_rescanGate)
        {
            var current = Volatile.Read(ref _layers);

            _suppressedUntil[name] = System.Environment.TickCount64 + Math.Max(1, seconds) * 1000L;
            Volatile.Write(ref _suppressedCount, _suppressedUntil.Count);

            if (!current.TryGetValue(name, out var slot))
                return false;

            var next = current
                .Where(pair => !string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            Volatile.Write(ref _layers, next.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

            // 진행 중인 요청이 있으면 마지막 요청이 끝날 때 닫힌다. 보통 마이크로초 단위다.
            slot.Retire();

            _log.LogInformation("레이어를 떼어냈습니다. Layer={Layer} {Seconds}초 동안 다시 등록하지 않습니다.",
                name, seconds);

            return true;
        }
    }

    /// <summary>떼어낸 레이어를 지금 바로 되돌린다.</summary>
    public void Attach(string name)
    {
        lock (_rescanGate)
        {
            _suppressedUntil.Remove(name);
            Volatile.Write(ref _suppressedCount, _suppressedUntil.Count);
        }

        _log.LogInformation("레이어 떼어내기를 해제했습니다. Layer={Layer}", name);
        Rescan();
    }

    private bool AnySuppressionExpired(long now)
    {
        lock (_rescanGate)
        {
            foreach (var expiry in _suppressedUntil.Values)
            {
                if (now >= expiry)
                    return true;
            }

            return false;
        }
    }

    /// <summary>절대 예외를 밖으로 내보내지 않는다. 일시적인 IO 오류로 타이머가 죽으면 안 된다.</summary>
    public void Rescan()
    {
        if (_disposed)
            return;

        lock (_rescanGate)
        {
            Volatile.Write(ref _lastRescanTicks, System.Environment.TickCount64);

            // 루트를 못 읽는 상황에서도 떼어내기 만료는 처리해야 한다. 그래서 RescanCore 밖에 둔다.
            PurgeExpiredSuppressions();

            try
            {
                RescanCore();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "레이어 재훑기에 실패했습니다. 기존 목록을 그대로 유지합니다. Root={Root}", Root);
            }

            try
            {
                DrainPending();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "내려간 레이어를 정리하는 중 오류.");
            }
        }
    }

    private void RescanCore()
    {
        if (!Directory.Exists(Root))
        {
            // 루트가 사라졌다. 네트워크 마운트가 잠깐 끊긴 경우일 수 있다.
            // 이때 레이어를 전부 내려버리면 마운트가 돌아올 때까지 서비스가 완전히 죽는다.
            // 지금 열려있는 핸들은 그대로 두고(파일 핸들은 살아있다) 경고만 남긴다.
            if (!_rootMissingLogged)
            {
                _log.LogWarning("타일 루트 폴더를 찾을 수 없습니다. 기존 레이어를 유지합니다. Root={Root}", Root);
                _rootMissingLogged = true;
            }

            return;
        }

        _rootMissingLogged = false;

        var current = Volatile.Read(ref _layers);
        var next = new Dictionary<string, LayerSlot>(current.Count, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<LayerSlot>();

        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(directory);

            if (!IsServableName(name))
                continue;

            // 떼어내기(Detach) 중인 이름은 건너뛴다. 운영자가 폴더를 교체하는 중이다.
            if (_suppressedUntil.ContainsKey(name))
                continue;

            if (!LayerProbe.TryClassify(directory, out var sourceKind, out var fingerprint))
                continue;

            if (next.ContainsKey(name))
            {
                // 리눅스에서는 대소문자만 다른 폴더가 공존할 수 있다.
                // URL 조회는 대소문자를 무시하므로 둘 중 하나만 쓸 수 있다. 먼저 본 것을 쓴다.
                _log.LogWarning(
                    "이름이 대소문자만 다른 폴더가 있습니다. 먼저 찾은 것만 서비스합니다. Name={Name} Path={Path}",
                    name, directory);
                continue;
            }

            if (current.TryGetValue(name, out var existing) && CanReuse(existing, directory, sourceKind, fingerprint))
            {
                next.Add(name, existing);
                seen.Add(existing);
                continue;
            }

            // 내려보내려고 대기 중인 칸이 다시 나타났다면(재배포/rsync 중이었던 경우) 되살린다.
            var revived = TakePending(name, directory, sourceKind, fingerprint);
            if (revived is not null)
            {
                _log.LogInformation("레이어가 다시 나타나 되살렸습니다. Layer={Layer}", name);
                next.Add(name, revived);
                seen.Add(revived);
                continue;
            }

            if (existing is not null)
            {
                _log.LogInformation(
                    "레이어가 바뀌었습니다. 다시 엽니다. Layer={Layer} Source={Source}",
                    name, sourceKind);
            }

            next.Add(name, CreateSlot(name, directory, sourceKind, fingerprint));
        }

        // 이번에 안 보인 칸들은 내려보낸다.
        foreach (var slot in current.Values)
        {
            if (!seen.Contains(slot))
            {
                _log.LogInformation("레이어가 사라졌습니다. Layer={Layer} Path={Path}", slot.Name, slot.Path);
                QueueRetire(slot, _options.RetireGraceSeconds);
            }
        }

        var added = next.Count - seen.Count;
        var removed = current.Count - seen.Count;

        Volatile.Write(ref _layers, next.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

        if (added > 0 || removed > 0)
        {
            _log.LogInformation(
                "레이어 목록을 갱신했습니다. Total={Total} Added={Added} Removed={Removed}",
                next.Count, added, removed);
        }
    }

    /// <summary>같은 폴더, 같은 종류, 같은 지문이면 이미 열어둔 핸들을 계속 쓴다.</summary>
    private static bool CanReuse(
        LayerSlot slot,
        string path,
        LayerSourceKind sourceKind,
        LayerFingerprint fingerprint)
    {
        if (slot.IsRetired)
            return false;

        if (slot.SourceKind != sourceKind)
            return false;

        if (!string.Equals(slot.Path, path, StringComparison.Ordinal))
            return false;

        // RocksDB 는 조회 전용 스냅샷이라, DB 가 교체되면 반드시 다시 열어야 한다.
        // 파일 폴더는 요청마다 디스크를 보므로 지문을 볼 필요가 없다.
        return sourceKind != LayerSourceKind.RocksDb || slot.Fingerprint == fingerprint;
    }

    private LayerSlot CreateSlot(
        string name,
        string path,
        LayerSourceKind sourceKind,
        LayerFingerprint fingerprint)
    {
        // 여는 것은 지연시킨다. 첫 요청(또는 워밍업)에서 열린다.
        // 그래서 레이어가 수백 개라도 기동이 즉시 끝나고, 깨진 DB 하나가 다른 레이어를 막지 않는다.
        return new LayerSlot(
            name,
            path,
            sourceKind,
            fingerprint,
            open: slot => sourceKind switch
            {
                LayerSourceKind.RocksDb => RocksDbTileLayer.Open(
                    slot.Name,
                    slot.Path,
                    _environment,
                    _options,
                    _loggerFactory.CreateLogger<RocksDbTileLayer>()),

                _ => FileSystemTileLayer.Open(
                    slot.Name,
                    slot.Path,
                    _loggerFactory.CreateLogger<FileSystemTileLayer>()),
            },
            _options.OpenRetrySeconds,
            _loggerFactory.CreateLogger<LayerSlot>());
    }

    private bool IsServableName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        // 숨김/작업용 폴더는 건너뛴다. 복사 중인 임시 폴더가 레이어로 잡히는 걸 막는다.
        if (name[0] is '.' or '$' or '~')
            return false;

        foreach (var ignore in _options.IgnoreFolders)
        {
            if (string.Equals(name, ignore, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private void PurgeExpiredSuppressions()
    {
        if (_suppressedUntil.Count == 0)
            return;

        var now = System.Environment.TickCount64;

        foreach (var name in _suppressedUntil.Where(pair => now >= pair.Value).Select(pair => pair.Key).ToArray())
        {
            _suppressedUntil.Remove(name);
            _log.LogInformation("레이어 떼어내기 시간이 끝났습니다. 다시 등록합니다. Layer={Layer}", name);
        }

        Volatile.Write(ref _suppressedCount, _suppressedUntil.Count);
    }

    private void QueueRetire(LayerSlot slot, int graceSeconds)
    {
        if (graceSeconds <= 0)
        {
            slot.Retire();
            return;
        }

        _pending.Add(new PendingRetire(slot, System.Environment.TickCount64 + graceSeconds * 1000L));
    }

    private LayerSlot? TakePending(
        string name,
        string path,
        LayerSourceKind sourceKind,
        LayerFingerprint fingerprint)
    {
        for (var i = 0; i < _pending.Count; i++)
        {
            var slot = _pending[i].Slot;

            if (!string.Equals(slot.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!CanReuse(slot, path, sourceKind, fingerprint))
                continue;

            _pending.RemoveAt(i);
            return slot;
        }

        return null;
    }

    private void DrainPending()
    {
        if (_pending.Count == 0)
            return;

        var now = System.Environment.TickCount64;

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            if (now < _pending[i].DueTicks)
                continue;

            _pending[i].Slot.Retire();
            _pending.RemoveAt(i);
        }
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(Root))
            return;

        try
        {
            var watcher = new FileSystemWatcher(Root)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,

                // 최상위만 본다.
                // 하위까지 켜면 DB 하나를 복사할 때 SST 파일 수천 개의 이벤트가 쏟아져서
                // 감시 버퍼가 넘치고, 넘치는 순간 이벤트를 통째로 잃는다.
                // 어차피 주기적 재훑기가 실제로 믿는 수단이라 최상위만으로 충분하다.
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnWatcherEvent;
            watcher.Deleted += OnWatcherEvent;
            watcher.Renamed += OnWatcherEvent;
            watcher.Changed += OnWatcherEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _log.LogInformation("루트 폴더를 감시합니다. Root={Root}", Root);
        }
        catch (Exception ex)
        {
            // 감시는 편의 기능이다. 실패해도 주기적 재훑기로 계속 굴러간다.
            _log.LogWarning(ex,
                "폴더 감시를 시작하지 못했습니다. {Seconds}초 주기 재훑기로만 동작합니다.",
                _options.RescanSeconds);
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) => RequestRescan();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 버퍼 넘침 등으로 감시가 끊겼다. 주기적 재훑기가 있으니 서비스는 계속된다.
        _log.LogWarning(e.GetException(),
            "폴더 감시가 끊겼습니다. {Seconds}초 주기 재훑기로 계속합니다.",
            _options.RescanSeconds);

        RequestRescan();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _watcher?.Dispose();
        }
        catch
        {
            // 종료 중이다. 여기서 더 할 수 있는 게 없다.
        }

        lock (_rescanGate)
        {
            foreach (var pending in _pending)
                pending.Slot.Retire();

            _pending.Clear();

            foreach (var slot in Volatile.Read(ref _layers).Values)
                slot.Retire();

            Volatile.Write(ref _layers, FrozenDictionary<string, LayerSlot>.Empty);
        }
    }

    private void Warmup()
    {
        var snapshot = Volatile.Read(ref _layers);
        if (snapshot.Count == 0)
        {
            _log.LogWarning(
                "서비스할 레이어가 없습니다. Root 밑에 DB 폴더를 넣으면 자동으로 인식합니다. Root={Root}",
                Root);
            return;
        }

        var ok = 0;
        var failed = 0;

        foreach (var slot in snapshot.Values)
        {
            if (slot.TryWarmup(out _))
                ok++;
            else
                failed++;
        }

        _log.LogInformation("레이어 워밍업 완료. 성공={Ok} 실패={Failed}", ok, failed);
    }

    private readonly record struct PendingRetire(LayerSlot Slot, long DueTicks);
}
