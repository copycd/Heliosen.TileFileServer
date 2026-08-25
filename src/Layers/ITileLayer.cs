namespace Heliosen.TileFileServer.Layers;

/// <summary>
/// 하나의 레이어(= 루트 밑의 폴더 하나).
///
/// 구현은 모두 **스레드 안전하고 상태를 바꾸지 않는 조회 전용**이어야 한다.
/// 요청 스레드에서 그대로 호출된다.
/// </summary>
public interface ITileLayer : IDisposable
{
    string Name { get; }

    /// <summary>진단용 표기. "RocksDB" 또는 "FileSystem".</summary>
    string Source { get; }

    string Path { get; }

    /// <summary>
    /// 타일 좌표 규칙 없이 **경로 그대로** 서비스하는 레이어인지.
    ///
    /// 파일 폴더는 그냥 일반 웹 서버처럼 동작해야 한다. 레벨 상한도, 좌표 범위도,
    /// 확장자 추측도 없다. URL 경로가 곧 파일 경로다.
    /// RocksDB 는 반대다. 좌표를 10 바이트 키로 인코딩해야 하므로 규칙이 필요하다.
    /// </summary>
    bool ServesByPath { get; }

    /// <summary>{z}/{x}/{y}.{ext} 조회. 없으면 null.</summary>
    TilePayload? GetTile(int z, int x, int y, string? extension);

    /// <summary>
    /// 타일 좌표가 아닌 경로 조회. layer.json, tilemapresource.xml, tileset.json,
    /// 3D Tiles 조각(0/0/0.b3dm) 같은 것들. 없으면 null.
    /// </summary>
    TilePayload? GetBlob(string relativePath);

    LayerDescription Describe(string state, string? error);
}
