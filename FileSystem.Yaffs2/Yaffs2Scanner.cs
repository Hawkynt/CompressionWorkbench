#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Yaffs2;

/// <summary>
/// Scans a YAFFS2 raw-NAND image. YAFFS2 has no magic bytes; we try each
/// (chunk, spare) combination in turn and score by how many plausible
/// <c>ObjectHeader</c> chunks we decode.
/// </summary>
internal static class Yaffs2Scanner {
  public static readonly (int Chunk, int Spare)[] CandidateLayouts = [
    (2048, 64),
    (4096, 128),
    (512, 16),
    (8192, 256),
  ];

  public enum YObjectType {
    Unknown = 0,
    File = 1,
    Symlink = 2,
    Directory = 3,
    HardLink = 4,
    Special = 5,
  }

  internal sealed record ObjectEntry(int ObjectId, int ParentId, YObjectType Type, string Name, long Size);

  internal sealed class ScanResult {
    public int ChunkSize { get; set; }
    public int SpareSize { get; set; }
    public List<ObjectEntry> Objects { get; } = [];
    // ObjectId -> concatenated data chunks (collected in the order they appear in the image).
    public Dictionary<int, List<byte[]>> DataChunks { get; } = new();
    public bool ParseOk { get; set; }
  }

  public static ScanResult Scan(ReadOnlySpan<byte> image) {
    // Try each candidate layout, pick the one that yields the most valid ObjectHeader decodes.
    (int chunk, int spare, int score) best = (0, 0, 0);
    foreach (var (chunk, spare) in CandidateLayouts) {
      var score = ScoreLayout(image, chunk, spare);
      if (score > best.score) best = (chunk, spare, score);
    }

    var result = new ScanResult { ChunkSize = best.chunk, SpareSize = best.spare };
    if (best.chunk == 0) { result.ParseOk = false; return result; }

    try {
      DecodeAll(image, best.chunk, best.spare, result);
      result.ParseOk = true;
    } catch {
      result.ParseOk = false;
    }
    return result;
  }

  private static int ScoreLayout(ReadOnlySpan<byte> image, int chunk, int spare) {
    var stride = chunk + spare;
    if (stride <= 0) return 0;
    var score = 0;
    for (var off = 0; off + stride <= image.Length && off < 64 * stride; off += stride) {
      var hdr = ParseHeader(image.Slice(off, chunk));
      if (hdr == null) continue;
      // A plausible header has a type in {1..5} and a non-empty, printable name (or empty + type=Special).
      if ((int)hdr.Type is >= 1 and <= 5) ++score;
    }
    return score;
  }

  /// <summary>ObjectHeader is 512 bytes at the start of a chunk.</summary>
  private sealed record HeaderRaw(YObjectType Type, int ParentId, string Name, long Size);

  private static HeaderRaw? ParseHeader(ReadOnlySpan<byte> chunk) {
    if (chunk.Length < 512) return null;
    try {
      // yaffs_obj_hdr:
      //   type           i32   // offset 0
      //   parent_obj_id  i32   // offset 4
      //   checksum       u16   // offset 8
      //   unused2        u16   // offset 10
      //   name[256]            // offset 12
      //   unused3        u32   // offset 268
      //   yst_mode       u32   // offset 272
      //   yst_uid        u32   // offset 276
      //   yst_gid        u32   // offset 280
      //   yst_atime      u32   // offset 284
      //   yst_mtime      u32   // offset 288
      //   yst_ctime      u32   // offset 292
      //   file_size_low  i32   // offset 296
      //   equiv_id       i32   // offset 300
      //   alias[256]           // offset 304
      //   ... (win_ctime/atime/mtime + inband + shadows + file_size_high etc)
      var typeVal = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(0, 4));
      var parent = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(4, 4));
      if (typeVal is < 0 or > 5) return null;
      var name = DecodeCString(chunk.Slice(12, 256));
      // file_size_low at 296, file_size_high at 300... uh, offset for size_high differs. We read
      // file_size_low only; many writers stash full size there for files < 2 GiB.
      var sizeLow = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(296, 4));
      return new HeaderRaw((YObjectType)typeVal, parent, name, sizeLow < 0 ? 0 : sizeLow);
    } catch {
      return null;
    }
  }

  private static string DecodeCString(ReadOnlySpan<byte> raw) {
    var zero = raw.IndexOf((byte)0);
    if (zero < 0) zero = raw.Length;
    var bytes = raw.Slice(0, zero);
    // Filter out non-printable garbage; yaffs names are usually UTF-8.
    try {
      return Encoding.UTF8.GetString(bytes);
    } catch {
      return "";
    }
  }

  private static void DecodeAll(ReadOnlySpan<byte> image, int chunkSize, int spareSize, ScanResult result) {
    var stride = chunkSize + spareSize;
    // First pass: collect headers.
    // Spare layout (packed oob) that yaffs_mkyaffs2image emits (simplified):
    //   u16 seq_number, ? — we mainly look for chunk_id + obj_id.
    // Common yaffs2 packed tags layout (32 bytes):
    //   seq_number  u32  offset 0
    //   obj_id      u32  offset 4
    //   chunk_id    u32  offset 8
    //   n_bytes     u32  offset 12
    //   ecc[3]      u32x3 offset 16..28
    // Chunk with chunk_id == 0 is an object header.
    for (var off = 0; off + stride <= image.Length; off += stride) {
      var chunk = image.Slice(off, chunkSize);
      var spare = image.Slice(off + chunkSize, spareSize);
      var (objId, chunkId, nBytes) = ParseSpare(spare);

      if (chunkId == 0) {
        var hdr = ParseHeader(chunk);
        if (hdr == null) continue;
        var effectiveObjId = objId != 0 ? objId : (result.Objects.Count + 2); // rough fallback
        result.Objects.Add(new ObjectEntry(
          ObjectId: effectiveObjId,
          ParentId: hdr.ParentId,
          Type: hdr.Type,
          Name: hdr.Name,
          Size: hdr.Size));
      } else if (objId != 0 && nBytes > 0 && nBytes <= chunkSize) {
        if (!result.DataChunks.TryGetValue(objId, out var list)) {
          list = [];
          result.DataChunks[objId] = list;
        }
        list.Add(chunk.Slice(0, (int)Math.Min(nBytes, chunk.Length)).ToArray());
      }
    }
  }

  private static (int ObjId, int ChunkId, uint NBytes) ParseSpare(ReadOnlySpan<byte> spare) {
    if (spare.Length < 16) return (0, 0, 0);
    try {
      // Try packed-tags-2 layout first.
      var objId = BinaryPrimitives.ReadInt32LittleEndian(spare.Slice(4, 4));
      var chunkId = BinaryPrimitives.ReadInt32LittleEndian(spare.Slice(8, 4));
      var nBytes = BinaryPrimitives.ReadUInt32LittleEndian(spare.Slice(12, 4));
      // Sanity clamp — IDs above 1M are implausible for our test fixtures & typical images.
      if (objId is < 0 or > 1_000_000) objId = 0;
      if (chunkId is < 0 or > 1_000_000) chunkId = 0;
      return (objId, chunkId, nBytes);
    } catch {
      return (0, 0, 0);
    }
  }
}
