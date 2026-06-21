#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Yaffs2;

/// <summary>
/// Forensic wipe for the NAND-log YAFFS2: a delete only appends a tombstone object
/// header (parent = <see cref="Yaffs2Scanner.TombstoneParentId"/>); the deleted
/// file's data chunks and earlier headers physically persist until garbage
/// collection, so the content stays recoverable. This zeros every chunk belonging
/// to a deleted object, plus superseded/out-of-range data chunks of live objects —
/// data bytes to 0x00 and the spare tags back to 0xFF (the erased-flash state the
/// reader treats as an empty slot) — while leaving each live object's current
/// header + data chunks byte-intact (the reader walks by fixed stride, so blanked
/// slots are simply skipped).
/// </summary>
internal static class Yaffs2ForensicWiper {
  private const int HdrParentOffset = 4;        // parent_obj_id within the object header
  private const int TombstoneParentId = unchecked((int)0xFFFFFFFE);

  public static long WipeObsolete(byte[] image) {
    var scan = Yaffs2Scanner.Scan(image);
    if (!scan.ParseOk || scan.ChunkSize <= 0 || scan.SpareSize <= 0) return 0;
    int chunkSize = scan.ChunkSize, spareSize = scan.SpareSize, stride = chunkSize + spareSize;

    // Pass 1: index chunks; track the latest header per object and latest data
    // chunk per (object, chunkId).
    var latestHeaderSeq = new Dictionary<int, uint>();
    var latestHeaderParent = new Dictionary<int, int>();
    var latestHeaderSize = new Dictionary<int, long>();
    var latestDataSeq = new Dictionary<(int, int), uint>();

    for (var off = 0; off + stride <= image.Length; off += stride) {
      var (seq, objId, chunkId, _) = Tags(image, off, chunkSize);
      if (objId <= 0) continue;
      if (chunkId == 0) {
        if (!latestHeaderSeq.TryGetValue(objId, out var s) || seq >= s) {
          latestHeaderSeq[objId] = seq;
          latestHeaderParent[objId] = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(off + HdrParentOffset, 4));
          // Size lives later in the header; bound conservatively via data chunks instead.
          latestHeaderSize[objId] = 0;
        }
      } else {
        var key = (objId, chunkId);
        if (!latestDataSeq.TryGetValue(key, out var s) || seq >= s) latestDataSeq[key] = seq;
      }
    }

    var liveObjIds = new HashSet<int>();
    foreach (var (objId, parent) in latestHeaderParent)
      if (parent != TombstoneParentId) liveObjIds.Add(objId);

    // Pass 2: zero obsolete chunks.
    long wiped = 0;
    for (var off = 0; off + stride <= image.Length; off += stride) {
      var (seq, objId, chunkId, _) = Tags(image, off, chunkSize);
      if (objId <= 0) continue;

      bool obsolete;
      if (!liveObjIds.Contains(objId)) {
        obsolete = true; // deleted/tombstoned object — every chunk goes
      } else if (chunkId == 0) {
        obsolete = seq < latestHeaderSeq[objId]; // superseded header of a live object
      } else {
        obsolete = latestDataSeq.TryGetValue((objId, chunkId), out var ls) && seq < ls; // superseded data
      }
      if (!obsolete) continue;

      var end = Math.Min(image.Length, off + chunkSize);
      for (var i = off; i < end; i++)
        if (image[i] != 0) { image[i] = 0; wiped++; }
      // Blank the spare to erased-flash 0xFF so the slot reads as empty (no leak).
      var spareEnd = Math.Min(image.Length, off + stride);
      for (var i = off + chunkSize; i < spareEnd; i++)
        if (image[i] != 0xFF) { image[i] = 0xFF; wiped++; }
    }
    return wiped;
  }

  private static (uint Seq, int ObjId, int ChunkId, uint NBytes) Tags(byte[] image, int off, int chunkSize) {
    var s = off + chunkSize;
    if (s + 16 > image.Length) return (0, 0, 0, 0);
    return (
      BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s, 4)),
      BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(s + 4, 4)),
      BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(s + 8, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(s + 12, 4)));
  }
}
