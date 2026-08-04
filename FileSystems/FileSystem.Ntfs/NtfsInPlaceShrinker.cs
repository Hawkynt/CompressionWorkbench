#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ntfs;

/// <summary>
/// Genuine in-place NTFS volume shrink. Reduces the volume to a smaller cluster
/// count by <b>relocating only the clusters that sit at or above the new boundary</b>
/// into free clusters below it, patching the owning attribute's data runs and the
/// $Bitmap, then trimming the image. Work is <c>O(clusters relocated + metadata
/// touched)</c> — clusters that already live below the boundary are never read or
/// rewritten, so the bytes below the boundary stay byte-identical.
///
/// <para><b>Algorithm</b></para>
/// <list type="number">
///   <item>Parse the boot sector. The image spans <c>(NumberSectors+1)/spc</c> total
///   volume clusters; the final cluster is reserved for the backup boot sector (its
///   last sector is the one <c>NumberSectors</c> excludes), so file data lives in
///   clusters <c>0..volumeClusters-2</c>.</item>
///   <item>Pick a target volume-cluster count. The data boundary is
///   <c>target-1</c> (the new last cluster stays reserved). Refuse (throw
///   <see cref="NotSupportedException"/>) if the in-use clusters cannot fit below the
///   boundary, or if a cluster at or above it belongs to a compressed/sparse $DATA
///   stream this shrinker cannot relocate.</item>
///   <item>Walk every MFT record's non-resident $DATA / $INDEX_ALLOCATION runs.
///   For every run that crosses the boundary, claim a contiguous free region below it
///   (falling back to per-cluster), copy the cluster bytes, rewrite the run list to
///   the new LCNs, and flip the old/new $Bitmap bits. The attribute is grown in place
///   (shifting the record tail) when the relocated run list needs more bytes. $MFT,
///   $MFTMirr, $LogFile, $Bitmap and the directory indices are handled by the same
///   record walk (records 0..15 included).</item>
///   <item>Reshape $Bitmap to the new size (reserve the new backup cluster, pad the
///   final bitmap byte), rewrite $Boot's <c>NumberSectors</c>, copy the boot sector to
///   the new last sector (the relocated backup boot), re-sync $MFTMirr, and truncate
///   the image to <c>target * clusterSize</c>.</item>
/// </list>
///
/// <para><b>Supported shrink shapes (in place, no re-pack)</b>: any target where the
/// highest in-use cluster can be relocated into free space below the boundary and
/// every relocated attribute is a plain (uncompressed, non-sparse) non-resident
/// stream. <b>Refused</b> (<see cref="NotSupportedException"/>): a target below the
/// in-use cluster count, or one that would require relocating a compressed/sparse
/// stream (those keep their on-disk LCNs and are left to the rebuild fallback).</para>
/// </summary>
public static class NtfsInPlaceShrinker {

  private const int FirstSystemRecord = 0;

  /// <summary>Result of a shrink attempt: the before/after byte sizes and how many bytes were physically rewritten.</summary>
  public readonly record struct ShrinkResult(long OriginalSize, long NewSize, long BytesRelocated, long ClustersRelocated) {
    /// <summary>True when the image was actually made smaller.</summary>
    public bool WasReduced => this.NewSize < this.OriginalSize;
  }

  /// <summary>
  /// Shrinks an NTFS image in place to the smallest cluster count that still holds
  /// the current allocation (auto-fit). Equivalent to
  /// <see cref="ShrinkToClusters(byte[], long)"/> with the tightest legal target.
  /// </summary>
  /// <param name="image">The full NTFS image bytes (modified in place; the array is not resized — read <see cref="ShrinkResult.NewSize"/> and truncate the backing store to it).</param>
  /// <returns>The shrink result. <see cref="ShrinkResult.NewSize"/> is the number of valid bytes at the front of <paramref name="image"/>.</returns>
  public static ShrinkResult ShrinkToFit(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    var geo = ParseBoot(image);
    // Boundary = one past the highest cluster either marked in $Bitmap OR referenced
    // by any record's run list. The bitmap and the run lists can disagree by a
    // cluster (mkfs reserves a trailing run it doesn't bitmap, or vice-versa); take
    // the max so nothing is left dangling above the trim point.
    var highest = Math.Max(HighestAllocatedCluster(image, geo), HighestReferencedCluster(image, geo));
    // The volume's LAST cluster is reserved for the backup boot sector, so it cannot
    // also hold file data. Total volume clusters = highest data cluster + 1 data
    // headroom + 1 reserved = highest + 2.
    var target = highest + 2;
    return ShrinkToClusters(image, target);
  }

  /// <summary>
  /// Shrinks an NTFS image in place to exactly <paramref name="targetClusters"/>
  /// clusters, relocating any allocation at or above the boundary into free space
  /// below it.
  /// </summary>
  /// <param name="image">The full NTFS image bytes, modified in place.</param>
  /// <param name="targetClusters">The desired new total volume cluster count (image size = targetClusters * clusterSize). The last cluster stays reserved for the backup boot sector.</param>
  /// <returns>The shrink result; <see cref="ShrinkResult.NewSize"/> is the new valid byte length.</returns>
  /// <exception cref="NotSupportedException">If the allocation cannot fit below the boundary, or a relocate-needing stream is compressed/sparse.</exception>
  public static ShrinkResult ShrinkToClusters(byte[] image, long targetClusters) {
    ArgumentNullException.ThrowIfNull(image);
    var geo = ParseBoot(image);
    var originalSize = image.Length;

    // geo.VolumeClusters is the current total volume cluster count (image clusters).
    if (targetClusters <= 0 || targetClusters >= geo.VolumeClusters)
      return new ShrinkResult(originalSize, originalSize, 0, 0);

    // The volume's last cluster (index targetClusters-1) is reserved for the backup
    // boot sector, so file data must live in 0..targetClusters-2. The relocation
    // boundary is therefore targetClusters-1: anything at or above it must move down.
    var dataBoundary = targetClusters - 1;

    // The number of currently-allocated DATA clusters must fit below the boundary
    // (the reserved backup cluster is counted separately).
    var inUse = CountAllocatedClusters(image, geo) - 1; // minus the old backup cluster
    if (inUse > dataBoundary)
      throw new NotSupportedException(
        $"NTFS shrink: {inUse} data clusters in use cannot fit below a {dataBoundary}-cluster boundary.");

    // Relocate every allocated cluster at LCN >= dataBoundary down to a free LCN below it.
    var relocated = RelocateClustersAboveBoundary(image, geo, dataBoundary);

    // Reshape $Bitmap: clear bits past the new volume, reserve the new backup-boot
    // cluster (index targetClusters-1), and shrink the bitmap's valid length.
    TrimBitmap(image, geo, targetClusters);

    // Total volume clusters = targetClusters; NumberSectors drops the final backup
    // sector exactly as mkfs.ntfs does: NumberSectors = volumeClusters*spc - 1.
    var newSize = targetClusters * (long)geo.ClusterSize;
    var newNumberSectors = targetClusters * geo.SectorsPerCluster - 1;
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(40), newNumberSectors);

    // Backup boot sector lives in the new last sector (= newNumberSectors index),
    // which is the last sector of the reserved final cluster.
    var backupOff = newNumberSectors * (long)geo.BytesPerSector;
    if (backupOff >= 0 && backupOff + geo.BytesPerSector <= newSize)
      image.AsSpan(0, geo.BytesPerSector).CopyTo(image.AsSpan((int)backupOff, geo.BytesPerSector));

    // Record 0 ($MFT) and any low records may have changed; re-sync $MFTMirr.
    SyncMftMirror(image, geo);

    return new ShrinkResult(originalSize, newSize, relocated.bytes, relocated.clusters);
  }

  // ── Relocation core ───────────────────────────────────────────────────────

  // Walks all MFT records; for each non-resident $DATA / $INDEX_ALLOCATION /
  // $BITMAP run-list cluster at LCN >= boundary, relocates it below the boundary.
  private static (long bytes, long clusters) RelocateClustersAboveBoundary(byte[] image, Geo geo, long boundary) {
    long bytes = 0, clusters = 0;
    var slots = MftSlotCount(image, geo);

    for (var slot = FirstSystemRecord; slot < slots; slot++) {
      var recOff = (int)MftRecordOffset(image, geo, slot);
      if (recOff < 0 || recOff + geo.MftRecordSize > image.Length) continue;
      if (image[recOff] != 'F' || image[recOff + 1] != 'I' || image[recOff + 2] != 'L' || image[recOff + 3] != 'E')
        continue;

      // Re-read & fix up a working copy each pass — earlier relocations may have
      // already rewritten this record (e.g. $MFT's own $DATA run after moving an
      // MFT cluster).
      var rec = image.AsSpan(recOff, geo.MftRecordSize).ToArray();
      ApplyFixup(rec);
      if ((BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(22)) & 0x01) == 0) continue; // not in use

      foreach (var attrType in new uint[] { 0x80, 0xA0, 0xB0 }) {
        var changed = RelocateAttributeRuns(image, geo, slot, attrType, boundary, ref bytes, ref clusters);
        if (changed) {
          // Reload because the record bytes on disk just changed.
          rec = image.AsSpan(recOff, geo.MftRecordSize).ToArray();
          ApplyFixup(rec);
        }
      }
    }
    return (bytes, clusters);
  }

  // Relocates run clusters >= boundary for one attribute type in one record. Returns
  // true if it rewrote the record. May be called repeatedly until no run remains
  // above the boundary (it relocates all qualifying runs in a single pass).
  private static bool RelocateAttributeRuns(byte[] image, Geo geo, int slot, uint attrType,
      long boundary, ref long bytes, ref long clusters) {
    var recOff = (int)MftRecordOffset(image, geo, slot);
    var rec = image.AsSpan(recOff, geo.MftRecordSize).ToArray();
    ApplyFixup(rec);

    var (attrPos, _) = FindAttr(rec, attrType, unnamedOnly: attrType == 0x80);
    if (attrPos < 0 || rec[attrPos + 8] == 0) return false; // absent or resident

    // Refuse to relocate compressed/sparse $DATA (flags @ +12): keeping the LZNT1
    // unit geometry correct under a move is out of scope — caller falls back.
    var attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(attrPos + 12));
    var needsMove = RunsCrossBoundary(rec, attrPos, geo, boundary);
    if (needsMove && (attrFlags & 0x0001) != 0)
      throw new NotSupportedException("NTFS shrink: a compressed stream needs relocation; rebuild fallback required.");

    if (!needsMove) return false;

    var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(attrPos + 32));
    var runs = DecodeDataRuns(rec, runsOff);

    var any = false;
    for (var i = 0; i < runs.Count; i++) {
      var (lcn, count) = runs[i];
      if (lcn < 0) continue; // sparse hole carries no LCN
      if (lcn + count <= boundary) continue; // entirely below

      // Split the run at the boundary: the [boundary, lcn+count) tail must move.
      var belowCount = Math.Max(0, boundary - lcn);
      var aboveCount = count - belowCount;
      var aboveStart = lcn + belowCount;

      // Prefer a single contiguous destination so the run list does NOT grow (and
      // the MFT record's slack stays sufficient). Fall back to per-cluster only when
      // no contiguous gap of aboveCount clusters exists below the boundary.
      var dstStart = AllocateContiguousBelow(image, geo, boundary, aboveCount);
      long[] newAbove;
      if (dstStart >= 0) {
        // Block move.
        var srcByte = aboveStart * geo.ClusterSize;
        var dstByte = dstStart * geo.ClusterSize;
        image.AsSpan((int)srcByte, (int)(aboveCount * geo.ClusterSize))
          .CopyTo(image.AsSpan((int)dstByte));
        for (long k = 0; k < aboveCount; k++) FreeCluster(image, geo, aboveStart + k);
        newAbove = new long[aboveCount];
        for (long k = 0; k < aboveCount; k++) newAbove[k] = dstStart + k;
        bytes += aboveCount * geo.ClusterSize;
        clusters += aboveCount;
      } else {
        newAbove = new long[aboveCount];
        for (long k = 0; k < aboveCount; k++) {
          var src = aboveStart + k;
          var dst = AllocateFreeClusterBelow(image, geo, boundary);
          image.AsSpan((int)(src * geo.ClusterSize), geo.ClusterSize)
            .CopyTo(image.AsSpan((int)(dst * geo.ClusterSize)));
          FreeCluster(image, geo, src);
          newAbove[k] = dst;
          bytes += geo.ClusterSize;
          clusters++;
        }
      }
      any = true;

      // Rebuild the run list for this entry: the below part stays, the relocated
      // part is coalesced into contiguous sub-runs.
      var replacement = new List<(long Lcn, long Count)>();
      if (belowCount > 0) replacement.Add((lcn, belowCount));
      replacement.AddRange(CoalesceClusters(newAbove));
      runs.RemoveAt(i);
      runs.InsertRange(i, replacement);
      i += replacement.Count - 1;
    }

    if (!any) return false;

    // Re-encode the run list back into the attribute. Relocating downward can make
    // the run list slightly LONGER (a larger signed LCN delta needs more bytes), so
    // grow the attribute in place when the run list no longer fits its current span,
    // shifting the following attributes by the delta — provided the record has room.
    var newRunBytes = EncodeDataRuns(runs);
    var attrLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(attrPos + 4));
    var dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(attrPos + 32));
    var slack = attrLen - dataRunsOffset;

    if (newRunBytes.Length <= slack) {
      Array.Clear(rec, runsOff, slack);
      newRunBytes.CopyTo(rec, runsOff);
    } else {
      // Grow the attribute: new aligned length, shift the tail ($ATTRIBUTE end
      // marker and any following attributes) by the delta.
      var newAttrLen = (dataRunsOffset + newRunBytes.Length + 7) & ~7;
      var usedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(24));
      var tailStart = attrPos + attrLen;
      var tailLen = usedSize - tailStart;
      var delta = newAttrLen - attrLen;
      if (usedSize + delta + 8 > geo.MftRecordSize)
        throw new NotSupportedException(
          $"NTFS shrink: record {slot} cannot hold the grown run list ({newRunBytes.Length} B); rebuild fallback required.");

      var rebuilt = new byte[geo.MftRecordSize];
      rec.AsSpan(0, attrPos + dataRunsOffset).CopyTo(rebuilt);          // header up to run list
      BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(attrPos + 4), (uint)newAttrLen);
      newRunBytes.CopyTo(rebuilt, attrPos + dataRunsOffset);
      if (tailLen > 0)
        rec.AsSpan(tailStart, tailLen).CopyTo(rebuilt.AsSpan(attrPos + newAttrLen));
      BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(24), (uint)(usedSize + delta));
      rebuilt.CopyTo(rec);
    }

    WriteUsaFixup(rec, geo);
    rec.CopyTo(image, recOff);
    return true;
  }

  private static bool RunsCrossBoundary(byte[] rec, int attrPos, Geo geo, long boundary) {
    var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(attrPos + 32));
    foreach (var (lcn, count) in DecodeDataRuns(rec, runsOff))
      if (lcn >= 0 && lcn + count > boundary)
        return true;
    return false;
  }

  private static List<(long Lcn, long Count)> CoalesceClusters(long[] lcns) {
    var runs = new List<(long Lcn, long Count)>();
    foreach (var lcn in lcns) {
      if (runs.Count > 0 && runs[^1].Lcn + runs[^1].Count == lcn)
        runs[^1] = (runs[^1].Lcn, runs[^1].Count + 1);
      else
        runs.Add((lcn, 1));
    }
    return runs;
  }

  // ── $Bitmap helpers ─────────────────────────────────────────────────────────

  // Finds the lowest free cluster strictly below `boundary` and marks it allocated.
  private static long AllocateFreeClusterBelow(byte[] image, Geo geo, long boundary) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    for (long c = 0; c < boundary; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff < 0 || bOff >= image.Length) break;
      var mask = (byte)(1 << (int)(c % 8));
      if ((image[bOff] & mask) != 0) continue;
      image[bOff] |= mask;
      return c;
    }
    throw new NotSupportedException("NTFS shrink: no free cluster below the boundary to relocate into.");
  }

  // Finds the lowest run of `count` contiguous free clusters strictly below
  // `boundary` and marks them all allocated. Returns the start LCN, or -1 if no such
  // contiguous gap exists (caller falls back to per-cluster relocation).
  private static long AllocateContiguousBelow(byte[] image, Geo geo, long boundary, long count) {
    if (count <= 0) return -1;
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    long runStart = -1, runLen = 0;
    for (long c = 0; c < boundary; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff < 0 || bOff >= image.Length) break;
      var free = (image[bOff] & (1 << (int)(c % 8))) == 0;
      if (free) {
        if (runStart < 0) { runStart = c; runLen = 0; }
        runLen++;
        if (runLen == count) {
          for (long k = runStart; k < runStart + count; k++) {
            var kOff = bmByteOffset + (int)(k / 8);
            image[kOff] |= (byte)(1 << (int)(k % 8));
          }
          return runStart;
        }
      } else {
        runStart = -1; runLen = 0;
      }
    }
    return -1;
  }

  private static void FreeCluster(byte[] image, Geo geo, long lcn) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    var bOff = bmByteOffset + (int)(lcn / 8);
    if (bOff < 0 || bOff >= image.Length) return;
    image[bOff] &= (byte)~(1 << (int)(lcn % 8));
  }

  // Reshapes $Bitmap for the new geometry: `target` usable clusters (0..target-1)
  // plus one reserved backup-boot cluster at index `target`. Clears every bit
  // strictly above the backup cluster, marks the backup cluster allocated (NTFS
  // reserves the volume's last cluster for the backup boot — mkfs.ntfs sets this
  // bit, and ntfsresize's cluster accounting requires it), and shrinks the
  // $Bitmap:$DATA valid length to cover exactly target+1 bits.
  private static void TrimBitmap(byte[] image, Geo geo, long target) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    var imageClusters = target;          // total clusters in the trimmed volume
    var backupCluster = target - 1;      // index of the reserved last cluster

    // Clear bits strictly above the backup cluster (stale allocations past the end).
    for (long c = backupCluster + 1; c < geo.VolumeClusters; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff < 0 || bOff >= image.Length) break;
      image[bOff] &= (byte)~(1 << (int)(c % 8));
    }
    // Reserve the backup-boot cluster.
    var backOff = bmByteOffset + (int)(backupCluster / 8);
    if (backOff >= 0 && backOff < image.Length)
      image[backOff] |= (byte)(1 << (int)(backupCluster % 8));

    // Pad the final bitmap byte: bits past the last volume cluster must read as
    // allocated (mkfs.ntfs convention). ntfsresize derives the volume cluster count
    // from the bitmap's real size in bytes (ceil(clusters/8)*8 bits), so a free
    // padding bit there is flagged as a missing/under-allocated cluster.
    // A driver reads the bitmap in 64-bit words and expects the attribute to be at
    // least that many bytes long, so the tail is padded to a whole word — and the
    // bits in it must read as allocated, or a cluster that is not there is offered
    // as free space.
    var validBits = ((imageClusters + 63) / 64) * 64;
    for (var c = imageClusters; c < validBits; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff < 0 || bOff >= image.Length) break;
      image[bOff] |= (byte)(1 << (int)(c % 8));
    }

    // Update $Bitmap (record 6) $DATA real-size / valid-data-length to the bytes
    // that cover imageClusters bits. Allocated size stays cluster-rounded and within
    // the bitmap's already-allocated clusters.
    var rec6Off = (int)(geo.MftOffset + 6L * geo.MftRecordSize);
    var rec6 = image.AsSpan(rec6Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec6);
    var (attrPos, _) = FindAttr(rec6, 0x80, unnamedOnly: true);
    if (attrPos < 0 || rec6[attrPos + 8] == 0) return;
    var newValid = ((imageClusters + 63) / 64) * 8;
    var allocated = BinaryPrimitives.ReadInt64LittleEndian(rec6.AsSpan(attrPos + 40));
    if (newValid > allocated) newValid = allocated;
    BinaryPrimitives.WriteInt64LittleEndian(rec6.AsSpan(attrPos + 48), newValid); // real size
    BinaryPrimitives.WriteInt64LittleEndian(rec6.AsSpan(attrPos + 56), newValid); // valid data length
    WriteUsaFixup(rec6, geo);
    rec6.CopyTo(image, rec6Off);
  }

  private static long HighestAllocatedCluster(byte[] image, Geo geo) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    long highest = 0;
    // Exclude the reserved final cluster (backup boot) — it is always marked
    // allocated and would otherwise pin the volume at full size.
    for (long c = 0; c < geo.VolumeClusters - 1; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff < 0 || bOff >= image.Length) break;
      if ((image[bOff] & (1 << (int)(c % 8))) != 0) highest = c;
    }
    return highest;
  }

  // Highest LCN referenced by any non-resident run in any in-use MFT record.
  private static long HighestReferencedCluster(byte[] image, Geo geo) {
    long highest = 0;
    var slots = MftSlotCount(image, geo);
    for (var slot = 0; slot < slots; slot++) {
      var recOff = (int)MftRecordOffset(image, geo, slot);
      if (recOff < 0 || recOff + geo.MftRecordSize > image.Length) continue;
      if (image[recOff] != 'F' || image[recOff + 1] != 'I' || image[recOff + 2] != 'L' || image[recOff + 3] != 'E')
        continue;
      var rec = image.AsSpan(recOff, geo.MftRecordSize).ToArray();
      ApplyFixup(rec);
      if ((BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(22)) & 0x01) == 0) continue;
      foreach (var attrType in new uint[] { 0x80, 0xA0, 0xB0 }) {
        var (attrPos, _) = FindAttr(rec, attrType, unnamedOnly: attrType == 0x80);
        if (attrPos < 0 || rec[attrPos + 8] == 0) continue;
        var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(attrPos + 32));
        foreach (var (lcn, count) in DecodeDataRuns(rec, runsOff))
          if (lcn >= 0 && lcn + count - 1 > highest) highest = lcn + count - 1;
      }
    }
    return highest;
  }

  private static long CountAllocatedClusters(byte[] image, Geo geo) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    long count = 0;
    for (long c = 0; c < geo.VolumeClusters; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff < 0 || bOff >= image.Length) break;
      if ((image[bOff] & (1 << (int)(c % 8))) != 0) count++;
    }
    return count;
  }

  // ── Shared boot / MFT plumbing (mirrors NtfsInPlaceAdder) ────────────────────

  private readonly record struct Geo(
      int BytesPerSector, int SectorsPerCluster, int ClusterSize,
      long MftOffset, long MftMirrOffset, int MftRecordSize, long VolumeClusters);

  private static Geo ParseBoot(byte[] image) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bps == 0) bps = 512;
    var spc = image[13] == 0 ? (byte)8 : image[13];
    var clusterSize = bps * spc;
    var numberSectors = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(40));
    var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(48));
    var mftMirrCluster = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(56));
    var cpr = (sbyte)image[64];
    var recSize = cpr < 0 ? 1 << (-cpr) : cpr * clusterSize;
    // NumberSectors excludes the trailing backup-boot sector; the image therefore
    // spans NumberSectors+1 sectors = (NumberSectors+1)/spc total volume clusters.
    var volumeClusters = (numberSectors + 1) / spc;
    return new Geo(bps, spc, clusterSize, mftCluster * clusterSize, mftMirrCluster * clusterSize,
      recSize, volumeClusters);
  }

  private static int MftSlotCount(byte[] image, Geo geo) {
    var rec0 = image.AsSpan((int)geo.MftOffset, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);
    var (attrPos, _) = FindAttr(rec0, 0x80, unnamedOnly: true);
    if (attrPos < 0 || rec0[attrPos + 8] == 0) {
      var fromImage = (int)((image.Length - geo.MftOffset) / geo.MftRecordSize);
      return Math.Max(16, fromImage);
    }
    var alloc = BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 40));
    return (int)(alloc / geo.MftRecordSize);
  }

  private static long MftRecordOffset(byte[] image, Geo geo, int slot) {
    var vcnByte = (long)slot * geo.MftRecordSize;
    var targetCluster = vcnByte / geo.ClusterSize;
    var offsetInCluster = vcnByte % geo.ClusterSize;

    var rec0 = image.AsSpan((int)geo.MftOffset, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);
    var (attrPos, _) = FindAttr(rec0, 0x80, unnamedOnly: true);
    if (attrPos < 0 || rec0[attrPos + 8] == 0)
      return geo.MftOffset + (long)slot * geo.MftRecordSize;
    var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec0.AsSpan(attrPos + 32));
    var runs = DecodeDataRuns(rec0, runsOff);
    long vcn = 0;
    foreach (var (lcn, count) in runs) {
      if (lcn < 0) { vcn += count; continue; }
      if (targetCluster < vcn + count) {
        var phys = lcn + (targetCluster - vcn);
        return phys * geo.ClusterSize + offsetInCluster;
      }
      vcn += count;
    }
    return -1;
  }

  private static void SyncMftMirror(byte[] image, Geo geo) {
    var mirrorRecords = MftMirrRecordCount(image, geo);
    for (var i = 0; i < mirrorRecords; i++) {
      var src = (int)(geo.MftOffset + (long)i * geo.MftRecordSize);
      var dst = (int)(geo.MftMirrOffset + (long)i * geo.MftRecordSize);
      if (src + geo.MftRecordSize > image.Length || dst + geo.MftRecordSize > image.Length) break;
      image.AsSpan(src, geo.MftRecordSize).CopyTo(image.AsSpan(dst));
    }
  }

  private static int MftMirrRecordCount(byte[] image, Geo geo) {
    var rec1 = image.AsSpan((int)(geo.MftOffset + geo.MftRecordSize), geo.MftRecordSize).ToArray();
    ApplyFixup(rec1);
    var (attrPos, _) = FindAttr(rec1, 0x80, unnamedOnly: true);
    if (attrPos < 0) return 4;
    var size = rec1[attrPos + 8] == 0
      ? BinaryPrimitives.ReadUInt32LittleEndian(rec1.AsSpan(attrPos + 16))
      : BinaryPrimitives.ReadInt64LittleEndian(rec1.AsSpan(attrPos + 48));
    return (int)Math.Max(1, size / geo.MftRecordSize);
  }

  private static (int ByteOffset, long Lcn) LocateClusterBitmap(byte[] image, Geo geo) {
    var rec6Off = (int)(geo.MftOffset + 6L * geo.MftRecordSize);
    var rec6 = image.AsSpan(rec6Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec6);
    var (attrPos, _) = FindAttr(rec6, 0x80, unnamedOnly: true);
    if (attrPos < 0 || rec6[attrPos + 8] == 0)
      throw new IOException("NTFS shrink: $Bitmap not found / resident.");
    var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec6.AsSpan(attrPos + 32));
    var bmLcn = FirstRunLcn(rec6, runsOff);
    return ((int)(bmLcn * geo.ClusterSize), bmLcn);
  }

  private static (int Pos, int Len) FindAttr(byte[] record, uint type, bool unnamedOnly) {
    var first = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));
    var pos = (int)first;
    while (pos + 16 <= used && pos + 16 <= record.Length) {
      var t = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
      if (t == 0xFFFFFFFF) break;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
      if (len < 16 || pos + len > record.Length) break;
      if (t == type && (!unnamedOnly || record[pos + 9] == 0)) return (pos, len);
      pos += len;
    }
    return (-1, 0);
  }

  private static long FirstRunLcn(byte[] record, int runsOffset) {
    var header = record[runsOffset];
    var lengthBytes = header & 0x0F;
    var offsetBytes = (header >> 4) & 0x0F;
    long lcn = 0;
    var o = runsOffset + 1 + lengthBytes;
    for (var i = 0; i < offsetBytes; i++) lcn |= (long)record[o + i] << (i * 8);
    if (offsetBytes > 0 && (record[o + offsetBytes - 1] & 0x80) != 0)
      for (var i = offsetBytes; i < 8; i++) lcn |= (long)0xFF << (i * 8);
    return lcn;
  }

  // Decodes data runs; a sparse run (offset field absent) is reported with Lcn = -1.
  private static List<(long Lcn, long Count)> DecodeDataRuns(byte[] record, int offset) {
    var runs = new List<(long Lcn, long Count)>();
    long prevLcn = 0;
    while (offset < record.Length) {
      var header = record[offset];
      if (header == 0) break;
      var lenB = header & 0x0F;
      var offB = (header >> 4) & 0x0F;
      offset++;
      long length = 0;
      for (var i = 0; i < lenB; i++) length |= (long)record[offset + i] << (i * 8);
      offset += lenB;
      if (offB == 0) { runs.Add((-1, length)); continue; } // sparse hole
      long delta = 0;
      for (var i = 0; i < offB; i++) delta |= (long)record[offset + i] << (i * 8);
      if ((record[offset + offB - 1] & 0x80) != 0)
        for (var i = offB; i < 8; i++) delta |= (long)0xFF << (i * 8);
      offset += offB;
      prevLcn += delta;
      runs.Add((prevLcn, length));
    }
    return runs;
  }

  private static byte[] EncodeDataRuns(List<(long Lcn, long Count)> runs) {
    using var ms = new MemoryStream();
    long prev = 0;
    foreach (var (lcn, count) in runs) {
      if (lcn < 0) {
        // sparse hole: length field only, no offset
        var lenOnly = FieldBytes(count, false);
        ms.WriteByte((byte)lenOnly);
        for (var i = 0; i < lenOnly; i++) ms.WriteByte((byte)(count >> (i * 8)));
        continue;
      }
      var offset = lcn - prev;
      var lenB = FieldBytes(count, false);
      var offB = FieldBytes(offset, true);
      ms.WriteByte((byte)((offB << 4) | lenB));
      for (var i = 0; i < lenB; i++) ms.WriteByte((byte)(count >> (i * 8)));
      for (var i = 0; i < offB; i++) ms.WriteByte((byte)(offset >> (i * 8)));
      prev = lcn;
    }
    ms.WriteByte(0);
    return ms.ToArray();
  }

  private static int FieldBytes(long value, bool signed) {
    if (value == 0) return signed ? 0 : 1;
    if (!signed) return value <= 0xFF ? 1 : value <= 0xFFFF ? 2 : value <= 0xFFFFFF ? 3 : 4;
    if (value >= -128 && value <= 127) return 1;
    if (value >= -32768 && value <= 32767) return 2;
    if (value >= -8388608 && value <= 8388607) return 3;
    return 4;
  }

  private static void ApplyFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaOffset + usaCount * 2 > record.Length || usaCount < 2) return;
    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;
      if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd)) != usn) continue;
      record.AsSpan(usaOffset + i * 2, 2).CopyTo(record.AsSpan(sectorEnd));
    }
  }

  private static void WriteUsaFixup(byte[] record, Geo geo) {
    _ = geo;
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length) return;
    var usn = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset)) + 1);
    if (usn is 0 or 0xFFFF) usn = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset), usn);
    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;
      var usaSlot = usaOffset + i * 2;
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaSlot),
        BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd)));
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd), usn);
    }
  }
}
