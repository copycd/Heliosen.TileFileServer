using System.Diagnostics;
using Heliosen.TileFileServer.Configuration;
using Heliosen.TileFileServer.Layers;

namespace Heliosen.TileFileServer.Endpoints;

/// <summary>
/// 첫 화면과 /admin/status 에 보여줄 서버 상태.
///
/// 값은 전부 요청이 올 때 그 자리에서 읽는다. 카운터를 따로 돌리지 않는다.
/// 요청마다 갱신하는 통계(총 요청 수 같은 것)는 코어 사이에서 캐시 라인을 튕겨서
/// 초당 수십만 요청 구간에서 실제로 처리량을 깎는다. 상태 화면 하나 때문에 그걸 낼 이유가 없다.
/// </summary>
internal sealed record ServerStatus(
    string Version,
    DateTimeOffset StartedAt,
    TimeSpan Uptime,
    DateTimeOffset Now,
    string Root,
    bool RootExists,
    int LayerCount,
    DateTimeOffset? LastScanAt,
    int RescanSeconds,
    bool WatchFileSystem,
    int BlockCacheMB,
    long BlockCacheUsedMB,
    long ManagedMemoryMB,
    long WorkingSetMB,
    string Runtime,
    string OperatingSystem,
    string MachineName,
    int ProcessorCount)
{
    /// <summary>프로세스가 뜬 시각. 정적 초기화 시점이라 기동 직후로 봐도 된다.</summary>
    private static readonly DateTimeOffset ProcessStartedAt = ResolveStartTime();

    public static ServerStatus Capture(LayerCatalog catalog, TileServerOptions options, string version)
    {
        var now = DateTimeOffset.Now;

        return new ServerStatus(
            Version: version,
            StartedAt: ProcessStartedAt,
            Uptime: now - ProcessStartedAt,
            Now: now,
            Root: catalog.Root,
            RootExists: Directory.Exists(catalog.Root),
            LayerCount: catalog.Count,
            LastScanAt: catalog.LastScanAt,
            RescanSeconds: options.RescanSeconds,
            WatchFileSystem: options.WatchFileSystem,
            BlockCacheMB: catalog.Environment.BlockCacheMB,
            BlockCacheUsedMB: (long)(catalog.Environment.BlockCacheUsedBytes / (1024 * 1024)),
            ManagedMemoryMB: GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024),
            WorkingSetMB: Environment.WorkingSet / (1024 * 1024),
            Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim(),
            MachineName: Environment.MachineName,
            ProcessorCount: Environment.ProcessorCount);
    }

    private static DateTimeOffset ResolveStartTime()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime;
        }
        catch
        {
            // 컨테이너 등에서 프로세스 정보를 못 읽을 수 있다. 그때는 이 타입이 처음 쓰인 시각으로 대신한다.
            return DateTimeOffset.Now;
        }
    }

    /// <summary>"3일 4시간 12분" 처럼 사람이 읽기 좋게.</summary>
    public string UptimeText
    {
        get
        {
            var t = Uptime;

            if (t.TotalDays >= 1)
                return $"{(int)t.TotalDays}일 {t.Hours}시간 {t.Minutes}분";

            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}시간 {t.Minutes}분 {t.Seconds}초";

            if (t.TotalMinutes >= 1)
                return $"{(int)t.TotalMinutes}분 {t.Seconds}초";

            return $"{(int)t.TotalSeconds}초";
        }
    }

    /// <summary>다음 주기 훑기까지 남은 시간. 주기 훑기를 껐으면 null.</summary>
    public TimeSpan? NextScanIn
    {
        get
        {
            if (RescanSeconds <= 0 || LastScanAt is null)
                return null;

            var due = LastScanAt.Value.AddSeconds(RescanSeconds) - Now;
            return due > TimeSpan.Zero ? due : TimeSpan.Zero;
        }
    }
}
