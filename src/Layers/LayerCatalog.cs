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

    /// <summary>감시 중인 폴더들. 루트 + 묶음 폴더. _rescanGate 로 보호한다.</summary>
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);

    /// <summary>감시 폴더 수 상한. 묶음 폴더가 폭발적으로 많아지는 구성에서 핸들을 지킨다.</summary>
    private const int MaxWatchers = 64;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private long _rescanDueTicks;
    private long _lastRescanTicks;
    private bool _rootMissingLogged;

    /// <summary>
    /// 이미 경고한 "빠진 파일 폴더" 목록. 재훑기마다 같은 경고를 반복하지 않기 위한 것이다.
    /// 구성이 바뀌면 새 문자열이 되어 한 번 더 남는다. _rescanGate 로 보호한다.
    /// </summary>
    private readonly HashSet<string> _skippedWarnings = new(StringComparer.Ordinal);
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

        // Rescan 안에서 감시 폴더(루트 + 묶음 폴더)까지 함께 맞춘다.
        Rescan();

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

        var discovered = new List<DiscoveredLayer>();
        var groupFolders = new List<string>();
        CollectLayers(Root, prefix: string.Empty, depth: 1, discovered, groupFolders);

        foreach (var (name, directory, sourceKind, fingerprint) in discovered)
        {
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

        // 구조가 바뀌었을 수 있으니 감시 폴더도 맞춘다.
        SyncWatchers(groupFolders);

        if (added > 0 || removed > 0)
        {
            _log.LogInformation(
                "레이어 목록을 갱신했습니다. Total={Total} Added={Added} Removed={Removed}",
                next.Count, added, removed);
        }
    }

    private readonly record struct DiscoveredLayer(
        string Name,
        string Path,
        LayerSourceKind SourceKind,
        LayerFingerprint Fingerprint);

    /// <summary>
    /// 루트 밑을 훑어서 레이어가 될 폴더를 모은다.
    ///
    /// 판정 규칙 (폴더 D 하나에 대해):
    ///  1. D 가 RocksDB 면 D 가 레이어다. **DB 안으로는 절대 들어가지 않는다.**
    ///  2. D 의 하위(깊이 제한 내)에 RocksDB 가 하나라도 있으면 D 는 이름만 빌려주는 묶음 폴더다.
    ///     레이어로 만들지 않고 하위에 같은 규칙을 적용한다.
    ///  3. 하위에 RocksDB 가 전혀 없으면 **D 자체가** 파일 레이어다. 더 내려가지 않는다.
    ///
    /// 3번이 핵심이다. 파일 타일 폴더는 안이 z/x/y 로 되어 있어서, 더 내려가면
    /// filelayer/7/12 처럼 레벨별로 쪼개져 버린다. 그래서 RocksDB 가 없는 가지는
    /// **가장 얕은 곳에서** 레이어로 확정하고 멈춘다.
    ///
    /// 반환값은 "이 폴더의 하위에서 RocksDB 를 찾았는지" 다. 위 2번 판단에 쓴다.
    /// </summary>
    private bool CollectLayers(string directory, string prefix, int depth, List<DiscoveredLayer> found, List<string> groups)
    {
        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex)
        {
            // 권한 없는 폴더 하나 때문에 전체 훑기가 죽으면 안 된다.
            _log.LogWarning(ex, "폴더를 읽을 수 없어 건너뜁니다. Path={Path}", directory);
            return false;
        }

        var anyRocksBelow = false;

        // RocksDB 가 아닌 폴더들. 하위를 다 본 뒤에 레이어로 만들지 결정한다.
        List<DiscoveredLayer>? fileCandidates = null;

        foreach (var sub in subdirectories)
        {
            var name = Path.GetFileName(sub);

            if (!IsServableName(name))
                continue;

            var layerName = prefix.Length == 0 ? name : prefix + "/" + name;

            // 떼어내기(Detach) 중인 이름은 건너뛴다. 운영자가 폴더를 교체하는 중이다.
            if (_suppressedUntil.ContainsKey(layerName))
                continue;

            // 이름이 숫자뿐이면 타일 레벨(z) 폴더다. DB 일 수 없으니 "복사 중인 DB" 검사를 건너뛴다.
            // 그 검사는 폴더 안을 훑어야 해서, 항목이 수천 개인 z 폴더에서 재훑기 비용을 지배한다.
            var levelFolder = IsTileLevelName(name);

            // 준비가 안 된 DB(복사 중)면 아예 건너뛴다. 안으로도 들어가지 않는다.
            if (!LayerProbe.TryClassify(sub, out var sourceKind, out var fingerprint, checkPartial: !levelFolder))
                continue;

            if (sourceKind == LayerSourceKind.RocksDb)
            {
                found.Add(new DiscoveredLayer(layerName, sub, sourceKind, fingerprint));
                anyRocksBelow = true;
                continue;
            }

            // 하위에 RocksDB 가 있는지 따로 모아서 확인한다.
            // 없으면 하위 결과를 통째로 버리고 이 폴더를 파일 레이어로 쓴다.
            //
            // 단, 이름이 숫자뿐인 폴더는 들어가지 않는다. 타일 레벨(z)이기 때문이다.
            // 이 가지를 파고들면 z/x 폴더 수만큼(레벨 14 면 수만 개) 매번 훑게 되고,
            // 재훑기 한 번이 초 단위로 늘어난다. 묶음 폴더 이름은 숫자만으로 짓지 않는다.
            var below = new List<DiscoveredLayer>();
            var rocksBelow = depth < _options.MaxLayerDepth
                && !levelFolder
                && CollectLayers(sub, layerName, depth + 1, below, groups);

            if (rocksBelow)
            {
                // 묶음 폴더. 하위에서 확정된 레이어들을 그대로 채택한다.
                found.AddRange(below);
                anyRocksBelow = true;

                // 이 폴더 안에서 레이어가 추가/삭제되는 것을 감시해야 한다.
                groups.Add(sub);
                continue;
            }

            (fileCandidates ??= []).Add(new DiscoveredLayer(layerName, sub, sourceKind, fingerprint));
        }

        // 가지 하나는 RocksDB 아니면 파일, 한 가지로만 쓴다.
        // 이 폴더 아래에서 RocksDB 를 찾았다면 이 가지는 RocksDB 가지이므로,
        // 같은 자리에 있던 파일 폴더 후보들은 레이어로 만들지 않는다.
        //
        // 단 루트는 예외다. 루트 바로 밑의 폴더들은 서로 독립된 가지이고,
        // 여기서 섞이는 것("aaa 는 DB, ccc 는 파일 폴더")이 이 서버의 기본 사용법이다.
        // 루트까지 한 가지로 묶으면 그 조합이 통째로 죽는다.
        var branchIsRocks = depth > 1 && anyRocksBelow;

        if (fileCandidates is null)
            return anyRocksBelow;

        if (!branchIsRocks)
        {
            found.AddRange(fileCandidates);
            return anyRocksBelow;
        }

        // 조용히 빠지면 "왜 404 지?" 로 시간을 버린다. 무엇이 왜 빠졌는지 남긴다.
        //
        // 단 재훑기마다 같은 내용을 찍으면 30 초마다 로그가 쌓인다.
        // 목록이 바뀔 때만 남긴다(_rootMissingLogged 와 같은 방식).
        var skipped = string.Join(", ", fileCandidates.Select(static c => c.Name));

        if (_skippedWarnings.Add(skipped))
        {
            _log.LogWarning(
                "RocksDB 가지 안의 파일 폴더는 서비스하지 않습니다. Skipped={Skipped} (같은 가지에 RocksDB 가 있어서). " +
                "서비스하려면 이 폴더를 RocksDB 가지 밖으로 옮기세요.",
                skipped);
        }

        return anyRocksBelow;
    }

    /// <summary>
    /// 이름이 숫자뿐인지. 파일 타일 폴더의 레벨(z) 폴더를 알아보기 위한 것이다.
    ///
    /// 타일 스킴은 z/x/y 가 전부 숫자이고, 사람이 짓는 묶음 폴더 이름(korea, asia)은 그렇지 않다.
    /// 그래서 이 한 줄짜리 검사로 "여기부터는 타일 트리다" 를 구분해 파고들기를 멈춘다.
    /// 디스크를 건드리지 않으므로 비용이 0 이다.
    /// </summary>
    private static bool IsTileLevelName(string name)
    {
        foreach (var c in name)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return name.Length > 0;
    }

    /// <summary>
    /// URL 경로에서 레이어 이름을 떼어낸다. 가장 긴 접두사부터 맞춰본다.
    ///
    ///   "korea/seoul/layer.json"  ->  레이어 "korea/seoul" + 나머지 "layer.json"
    ///   "aaa/tilemapresource.xml" ->  레이어 "aaa"         + 나머지 "tilemapresource.xml"
    ///
    /// 깊이가 얕아서(기본 3) 반복은 몇 번뿐이다.
    /// </summary>
    public bool TryResolveBlob(string path, out LayerSlot slot, out string remainder)
    {
        var layers = Volatile.Read(ref _layers);
        var cut = path.Length;

        while (true)
        {
            cut = path.LastIndexOf('/', cut - 1);

            if (cut <= 0)
            {
                slot = null!;
                remainder = string.Empty;
                return false;
            }

            if (layers.TryGetValue(path[..cut], out slot!))
            {
                remainder = path[(cut + 1)..];
                return remainder.Length > 0;
            }
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

    /// <summary>
    /// 감시할 폴더를 현재 구조에 맞춘다. 루트 + 묶음 폴더들을 각각 **비재귀로** 감시한다.
    ///
    /// 왜 이렇게 하는가:
    ///  - 재귀 감시(IncludeSubdirectories=true)를 켜면 DB 하나 복사에 SST 파일 수천 개의
    ///    이벤트가 쏟아져서 감시 버퍼가 넘치고, 넘치는 순간 이벤트를 통째로 잃는다.
    ///  - 루트만 감시하면 중첩 레이어(korea/seoul)의 추가·삭제를 놓친다.
    ///    korea 안의 변화는 루트의 변화가 아니기 때문이다. 그러면 주기적 재훑기까지 기다려야 한다.
    ///  - 묶음 폴더는 그 안에 DB 파일이 없고 폴더만 있으므로, 비재귀로 감시하면
    ///    폴더 생성/삭제/이름변경만 몇 건 들어온다. 넘칠 일이 없다.
    ///
    /// 감시는 어디까지나 반응을 빠르게 하는 보조 수단이고, 믿는 것은 주기적 재훑기다.
    /// </summary>
    private void SyncWatchers(IReadOnlyCollection<string> groupFolders)
    {
        if (!_options.WatchFileSystem)
            return;

        var previous = _watchers.Count;
        var wanted = new HashSet<string>(PathComparer) { Root };

        foreach (var folder in groupFolders)
        {
            if (wanted.Count >= MaxWatchers)
                break;

            wanted.Add(folder);
        }

        // 없어진 폴더의 감시를 정리한다.
        foreach (var path in _watchers.Keys.Where(p => !wanted.Contains(p)).ToArray())
        {
            try
            {
                _watchers[path].Dispose();
            }
            catch
            {
                // 종료 중일 수 있다. 무시한다.
            }

            _watchers.Remove(path);
        }

        // 새로 생긴 폴더를 감시한다.
        foreach (var path in wanted)
        {
            if (_watchers.ContainsKey(path) || !Directory.Exists(path))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 64 * 1024,
                };

                watcher.Created += OnWatcherEvent;
                watcher.Deleted += OnWatcherEvent;
                watcher.Renamed += OnWatcherEvent;
                watcher.Changed += OnWatcherEvent;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                _watchers[path] = watcher;
            }
            catch (Exception ex)
            {
                // 감시는 편의 기능이다. 실패해도 주기적 재훑기로 계속 굴러간다.
                _log.LogWarning(ex,
                    "폴더 감시를 시작하지 못했습니다. Path={Path} ({Seconds}초 주기 재훑기로 대체)",
                    path, _options.RescanSeconds);
            }
        }

        if (_watchers.Count != previous)
        {
            _log.LogInformation(
                "감시 폴더를 갱신했습니다. 루트 + 묶음 폴더 = {Count}개 (묶음 {Groups}개)",
                _watchers.Count, Math.Max(0, _watchers.Count - 1));
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

        foreach (var watcher in _watchers.Values)
        {
            try
            {
                watcher.Dispose();
            }
            catch
            {
                // 종료 중이다. 여기서 더 할 수 있는 게 없다.
            }
        }

        _watchers.Clear();

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
