#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.Fat;

namespace Compression.Lib.FsConversion;

/// <summary>
/// In-place FAT12/16/32 shrink / grow primitive. Operates directly on a Stream
/// (no full image load) so it can be driven over a partition substream.
///
/// <para>The shrink algorithm walks the FAT chain, identifies any cluster above
/// the new size boundary that is still allocated, and uses
/// <see cref="FatBlockMover"/> to migrate those clusters to free slots below
/// the boundary. Once nothing references an above-boundary cluster the BPB
/// total-sectors field is patched and the stream is truncated.</para>
///
/// <para>The grow algorithm extends the stream length, patches BPB
/// total-sectors, and (per spec) does NOT resize the FAT table itself — that
/// would require an in-place format conversion when the cluster count crosses
/// a FAT12/16/32 threshold, which is out of scope. The new tail bytes become
/// inaccessible until a new FAT entry is added by a higher-level allocator,
/// but the on-disk FS is consistent.</para>
///
/// <para><b>Crash semantics:</b> writes go through the same targeted-write
/// path used by the defrag mover (<see cref="FatBlockMover.MoveExtent"/> +
/// <see cref="FatBlockMover.UpdateAllocationAfterMove"/>), so each cluster
/// migration is itself crash-safe. A crash mid-resize leaves the FS
/// readable; the BPB is only patched at the end, so a half-finished resize
/// looks like the original FS with some now-orphaned data on disk (or an
/// already-truncated FS — both fsck-recoverable).</para>
/// </summary>
public static class FatResizer {

  /// <summary>
  /// Shrinks a FAT image to <paramref name="newSizeBytes"/>. The new size must
  /// be sector-aligned and large enough to hold all currently-used clusters
  /// (after migrating any above-boundary clusters down).
  /// </summary>
  /// <exception cref="InvalidDataException">Stream is not a recognisable FAT image.</exception>
  /// <exception cref="InvalidOperationException">New size cannot fit existing payload.</exception>
  public static void Shrink(Stream image, long newSizeBytes) {
    ArgumentNullException.ThrowIfNull(image);
    if (newSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newSizeBytes));

    var ctx = ReadContext(image);
    if (newSizeBytes >= image.Length) return; // No-op: target is not smaller.
    if (newSizeBytes % ctx.BytesPerSector != 0)
      throw new ArgumentException(
        $"New size must be a multiple of sector size ({ctx.BytesPerSector}).", nameof(newSizeBytes));

    var newTotalSectors = (int)(newSizeBytes / ctx.BytesPerSector);
    // The new boundary must leave room for the BPB, FATs, and root-dir region.
    if (newTotalSectors <= ctx.FirstDataSector)
      throw new InvalidOperationException(
        $"New size ({newSizeBytes} bytes / {newTotalSectors} sectors) is too small to hold FAT metadata " +
        $"(first data sector = {ctx.FirstDataSector}).");

    // The highest cluster that still fits below the new boundary, inclusive.
    // Cluster `c` lives at sectors firstDataSector + (c-2)*spc ..
    //                          firstDataSector + (c-1)*spc - 1.
    var maxClusterInside = (newTotalSectors - ctx.FirstDataSector) / ctx.SectorsPerCluster + 1;
    if (maxClusterInside < 2)
      throw new InvalidOperationException("New size leaves zero data clusters.");

    // Walk the FAT, identify all used clusters and the set of free clusters
    // inside the new boundary that we can use as relocation targets.
    var fatBase = (long)ctx.ReservedSectors * ctx.BytesPerSector;
    var usedAbove = new List<int>();
    var freeBelow = new Queue<int>();
    for (var c = 2; c < ctx.TotalDataClusters + 2; c++) {
      var v = ReadFatEntryStream(image, fatBase, c, ctx.FatType);
      if (c > maxClusterInside) {
        if (v != 0) usedAbove.Add(c);
      } else {
        if (v == 0) freeBelow.Enqueue(c);
      }
    }

    if (usedAbove.Count > freeBelow.Count)
      throw new InvalidOperationException(
        $"Cannot shrink to {newSizeBytes} bytes: {usedAbove.Count} clusters live above the new boundary " +
        $"but only {freeBelow.Count} free clusters are available below it. Defragment first or pick a larger target.");

    // Migrate every above-boundary cluster down. We need to do this one
    // chain at a time so the FAT chain stays consistent. Group the
    // above-boundary clusters by the file they belong to.
    if (usedAbove.Count > 0) {
      var mover = new FatBlockMover();
      mover.Init(image);
      MigrateClustersDown(image, mover, ctx, usedAbove, freeBelow);
    }

    // Now every used cluster lives below the boundary. Zero the FAT entries
    // for clusters above the boundary (they may still hold stale chain links
    // or EOC markers from the migrated chains).
    for (var c = maxClusterInside + 1; c < ctx.TotalDataClusters + 2; c++) {
      for (var fatIdx = 0; fatIdx < ctx.FatCount; fatIdx++) {
        var fb = fatBase + (long)fatIdx * ctx.FatSize * ctx.BytesPerSector;
        WriteFatEntryStream(image, fb, c, 0, ctx.FatType);
      }
    }
    image.Flush();

    // Update BPB total sectors. This is a single targeted write to bytes
    // 19 (16-bit) and 32..35 (32-bit). The 16-bit field is used when the
    // count fits and the FS is not FAT32, per Microsoft's BPB convention.
    UpdateBpbTotalSectors(image, ctx, newTotalSectors);
    image.Flush();

    // Finally, truncate the stream. After this step the partition is at its
    // new size; any reader will see a consistent, smaller FS.
    image.SetLength(newSizeBytes);
    image.Flush();
  }

  /// <summary>
  /// Grows a FAT image to <paramref name="newSizeBytes"/>. The new tail is
  /// zero-filled (so the cluster-bitmap-style FAT entries that fall there
  /// read as free). The on-disk FAT table itself is NOT resized — see class
  /// remarks.
  /// </summary>
  public static void Grow(Stream image, long newSizeBytes) {
    ArgumentNullException.ThrowIfNull(image);
    if (newSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newSizeBytes));

    var ctx = ReadContext(image);
    if (newSizeBytes <= image.Length) return; // No-op: target is not larger.
    if (newSizeBytes % ctx.BytesPerSector != 0)
      throw new ArgumentException(
        $"New size must be a multiple of sector size ({ctx.BytesPerSector}).", nameof(newSizeBytes));

    var newTotalSectors = (int)(newSizeBytes / ctx.BytesPerSector);

    // Cluster count after grow — used to ensure we don't cross a FAT type
    // boundary (which would require an in-place format conversion, OOS).
    var newTotalDataClusters = (newTotalSectors - ctx.FirstDataSector) / ctx.SectorsPerCluster;
    var newFatType = newTotalDataClusters < 4085 ? 12 : newTotalDataClusters < 65525 ? 16 : 32;
    if (newFatType != ctx.FatType)
      throw new NotSupportedException(
        $"Growing this image would change the FAT type from {ctx.FatType} to {newFatType}; " +
        "in-place FAT type conversion is out of scope. Defrag-then-rebuild instead.");

    // Step 1: physically grow the stream. The new sectors are zero per
    // SetLength contract on FileStream and MemoryStream.
    var oldLength = image.Length;
    image.SetLength(newSizeBytes);
    // Explicitly zero the new region — not all Stream impls guarantee
    // zero-init on grow, and we need free clusters to read as 0.
    ZeroRange(image, oldLength, newSizeBytes - oldLength);
    image.Flush();

    // Step 2: patch BPB total sectors so the FS exposes the new geometry.
    UpdateBpbTotalSectors(image, ctx, newTotalSectors);
    image.Flush();
  }

  // ── Implementation helpers ───────────────────────────────────────────────

  private sealed class FatContext {
    public int BytesPerSector;
    public int SectorsPerCluster;
    public int ReservedSectors;
    public int FatCount;
    public int FatSize;
    public int RootEntryCount;
    public int RootDirSectors;
    public int TotalSectors;
    public int FirstDataSector;
    public int TotalDataClusters;
    public int FatType;
  }

  private static FatContext ReadContext(Stream image) {
    if (image.Length < 512) throw new InvalidDataException("FAT: image too small.");
    image.Position = 0;
    Span<byte> bpb = stackalloc byte[512];
    image.ReadExactly(bpb);

    var ctx = new FatContext {
      BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bpb[11..]),
      SectorsPerCluster = bpb[13],
      ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bpb[14..]),
      FatCount = bpb[16],
      RootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(bpb[17..]),
    };
    if (ctx.BytesPerSector is 0 or > 4096) ctx.BytesPerSector = 512;
    if (ctx.SectorsPerCluster == 0) ctx.SectorsPerCluster = 1;
    if (ctx.FatCount == 0) ctx.FatCount = 2;

    var ts16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[19..]);
    ctx.TotalSectors = ts16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(bpb[32..]) : ts16;

    var fs16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[22..]);
    ctx.FatSize = fs16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(bpb[36..]) : fs16;

    ctx.RootDirSectors = (ctx.RootEntryCount * 32 + ctx.BytesPerSector - 1) / ctx.BytesPerSector;
    ctx.FirstDataSector = ctx.ReservedSectors + ctx.FatCount * ctx.FatSize + ctx.RootDirSectors;
    ctx.TotalDataClusters = (ctx.TotalSectors - ctx.FirstDataSector) / ctx.SectorsPerCluster;
    ctx.FatType = ctx.TotalDataClusters < 4085 ? 12 : ctx.TotalDataClusters < 65525 ? 16 : 32;

    if (ctx.FatSize <= 0 || ctx.FirstDataSector <= 0)
      throw new InvalidDataException("FAT: implausible BPB (corrupt or non-FAT image).");
    return ctx;
  }

  private static void UpdateBpbTotalSectors(Stream image, FatContext ctx, int newTotalSectors) {
    image.Position = 19;
    Span<byte> ts16 = stackalloc byte[2];
    Span<byte> ts32 = stackalloc byte[4];

    if (ctx.FatType != 32 && newTotalSectors < 65536) {
      BinaryPrimitives.WriteUInt16LittleEndian(ts16, (ushort)newTotalSectors);
      image.Position = 19;
      image.Write(ts16);
      BinaryPrimitives.WriteUInt32LittleEndian(ts32, 0u);
      image.Position = 32;
      image.Write(ts32);
    } else {
      BinaryPrimitives.WriteUInt16LittleEndian(ts16, 0);
      image.Position = 19;
      image.Write(ts16);
      BinaryPrimitives.WriteUInt32LittleEndian(ts32, (uint)newTotalSectors);
      image.Position = 32;
      image.Write(ts32);
    }
  }

  private static void MigrateClustersDown(Stream image, FatBlockMover mover, FatContext ctx,
      List<int> usedAbove, Queue<int> freeBelow) {
    // Group above-boundary clusters by the chain they belong to. We walk
    // every used chain in the FAT, find the directory entry that points
    // at it (start-cluster), then call UpdateAllocationScattered to remap
    // the whole chain — old clusters left intact, new clusters chained
    // in the same order, dir entry repointed at the new head.
    var fatBase = (long)ctx.ReservedSectors * ctx.BytesPerSector;
    var aboveSet = new HashSet<int>(usedAbove);
    var clusterToHead = BuildClusterToChainHeadMap(image, ctx);

    // Bucket the above-boundary clusters by their chain head.
    var byHead = new Dictionary<int, List<int>>();
    foreach (var c in usedAbove) {
      var head = clusterToHead.GetValueOrDefault(c, c);
      if (!byHead.TryGetValue(head, out var lst)) byHead[head] = lst = [];
      lst.Add(c);
    }

    foreach (var (head, _) in byHead) {
      // Reconstruct the full chain for this file.
      var oldChain = new List<int>();
      var c = head;
      var seen = new HashSet<int>();
      while (c >= 2 && c <= ctx.TotalDataClusters + 1 && !IsEoc(c, ctx.FatType) && seen.Add(c)) {
        oldChain.Add(c);
        c = ReadFatEntryStream(image, fatBase, c, ctx.FatType);
      }

      // Build the new chain: keep below-boundary clusters in place, swap
      // each above-boundary one with a free below-boundary slot.
      var newChain = new List<int>(oldChain.Count);
      foreach (var cl in oldChain) {
        if (!aboveSet.Contains(cl)) {
          newChain.Add(cl);
          continue;
        }
        if (freeBelow.Count == 0)
          throw new InvalidOperationException("Ran out of free clusters below the new boundary during migration.");
        var dest = freeBelow.Dequeue();
        // Physically copy the cluster bytes from old → new location.
        var srcOff = ctx.FirstDataSector * (long)ctx.BytesPerSector
                     + (long)(cl - 2) * ctx.SectorsPerCluster * ctx.BytesPerSector;
        var dstOff = ctx.FirstDataSector * (long)ctx.BytesPerSector
                     + (long)(dest - 2) * ctx.SectorsPerCluster * ctx.BytesPerSector;
        mover.MoveExtent(image, srcOff, dstOff,
          (long)ctx.SectorsPerCluster * ctx.BytesPerSector, zeroSource: false);
        newChain.Add(dest);
      }

      // Patch FAT + dir entry. UpdateAllocationScattered handles the
      // three-step crash-safe metadata update (write new chain → patch dir →
      // free old). We pass "*" as the file name — PatchDirectoryEntriesStream
      // treats it as a wildcard and matches the first directory entry whose
      // start-cluster equals oldFirst (which is unique per file).
      mover.UpdateAllocationScattered(image, "*", oldChain, newChain);
    }
  }

  private static Dictionary<int, int> BuildClusterToChainHeadMap(Stream image, FatContext ctx) {
    // For every cluster, find the chain head that contains it. We walk every
    // cluster's FAT entry, identify chain starts (clusters whose value is
    // referenced by no other cluster), then walk forward from each start.
    var fatBase = (long)ctx.ReservedSectors * ctx.BytesPerSector;
    var referenced = new HashSet<int>();
    for (var c = 2; c < ctx.TotalDataClusters + 2; c++) {
      var v = ReadFatEntryStream(image, fatBase, c, ctx.FatType);
      if (v >= 2 && v <= ctx.TotalDataClusters + 1 && !IsEoc(v, ctx.FatType))
        referenced.Add(v);
    }

    var map = new Dictionary<int, int>();
    for (var c = 2; c < ctx.TotalDataClusters + 2; c++) {
      if (referenced.Contains(c)) continue;
      var v = ReadFatEntryStream(image, fatBase, c, ctx.FatType);
      if (v == 0) continue; // free
      // c is a chain head. Walk it.
      var cur = c;
      var seen = new HashSet<int>();
      while (cur >= 2 && cur <= ctx.TotalDataClusters + 1 && !IsEoc(cur, ctx.FatType) && seen.Add(cur)) {
        map[cur] = c;
        cur = ReadFatEntryStream(image, fatBase, cur, ctx.FatType);
      }
    }
    return map;
  }

  private static void ZeroRange(Stream image, long offset, long length) {
    if (length <= 0) return;
    var buf = new byte[Math.Min((int)length, 64 * 1024)];
    var remaining = length;
    var pos = offset;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, buf.Length);
      image.Position = pos;
      image.Write(buf, 0, chunk);
      pos += chunk;
      remaining -= chunk;
    }
  }

  // ── FAT entry read/write (stream-based) ─────────────────────────────────

  private static int ReadFatEntryStream(Stream image, long fatBase, int cluster, int fatType) {
    Span<byte> buf = stackalloc byte[4];
    switch (fatType) {
      case 12: {
        var pos = fatBase + cluster * 3L / 2;
        if (pos + 2 > image.Length) return 0;
        image.Position = pos;
        image.ReadExactly(buf[..2]);
        var val = BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
        return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
      }
      case 16: {
        var pos = fatBase + cluster * 2L;
        if (pos + 2 > image.Length) return 0;
        image.Position = pos;
        image.ReadExactly(buf[..2]);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
      }
      default: {
        var pos = fatBase + cluster * 4L;
        if (pos + 4 > image.Length) return 0;
        image.Position = pos;
        image.ReadExactly(buf[..4]);
        return BinaryPrimitives.ReadInt32LittleEndian(buf[..4]) & 0x0FFFFFFF;
      }
    }
  }

  private static void WriteFatEntryStream(Stream image, long fatBase, int cluster, int value, int fatType) {
    switch (fatType) {
      case 12: {
        var pos = fatBase + cluster * 3L / 2;
        if (pos + 2 > image.Length) return;
        Span<byte> buf = stackalloc byte[2];
        image.Position = pos;
        image.ReadExactly(buf);
        if ((cluster & 1) == 0) {
          buf[0] = (byte)(value & 0xFF);
          buf[1] = (byte)((buf[1] & 0xF0) | ((value >> 8) & 0x0F));
        } else {
          buf[0] = (byte)((buf[0] & 0x0F) | ((value << 4) & 0xF0));
          buf[1] = (byte)((value >> 4) & 0xFF);
        }
        image.Position = pos;
        image.Write(buf);
        break;
      }
      case 16: {
        var pos = fatBase + cluster * 2L;
        if (pos + 2 > image.Length) return;
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)value);
        image.Position = pos;
        image.Write(buf);
        break;
      }
      default: {
        var pos = fatBase + cluster * 4L;
        if (pos + 4 > image.Length) return;
        Span<byte> buf = stackalloc byte[4];
        image.Position = pos;
        image.ReadExactly(buf);
        var existing = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        var newVal = (existing & 0xF0000000u) | ((uint)value & 0x0FFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(buf, newVal);
        image.Position = pos;
        image.Write(buf);
        break;
      }
    }
  }

  private static bool IsEoc(int cluster, int fatType) => fatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    _ => cluster >= 0x0FFFFFF8,
  };
}
