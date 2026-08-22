
namespace Heliosen.TileServer.Tiles;

/// <summary>포맷 종류 ↔ 확장자 ↔ MIME 변환.</summary>
public static class TileFormat
{
    /// <summary>DB 에 실제로 들어갈 수 있는 종류. 오름차순이어야 한다(키 정렬 순서와 같게).</summary>
    public static readonly TileFormatKind[] KnownKinds =
    [
        TileFormatKind.Jpg,
        TileFormatKind.Png,
        TileFormatKind.Terrain,
        TileFormatKind.Raw,
        TileFormatKind.Vector,
    ];

    /// <summary>
    /// 타일 키의 첫 바이트로 올 수 있는 값인지.
    ///
    /// 알려진 값은 모두 10 이하이고, 문자열 키는 출력 가능한 아스키(0x20 이상)로 시작한다.
    /// 그래서 이 검사만으로 바이너리 타일 키와 문자열 경로 키가 확실히 구분된다.
    /// ("0/0/0.b3dm" 처럼 길이가 딱 10 바이트인 문자열 키도 첫 바이트가 '0'(0x30) 이라 걸러진다.)
    /// </summary>
    public static bool IsKnownKind(byte kind) =>
        kind is (byte)TileFormatKind.Jpg
             or (byte)TileFormatKind.Png
             or (byte)TileFormatKind.Terrain
             or (byte)TileFormatKind.Raw
             or (byte)TileFormatKind.Vector;

    /// <summary>URL 확장자를 포맷 종류로. 앞의 '.' 은 있어도 되고 없어도 된다.</summary>
    public static TileFormatKind FromExtension(string? ext)
    {
        if (string.IsNullOrEmpty(ext))
            return TileFormatKind.Unknown;

        ReadOnlySpan<char> e = ext;
        if (e[0] == '.')
            e = e[1..];

        // 대소문자 무시. 확장자 종류가 적어서 스위치가 사전 조회보다 빠르다.
        if (e.Equals("jpg", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Jpg;
        if (e.Equals("jpeg", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Jpg;
        if (e.Equals("png", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Png;
        if (e.Equals("terrain", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Terrain;
        if (e.Equals("raw", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Raw;
        if (e.Equals("pbf", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Vector;
        if (e.Equals("mvt", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Vector;
        if (e.Equals("vector", StringComparison.OrdinalIgnoreCase)) return TileFormatKind.Vector;

        return TileFormatKind.Unknown;
    }

    /// <summary>설정 파일의 "Format": "terrain" 같은 값을 읽을 때 쓴다.</summary>
    public static bool TryParseName(string? name, out TileFormatKind kind)
    {
        kind = FromExtension(name);
        if (kind != TileFormatKind.Unknown)
            return true;

        return Enum.TryParse(name, ignoreCase: true, out kind) && kind != TileFormatKind.Unknown;
    }

    public static string ContentTypeOf(TileFormatKind kind) => kind switch
    {
        TileFormatKind.Jpg => "image/jpeg",
        TileFormatKind.Png => "image/png",
        TileFormatKind.Terrain => "application/vnd.quantized-mesh",
        TileFormatKind.Vector => "application/x-protobuf",
        _ => "application/octet-stream",
    };

    public static string ExtensionOf(TileFormatKind kind) => kind switch
    {
        TileFormatKind.Jpg => "jpg",
        TileFormatKind.Png => "png",
        TileFormatKind.Terrain => "terrain",
        TileFormatKind.Raw => "raw",
        TileFormatKind.Vector => "pbf",
        _ => "bin",
    };

    /// <summary>
    /// 이 포맷이 gzip 으로 저장돼 있을 수 있는지.
    ///
    /// jpg/png 는 이미 압축된 포맷이라 gzip 으로 감싸 저장하지 않는다.
    /// 반면 terrain(quantized-mesh) 은 빌더(DrvHeightTerrain.writeGZipped)가 gzip 으로 기록하는 경우가 있고,
    /// 그때 Content-Encoding 을 안 붙이면 Cesium 이 그대로 파싱하다 깨진다.
    /// </summary>
    public static bool MayBeGzipped(TileFormatKind kind) =>
        kind is not (TileFormatKind.Jpg or TileFormatKind.Png);

    /// <summary>gzip 매직 넘버(1F 8B 08). 확실히 구분되므로 오탐 걱정은 없다.</summary>
    public static bool IsGzip(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0x1F && bytes[1] == 0x8B && bytes[2] == 0x08;

    /// <summary>
    /// 문자열 키(경로)로 저장된 부가 파일의 MIME. layer.json, tilemapresource.xml, 3D Tiles 조각들.
    /// </summary>
    public static string ContentTypeForPath(string path)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(path.AsSpan());
        if (ext.Length > 0 && ext[0] == '.')
            ext = ext[1..];

        if (ext.Equals("json", StringComparison.OrdinalIgnoreCase)) return "application/json";
        if (ext.Equals("xml", StringComparison.OrdinalIgnoreCase)) return "application/xml";
        if (ext.Equals("jpg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (ext.Equals("jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (ext.Equals("png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (ext.Equals("webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (ext.Equals("terrain", StringComparison.OrdinalIgnoreCase)) return "application/vnd.quantized-mesh";
        if (ext.Equals("pbf", StringComparison.OrdinalIgnoreCase)) return "application/x-protobuf";
        if (ext.Equals("mvt", StringComparison.OrdinalIgnoreCase)) return "application/x-protobuf";
        if (ext.Equals("b3dm", StringComparison.OrdinalIgnoreCase)) return "application/octet-stream";
        if (ext.Equals("i3dm", StringComparison.OrdinalIgnoreCase)) return "application/octet-stream";
        if (ext.Equals("pnts", StringComparison.OrdinalIgnoreCase)) return "application/octet-stream";
        if (ext.Equals("cmpt", StringComparison.OrdinalIgnoreCase)) return "application/octet-stream";
        if (ext.Equals("glb", StringComparison.OrdinalIgnoreCase)) return "model/gltf-binary";
        if (ext.Equals("gltf", StringComparison.OrdinalIgnoreCase)) return "model/gltf+json";
        if (ext.Equals("txt", StringComparison.OrdinalIgnoreCase)) return "text/plain; charset=utf-8";
        if (ext.Equals("html", StringComparison.OrdinalIgnoreCase)) return "text/html; charset=utf-8";

        return "application/octet-stream";
    }
}
