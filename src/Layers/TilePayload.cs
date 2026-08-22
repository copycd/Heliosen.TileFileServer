using Microsoft.Net.Http.Headers;

namespace Heliosen.TileServer.Layers;

/// <summary>
/// 응답으로 내보낼 내용. Bytes 또는 PhysicalPath 중 하나만 채운다.
/// PhysicalPath 를 쓰면 커널 sendfile 로 나가고 ETag/Range 처리는 ASP.NET 이 해준다.
/// </summary>
public sealed class TilePayload
{
    public byte[]? Bytes { get; init; }

    public string? PhysicalPath { get; init; }

    public required string ContentType { get; init; }

    /// <summary>gzip 으로 저장된 내용이면 "gzip". 아니면 null.</summary>
    public string? ContentEncoding { get; init; }

    public EntityTagHeaderValue? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }
}
