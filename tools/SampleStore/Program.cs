using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RocksDbSharp;

// 서버를 실제로 두드려보기 위한 표본 저장소 생성기.
//
// DTB.RocksTileStore 와 **같은 키 포맷**으로 기록한다.
//   [kind:1][level:1][col:4 big-endian][row:4 big-endian]
// 그래서 이걸로 만든 DB 가 서비스되면, 빌더가 만든 DB 도 서비스된다는 뜻이 된다.
//
// 사용법:  SampleStore <출력루트>
//   <출력루트>/aaa  jpg 래스터 타일
//   <출력루트>/bbb  gzip 으로 압축된 terrain 타일 (Content-Encoding 확인용)

var root = args.Length > 0 ? args[0] : "tiles";
Directory.CreateDirectory(root);

MakeRasterStore(Path.Combine(root, "aaa"));
MakeTerrainStore(Path.Combine(root, "bbb"));

Console.WriteLine($"완료. Root={Path.GetFullPath(root)}");
return 0;

static void MakeRasterStore(string path)
{
    using var db = Open(path);

    // 레벨 0..13 까지 각 레벨에 몇 장씩.
    var count = 0;
    for (byte level = 0; level <= 13; level++)
    {
        var span = 1u << level;
        for (uint col = 0; col < Math.Min(span, 4); col++)
        {
            for (uint row = 0; row < Math.Min(span, 4); row++)
            {
                db.Put(TileKey(1 /* jpg */, level, col, row), FakeJpeg(level, col, row));
                count++;
            }
        }
    }

    // 사용자가 예로 든 좌표를 확실히 넣어둔다: /aaa/12/345/4455.jpg
    db.Put(TileKey(1, 12, 345, 4455), FakeJpeg(12, 345, 4455));
    count++;

    // 타일이 아닌 부가 파일은 경로 문자열 키로 들어간다(빌더와 같은 방식).
    db.Put(Encoding.UTF8.GetBytes("tilemapresource.xml"),
        Encoding.UTF8.GetBytes("<TileMap><Title>aaa</Title></TileMap>"));

    db.Put(Encoding.UTF8.GetBytes("ContentsType"), Encoding.UTF8.GetBytes("TileRaster"));

    Flush(db);
    Console.WriteLine($"aaa: jpg 타일 {count}장 + tilemapresource.xml");
}

static void MakeTerrainStore(string path)
{
    using var db = Open(path);

    var count = 0;
    for (byte level = 0; level <= 5; level++)
    {
        var span = 1u << level;
        for (uint col = 0; col < Math.Min(span, 4); col++)
        {
            for (uint row = 0; row < Math.Min(span, 4); row++)
            {
                // 빌더의 writeGZipped 처럼 gzip 으로 넣는다.
                // 서버가 Content-Encoding: gzip 을 붙여주는지 확인하는 게 목적이다.
                db.Put(TileKey(5 /* terrain */, level, col, row), Gzip(FakeTerrain(level, col, row)));
                count++;
            }
        }
    }

    // 사용자가 예로 든 좌표: /bbb/4/222/5555.terrain
    db.Put(TileKey(5, 4, 222, 5555), Gzip(FakeTerrain(4, 222, 5555)));
    count++;

    db.Put(Encoding.UTF8.GetBytes("layer.json"),
        Encoding.UTF8.GetBytes("""{"tilejson":"2.1.0","format":"quantized-mesh-1.0","maxzoom":5}"""));

    db.Put(Encoding.UTF8.GetBytes("ContentsType"), Encoding.UTF8.GetBytes("TileTerrain"));

    Flush(db);
    Console.WriteLine($"bbb: gzip terrain 타일 {count}장 + layer.json");
}

static RocksDb Open(string path)
{
    // 빌더와 같은 조건을 만들기 위해 일부러 **기본 옵션**으로 만든다.
    // (블룸 필터도 블록 캐시 설정도 없는 상태. 서버가 그걸 전제로 동작하는지 보려는 것이다.)
    if (Directory.Exists(path))
        Directory.Delete(path, recursive: true);

    Directory.CreateDirectory(path);
    return RocksDb.Open(new DbOptions().SetCreateIfMissing(true), path);
}

static void Flush(RocksDb db) => db.Flush(new FlushOptions().SetWaitForFlush(true));

static byte[] TileKey(byte kind, byte level, uint col, uint row)
{
    var key = new byte[10];
    key[0] = kind;
    key[1] = level;
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2, 4), col);
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(6, 4), row);
    return key;
}

static byte[] FakeJpeg(byte level, uint col, uint row)
{
    // 실제 이미지는 아니지만 JPEG 시작 표시(FF D8 FF)로 시작하게 둔다.
    // gzip 오탐이 없는지도 이걸로 함께 확인된다.
    var body = Encoding.ASCII.GetBytes($"jpg {level}/{col}/{row}");
    var bytes = new byte[3 + body.Length];
    bytes[0] = 0xFF;
    bytes[1] = 0xD8;
    bytes[2] = 0xFF;
    body.CopyTo(bytes, 3);
    return bytes;
}

static byte[] FakeTerrain(byte level, uint col, uint row) =>
    Encoding.ASCII.GetBytes($"terrain {level}/{col}/{row} " + new string('h', 200));

static byte[] Gzip(byte[] raw)
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        gzip.Write(raw);

    return output.ToArray();
}
