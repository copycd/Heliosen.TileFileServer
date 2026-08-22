namespace Heliosen.TileFileServer.Tiles;

/// <summary>
/// RocksDB 타일 키의 첫 바이트로 들어가는 값.
///
/// **DTB.RocksTileStore.TileLayerFormatKind 와 숫자가 반드시 같아야 한다.**
/// 여기 숫자를 바꾸면 이미 만들어진 DB 를 못 읽는다.
/// (DTB.RocksTileStore 를 참조하지 않고 여기서 다시 정의한 이유는,
///  그 프로젝트가 win-x64 / AOT / 난독화에 묶여있고 사설 패키지 피드에만 있어서다.
///  키 포맷은 10 바이트뿐이라 복제 비용이 참조 비용보다 훨씬 싸다.)
/// </summary>
public enum TileFormatKind : byte
{
    Unknown = 0,
    Jpg = 1,
    Png = 2,
    Terrain = 5,
    Raw = 6,
    Vector = 10,
}
