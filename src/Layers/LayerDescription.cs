namespace Heliosen.TileFileServer.Layers;

/// <summary>/admin/layers 응답용. 진단이 목적이다.</summary>
public sealed class LayerDescription
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string Path { get; init; }
    public required string State { get; init; }
    public string? Format { get; init; }
    public string[]? Formats { get; init; }

    /// <summary>
    /// 응답할 때 gzip 으로 압축하는 포맷. (GzipTerrain / GzipLayers 설정)
    /// DB 에 이미 gzip 으로 저장된 타일은 여기 없어도 저장된 그대로 gzip 으로 나간다.
    /// </summary>
    public string[]? GzipFormats { get; init; }
    public string? ContentsType { get; init; }
    public string? Error { get; init; }
    public long? NegativeCacheCount { get; init; }
}
