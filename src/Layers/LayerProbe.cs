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
    /// 폴더가 RocksDB 인지 판정하고, 맞으면 지문도 함께 계산한다.
    /// 판정과 지문을 한 번에 하는 이유는 같은 디렉터리를 두 번 훑지 않기 위해서다.
    ///
    /// 파일 폴더는 목록에 담지 않으므로(요청 때 디스크에서 바로 찾는다) 여기서 구분할 필요가 없다.
    /// </summary>
    public static bool TryProbeRocksDb(string path, out LayerFingerprint fingerprint)
    {
        fingerprint = LayerFingerprint.None;

        try
        {
            if (!Directory.Exists(path))
                return false;

            // RocksDB 인지 아닌지는 DB 를 만든 쪽과 같은 판정을 쓴다.
            var currentPath = Path.Combine(path, "CURRENT");
            if (DTB.RocksTileStore.RocksDBUtils.IsRocksDBDirectory(path))
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
                    fingerprint = new LayerFingerprint(info.LastWriteTimeUtc.Ticks, info.Length, manifest);
                    return true;
                }
            }

            // CURRENT 가 없으면 아직 DB 가 아니다(복사 중이거나 그냥 파일 폴더).
            // 복사가 끝나면 다음 재훑기에서 잡힌다.
            return false;
        }
        catch (Exception)
        {
            // 권한 문제, 경로 길이, 잠긴 파일 등. 이 폴더만 건너뛴다.
            return false;
        }
    }

}
