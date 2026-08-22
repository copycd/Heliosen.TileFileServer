namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// "이 폴더의 내용이 그대로인가" 를 싸게 판단하기 위한 지문.
///
/// 이게 왜 필요한가:
/// RocksDB 를 조회 전용으로 열면 **열린 시점의 스냅샷만** 본다.
/// 그래서 운영자가 같은 폴더에 DB 를 새로 덮어써도, 이미 열려있는 핸들은
/// 옛 내용을 계속 내보낸다. 폴더 이름도 그대로니 재훑기도 눈치를 못 챈다.
///
/// CURRENT 는 RocksDB 가 MANIFEST 를 갈아치울 때마다 다시 쓰이는 파일이다.
/// 그래서 (CURRENT 의 시각/크기 + MANIFEST 이름) 이 바뀌면 DB 가 바뀐 것으로 보고 다시 연다.
/// </summary>
internal readonly record struct LayerFingerprint(long Ticks, long Size, string Manifest)
{
    public static readonly LayerFingerprint None = new(0, 0, string.Empty);
}

internal static class LayerProbe
{
    /// <summary>
    /// 폴더가 RocksDB 인지 그냥 파일 폴더인지 판정하고, 함께 지문도 계산한다.
    /// 판정과 지문을 한 번에 하는 이유는 같은 디렉터리를 두 번 훑지 않기 위해서다.
    /// </summary>
    public static bool TryClassify(string path, out LayerSourceKind kind, out LayerFingerprint fingerprint)
    {
        kind = LayerSourceKind.FileSystem;
        fingerprint = LayerFingerprint.None;

        try
        {
            if (!Directory.Exists(path))
                return false;

            var currentPath = Path.Combine(path, "CURRENT");
            if (File.Exists(currentPath))
            {
                // MANIFEST 는 보통 하나지만, 교체 중에 잠깐 둘이 보일 수 있다.
                // 이름이 증가하는 규칙이라 가장 큰 것을 쓰면 안정적이다.
                string? manifest = null;
                foreach (var file in Directory.EnumerateFiles(path, "MANIFEST-*"))
                {
                    var name = Path.GetFileName(file);
                    if (manifest is null || string.CompareOrdinal(name, manifest) > 0)
                        manifest = name;
                }

                if (manifest is not null)
                {
                    var info = new FileInfo(currentPath);
                    kind = LayerSourceKind.RocksDb;
                    fingerprint = new LayerFingerprint(info.LastWriteTimeUtc.Ticks, info.Length, manifest);
                    return true;
                }
            }

            // CURRENT 가 없는데 RocksDB 부속 파일이 보인다면, 복사가 아직 끝나지 않은 DB 다.
            // 이걸 일반 파일 폴더로 등록해버리면 완성될 때까지 엉뚱하게 404 만 내보내게 된다.
            // 아직 준비가 안 된 것으로 보고 건너뛰면, 복사가 끝난 뒤 재훑기에서 제대로 잡힌다.
            if (LooksLikePartialRocksDb(path))
                return false;

            // RocksDB 가 아니면 일반 파일 폴더로 본다.
            kind = LayerSourceKind.FileSystem;

            // 파일 폴더는 요청마다 디스크를 직접 보므로 내용이 바뀌어도 낡을 일이 없다.
            // 지문을 볼 이유가 없어서 계산하지 않는다.
            fingerprint = LayerFingerprint.None;
            return true;
        }
        catch (Exception)
        {
            // 권한 문제, 경로 길이, 잠긴 파일 등. 이 폴더만 건너뛴다.
            return false;
        }
    }

    /// <summary>CURRENT 는 없는데 RocksDB 흔적이 있는지. 복사/생성이 진행 중인 DB 를 걸러내기 위한 것.</summary>
    private static bool LooksLikePartialRocksDb(string path)
    {
        foreach (var pattern in (string[])["MANIFEST-*", "*.sst", "OPTIONS-*", "LOCK"])
        {
            if (Directory.EnumerateFiles(path, pattern).Any())
                return true;
        }

        return false;
    }
}
