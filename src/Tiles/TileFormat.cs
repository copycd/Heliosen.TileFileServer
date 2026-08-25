using DTB.RocksTileStore;
using RocksStore = DTB.RocksTileStore.RocksTileStore;

namespace Heliosen.TileFileServer.Tiles;

/// <summary>
/// 확장자 ↔ 포맷 ↔ MIME 변환.
///
/// **포맷 정의와 키 인코딩은 DTB.RocksTileStore 것을 그대로 쓴다.**
/// DB 를 만드는 쪽(빌더)과 읽는 쪽(이 서버)이 같은 정의를 공유해야 포맷 바이트가 어긋날 일이 없다.
/// 여기에 있는 것은 그 위에 HTTP 로 내보내기 위해 필요한 것들(MIME, gzip 판별)뿐이다.
/// </summary>
public static class TileFormat
{
    /// <summary>
    /// DB 에 실제로 들어갈 수 있는 종류. 오름차순이어야 한다(타일 키의 바이트 정렬 순서와 같게).
    /// TileLayerFormatKind 에 값이 추가되면 여기도 같이 넣어야 한다.
    /// </summary>
    public static readonly TileLayerFormatKind[] KnownKinds =
    [
        TileLayerFormatKind.jpg,
        TileLayerFormatKind.png,
        TileLayerFormatKind.terrain,
        TileLayerFormatKind.raw,
        TileLayerFormatKind.vector,
    ];

    /// <summary>타일 키의 길이. DTB.RocksTileStore 가 정한 값을 그대로 쓴다.</summary>
    public static int KeySize => RocksStore.KeySize;

    /// <summary>
    /// 타일 키의 첫 바이트로 올 수 있는 값인지.
    ///
    /// 알려진 값은 모두 10 이하이고, 문자열 키는 출력 가능한 아스키(0x20 이상)로 시작한다.
    /// 그래서 이 검사만으로 바이너리 타일 키와 문자열 경로 키가 확실히 구분된다.
    /// ("0/0/0.b3dm" 처럼 길이가 딱 10 바이트인 문자열 키도 첫 바이트가 '0'(0x30) 이라 걸러진다.)
    ///
    /// (DTB.RocksTileStore 의 IsTileKey 는 internal 이라 쓸 수 없어서 여기서 판정한다.
    ///  숫자 자체는 TileLayerFormatKind 를 그대로 참조하므로 어긋날 수 없다.)
    /// </summary>
    public static bool IsKnownKind(byte kind) =>
        kind is (byte)TileLayerFormatKind.jpg
             or (byte)TileLayerFormatKind.png
             or (byte)TileLayerFormatKind.terrain
             or (byte)TileLayerFormatKind.raw
             or (byte)TileLayerFormatKind.vector;

    /// <summary>
    /// URL 확장자를 포맷 종류로. 앞의 '.' 은 있어도 되고 없어도 된다.
    ///
    /// jpg / jpeg / png / terrain / raw 판정은 빌더와 같은 함수(RocksTileStoreUtils)를 쓴다.
    /// pbf / mvt / vector 는 그쪽에 없어서(빌더가 벡터 타일을 아직 안 만든다) 여기서 보탠다.
    /// </summary>
    public static TileLayerFormatKind FromExtension(string? ext)
    {
        if (string.IsNullOrEmpty(ext))
            return TileLayerFormatKind.Unknown;

        // 점이 있든 없든 받고, 대소문자도 무시한다(확인함).
        var kind = RocksTileStoreUtils.GetLayerFormatKind(ext);
        if (kind != TileLayerFormatKind.Unknown)
            return kind;

        ReadOnlySpan<char> e = ext;
        if (e[0] == '.')
            e = e[1..];

        if (e.Equals("pbf", StringComparison.OrdinalIgnoreCase)) return TileLayerFormatKind.vector;
        if (e.Equals("mvt", StringComparison.OrdinalIgnoreCase)) return TileLayerFormatKind.vector;
        if (e.Equals("vector", StringComparison.OrdinalIgnoreCase)) return TileLayerFormatKind.vector;

        return TileLayerFormatKind.Unknown;
    }

    public static string ContentTypeOf(TileLayerFormatKind kind) => kind switch
    {
        TileLayerFormatKind.jpg => "image/jpeg",
        TileLayerFormatKind.png => "image/png",
        TileLayerFormatKind.terrain => "application/vnd.quantized-mesh",
        TileLayerFormatKind.vector => "application/x-protobuf",
        _ => "application/octet-stream",
    };

    public static string ExtensionOf(TileLayerFormatKind kind) => kind switch
    {
        TileLayerFormatKind.jpg => "jpg",
        TileLayerFormatKind.png => "png",
        TileLayerFormatKind.terrain => "terrain",
        TileLayerFormatKind.raw => "raw",
        TileLayerFormatKind.vector => "pbf",
        _ => "bin",
    };

    /// <summary>
    /// 이 포맷이 gzip 으로 저장돼 있을 수 있는지.
    ///
    /// jpg/png 는 이미 압축된 포맷이라 gzip 으로 감싸 저장하지 않는다.
    /// 반면 terrain(quantized-mesh) 은 빌더(DrvHeightTerrain.writeGZipped)가 gzip 으로 기록하는 경우가 있고,
    /// 그때 Content-Encoding 을 안 붙이면 Cesium 이 그대로 파싱하다 깨진다.
    /// </summary>
    public static bool MayBeGzipped(TileLayerFormatKind kind) =>
        kind is not (TileLayerFormatKind.jpg or TileLayerFormatKind.png);

    /// <summary>gzip 매직 넘버(1F 8B 08). 확실히 구분되므로 오탐 걱정은 없다.</summary>
    public static bool IsGzip(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0x1F && bytes[1] == 0x8B && bytes[2] == 0x08;

    /// <summary>
    /// 확장자 → MIME 표.
    ///
    /// 파일 폴더는 일반 웹 서버처럼 동작해야 하므로(css, js, woff2, mp4 ...)
    /// 손으로 만든 표로는 부족하다. ASP.NET 의 정적 파일 MIME 표(약 380 종)를 그대로 쓰고,
    /// 그 표에 없는 타일 전용 확장자만 얹는다.
    /// css 를 octet-stream 으로 내보내면 브라우저가 스타일시트를 아예 적용하지 않는다.
    /// </summary>
    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider MimeProvider = CreateMimeProvider();

    private static Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider CreateMimeProvider()
    {
        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();

        // 타일 쪽 확장자는 표준 표에 없다. 있는 것은 건드리지 않고 없는 것만 채운다.
        provider.Mappings[".terrain"] = "application/vnd.quantized-mesh";
        provider.Mappings[".pbf"] = "application/x-protobuf";
        provider.Mappings[".mvt"] = "application/x-protobuf";
        provider.Mappings[".b3dm"] = "application/octet-stream";
        provider.Mappings[".i3dm"] = "application/octet-stream";
        provider.Mappings[".pnts"] = "application/octet-stream";
        provider.Mappings[".cmpt"] = "application/octet-stream";
        provider.Mappings[".glb"] = "model/gltf-binary";
        provider.Mappings[".gltf"] = "model/gltf+json";
        provider.Mappings[".raw"] = "application/octet-stream";

        return provider;
    }

    /// <summary>
    /// 경로의 확장자로 MIME 을 정한다.
    /// 부가 파일(layer.json, tilemapresource.xml)과 파일 폴더의 모든 파일이 이걸 쓴다.
    /// </summary>
    public static string ContentTypeForPath(string path) =>
        MimeProvider.TryGetContentType(path, out var contentType)
            ? contentType
            : "application/octet-stream";
}
