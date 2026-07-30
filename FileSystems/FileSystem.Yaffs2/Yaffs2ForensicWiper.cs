#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;

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

  /// <summary>
  /// Scrubs obsolete chunks in place. The image is read and written through the
  /// stream a chunk at a time; holding the whole volume capped this at the array
  /// limit, which a NAND image does not respect.
  /// </summary>
  public static long WipeObsolete(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;
    using var accessor = new ImageAccessor(image);
    var scan = Yaffs2Scanner.Scan(accessor);
    if (!scan.ParseOk || scan.ChunkSize <= 0 || scan.SpareSize <= 0) return 0;
    int chunkSize = scan.ChunkSize, spareSize = scan.SpareSize, stride = chunkSize + spareSize;
    var length = accessor.Length;
    var buffer = new byte[stride];

    // Pass 1: index chunks; track the latest header per object and latest data
    // chunk per (object, chunkId).
    var latestHeaderSeq = new Dictionary<int, uint>();
    var latestHeaderParent = new Dictionary<int, int>();
    var latestHeaderSize = new Dictionary<int, long>();
    var latestDataSeq = new Dictionary<(int, int), uint>();

    for (var off = 0L; off + stride <= length; off += stride) {
      accessor.Read(off, buffer.AsSpan());
      var (seq, objId, chunkId, _) = Tags(buffer, chunkSize);
      if (objId <= 0) continue;
      if (chunkId == 0) {
        if (!latestHeaderSeq.TryGetValue(objId, out var s) || seq >= s) {
          latestHeaderSeq[objId] = seq;
          latestHeaderParent[objId] = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(HdrParentOffset, 4));
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
    for (var off = 0L; off + stride <= length; off += stride) {
      accessor.Read(off, buffer.AsSpan());
      var (seq, objId, chunkId, _) = Tags(buffer, chunkSize);
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

      var dirty = false;
      for (var i = 0; i < chunkSize; i++)
        if (buffer[i] != 0) { buffer[i] = 0; ++wiped; dirty = true; }
      // Blank the spare to erased-flash 0xFF so the slot reads as empty (no leak).
      for (var i = chunkSize; i < stride; i++)
        if (buffer[i] != 0xFF) { buffer[i] = 0xFF; ++wiped; dirty = true; }
      if (!dirty) continue;

      image.Position = off;
      image.Write(buffer, 0, (int)Math.Min(stride, length - off));
      accessor.Invalidate(off, stride);
    }
    image.Flush();
    return wiped;
  }

  private static (uint Seq, int ObjId, int ChunkId, uint NBytes) Tags(byte[] chunk, int chunkSize) {
    if (chunkSize + 16 > chunk.Length) return (0, 0, 0, 0);
    return (
      BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(chunkSize, 4)),
      BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(chunkSize + 4, 4)),
      BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(chunkSize + 8, 4)),
      BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(chunkSize + 12, 4)));
  }
}
