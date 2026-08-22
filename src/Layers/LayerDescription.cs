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
    public string? ContentsType { get; init; }
    public string? Error { get; init; }
    public long? NegativeCacheCount { get; init; }
}
