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

    // Score over a fixed byte span rather than a fixed number of strides, so a
    // coarse layout whose stride is a multiple of the true stride cannot win by
    // sampling fewer-but-aligned chunks. A header chunk only counts when the
    // packed spare tags corroborate it (chunk_id == 0 and a plausible object id);
    // a misaligned layout reads its "spare" from the middle of real data, so the
    // tags do not line up and it scores far lower than the genuine layout.
    const int ScanSpan = 1 << 20; // 1 MiB window is enough to distinguish layouts.
    var limit = Math.Min(image.Length, ScanSpan);

    var score = 0;
    for (var off = 0; off + stride <= limit; off += stride) {
      var hdr = ParseHeader(image.Slice(off, chunk));
      if (hdr == null) continue;
      if ((int)hdr.Type is < 1 or > 5) continue;

      var (objId, chunkId, _) = ParseSpare(image.Slice(off + chunk, spare));
      // Genuine object-header chunks carry chunk_id == 0 and a non-zero object id
      // in the spare. Reward that corroboration heavily so the correct geometry
      // outscores any stride that merely happens to land on header bytes.
      if (chunkId == 0 && objId > 0)
        score += 4;
      else
        ++score;
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

  /// <summary>Sentinel parent id signalling "this object has been deleted".
  /// Matches <see cref="Yaffs2InPlaceModifier.TombstoneParentId"/> so a tombstone
  /// header written by the in-place modifier is recognised here.</summary>
  internal const int TombstoneParentId = unchecked((int)0xFFFFFFFE);

  private static void DecodeAll(ReadOnlySpan<byte> image, int chunkSize, int spareSize, ScanResult result) {
    var stride = chunkSize + spareSize;
    // YAFFS2 is log-structured. The same (objectId, chunkId) may appear multiple
    // times — each with a distinct seqNumber. The chunk with the HIGHEST
    // seqNumber wins; older copies are obsolete but stay byte-identical at their
    // original offsets until garbage collection.
    //
    // For object headers (chunkId == 0) this lets the in-place modifier emit
    // tombstones (header with parentId == TombstoneParentId) without touching
    // old bytes — the latest-wins rule collapses the object's view to "gone".
    //
    // For data chunks (chunkId > 0) this lets the modifier rewrite parts of a
    // file by appending a fresh chunk at the next free slot. Older versions
    // remain on the medium but are filtered out here.
    //
    // Spare layout (packed_tags2, 32 bytes):
    //   seq_number  u32  offset 0
    //   obj_id      u32  offset 4
    //   chunk_id    u32  offset 8
    //   n_bytes     u32  offset 12
    //   ecc[3]      u32x3 offset 16..28
    var headers = new Dictionary<int, (uint Seq, HeaderRaw Hdr)>();
    var dataChunks = new Dictionary<(int ObjId, int ChunkId), (uint Seq, byte[] Bytes)>();
    var fallbackHeaderCounter = 2;

    for (var off = 0; off + stride <= image.Length; off += stride) {
      var chunk = image.Slice(off, chunkSize);
      var spare = image.Slice(off + chunkSize, spareSize);
      var (seqNumber, objId, chunkId, nBytes) = ParseSpareWithSeq(spare);

      if (chunkId == 0) {
        var hdr = ParseHeader(chunk);
        if (hdr == null) continue;
        var effectiveObjId = objId != 0 ? objId : fallbackHeaderCounter++;
        // Latest-wins per objectId by seqNumber. Ties: last writer in image order
        // wins, matching log-append order on real flash.
        if (!headers.TryGetValue(effectiveObjId, out var existing) || seqNumber >= existing.Seq)
          headers[effectiveObjId] = (seqNumber, hdr);
      } else if (objId != 0 && nBytes > 0 && nBytes <= chunkSize) {
        var key = (objId, chunkId);
        var payload = chunk.Slice(0, (int)Math.Min(nBytes, chunk.Length)).ToArray();
        if (!dataChunks.TryGetValue(key, out var existing) || seqNumber >= existing.Seq)
          dataChunks[key] = (seqNumber, payload);
      }
    }

    // Emit objects in objectId order so callers see a stable layout.
    var sortedHeaders = headers.OrderBy(kv => kv.Key).ToList();
    foreach (var (objectId, (_, hdr)) in sortedHeaders) {
      // Tombstone: header with parent id == TombstoneParentId means the object
      // has been deleted. Skip it (and its data chunks) entirely.
      if (hdr.ParentId == TombstoneParentId) continue;
      result.Objects.Add(new ObjectEntry(
        ObjectId: objectId,
        ParentId: hdr.ParentId,
        Type: hdr.Type,
        Name: hdr.Name,
        Size: hdr.Size));
    }

    // Build per-object data chunk lists, bounded by the file's declared size so
    // shrinking replacements correctly drop now-stale tail chunks of the prior
    // version. Chunks are emitted in chunkId order for deterministic reads.
    var liveObjectIds = new Dictionary<int, HeaderRaw>();
    foreach (var (objectId, (_, hdr)) in sortedHeaders)
      if (hdr.ParentId != TombstoneParentId)
        liveObjectIds[objectId] = hdr;

    foreach (var group in dataChunks
               .Where(kv => liveObjectIds.ContainsKey(kv.Key.ObjId))
               .GroupBy(kv => kv.Key.ObjId)) {
      var hdr = liveObjectIds[group.Key];
      // Cap at ceil(size / chunkSize). Files of size 0 carry no data chunks.
      var maxChunkId = hdr.Size <= 0 ? 0 : (int)((hdr.Size + chunkSize - 1) / chunkSize);
      var ordered = group
        .Where(kv => kv.Key.ChunkId <= maxChunkId)
        .OrderBy(kv => kv.Key.ChunkId)
        .Select(kv => kv.Value.Bytes)
        .ToList();
      if (ordered.Count > 0)
        result.DataChunks[group.Key] = ordered;
    }
  }

  private static (uint Seq, int ObjId, int ChunkId, uint NBytes) ParseSpareWithSeq(ReadOnlySpan<byte> spare) {
    if (spare.Length < 16) return (0, 0, 0, 0);
    try {
      var seq = BinaryPrimitives.ReadUInt32LittleEndian(spare.Slice(0, 4));
      var objId = BinaryPrimitives.ReadInt32LittleEndian(spare.Slice(4, 4));
      var chunkId = BinaryPrimitives.ReadInt32LittleEndian(spare.Slice(8, 4));
      var nBytes = BinaryPrimitives.ReadUInt32LittleEndian(spare.Slice(12, 4));
      if (objId is < 0 or > 1_000_000) objId = 0;
      if (chunkId is < 0 or > 1_000_000) chunkId = 0;
      return (seq, objId, chunkId, nBytes);
    } catch {
      return (0, 0, 0, 0);
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
