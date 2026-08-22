using System.Buffers.Binary;

namespace Heliosen.TileFileServer.Tiles;

/// <summary>
/// 타일 키 인코딩. **DTB.RocksTileStore.RocksTileStore.EncodeTileKey 와 동일해야 한다.**
///
///   [kind:1][level:1][col:4 big-endian][row:4 big-endian]
///
/// big-endian 이라 바이트 사전순 = (kind, level, col, row) 오름차순이다.
/// 덕분에 "이 DB 에 어떤 kind 가 들어있나" 를 Seek 몇 번으로 알 수 있다.
/// </summary>
public static class TileKey
{
    public const int Size = 10;

    public static void Write(Span<byte> destination, TileFormatKind kind, byte level, uint col, uint row)
    {
        destination[0] = (byte)kind;
        destination[1] = level;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(2, 4), col);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(6, 4), row);
    }

    public static bool IsTileKey(ReadOnlySpan<byte> key) =>
        key.Length == Size && TileFormat.IsKnownKind(key[0]);
}
