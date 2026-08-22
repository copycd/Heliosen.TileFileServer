namespace Heliosen.TileServer.Configuration;

/// <summary>
/// 파일 디스크립터 한도 확인.
///
/// 리눅스에서 이게 실제로 서비스를 죽인다.
/// RocksDB 는 DB 하나가 SST 파일 수백 개를 열어둘 수 있어서
/// (레이어 수 x MaxOpenFilesPerDb) 가 ulimit -n 을 넘으면
/// "Too many open files" 로 조회가 실패하기 시작한다.
/// 그런데 그 시점은 트래픽이 몰릴 때라, 미리 경고해두는 게 낫다.
/// </summary>
internal static class ResourceLimits
{
    /// <summary>열 수 있는 파일 수의 소프트 한도. 알 수 없으면 null.</summary>
    public static int? TryGetOpenFileLimit()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            // /proc/self/limits 예:
            // Max open files            1024                 1048576              files
            foreach (var line in File.ReadLines("/proc/self/limits"))
            {
                if (!line.StartsWith("Max open files", StringComparison.Ordinal))
                    continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // parts = [Max, open, files, <soft>, <hard>, files]
                if (parts.Length >= 4 && int.TryParse(parts[3], out var soft))
                    return soft;

                // 소프트 한도가 "unlimited" 인 경우.
                return int.MaxValue;
            }
        }
        catch
        {
            // 못 읽어도 그냥 넘어간다. 확인용 정보일 뿐이다.
        }

        return null;
    }

    /// <summary>
    /// 한도가 빠듯하면 경고를 남긴다. 무엇을 바꾸면 되는지까지 알려준다.
    /// </summary>
    public static void WarnIfTight(ILogger log, TileServerOptions options, int layerCount)
    {
        var limit = TryGetOpenFileLimit();
        if (limit is null || limit == int.MaxValue)
            return;

        // 무제한(-1) 설정이면 레이어 하나가 얼마나 열지 예측할 수 없다. 그 자체로 경고 대상이다.
        if (options.MaxOpenFilesPerDb < 0)
        {
            log.LogWarning(
                "MaxOpenFilesPerDb 가 무제한(-1)입니다. 파일 디스크립터 한도가 {Limit} 뿐이라 " +
                "레이어가 늘어나면 'Too many open files' 가 날 수 있습니다.",
                limit);
            return;
        }

        if (layerCount <= 0)
            return;

        // 레이어가 쓸 수 있는 최대치 + 소켓/로그 등 여유분.
        var worstCase = (long)layerCount * options.MaxOpenFilesPerDb + 256;

        if (worstCase <= limit * 0.7)
            return;

        log.LogWarning(
            "파일 디스크립터가 부족할 수 있습니다. 레이어 {Layers}개 x DB당 {PerDb} = 최대 약 {Worst}개, " +
            "현재 한도 {Limit}개. " +
            "ulimit -n 을 올리거나(systemd 는 LimitNOFILE) MaxOpenFilesPerDb 를 낮추세요.",
            layerCount, options.MaxOpenFilesPerDb, worstCase, limit);
    }
}
