#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ntfs;

/// <summary>
/// In-place NTFS block mover. Moves cluster-aligned extents within an NTFS image
/// and patches the MFT data runs, Update Sequence Array fixup, and cluster bitmap
/// ($Bitmap, MFT record 6) so the file remains reachable at its new location.
///
/// <para>Handles the common case where a file's data runs cover the moved cluster
/// range. If re-encoding the patched data runs changes byte length and the
/// MFT record lacks sufficient slack space, throws <see cref="NotSupportedException"/>
/// to let the rebuild fallback handle it.</para>
///
/// <para>
/// Streaming: the mover never loads the whole image. <see cref="Init(Stream)"/>
/// reads only the boot sector and MFT record 0; all metadata updates are
/// targeted reads/writes via <see cref="SectorCache"/> + <see cref="Stream.Flush"/>
/// barriers so a crash mid-operation leaves the image fsck-recoverable, and a
/// multi-TB NTFS volume needs only ~256 MB of cache RAM regardless of size.
/// </para>
/// </summary>
public sealed class NtfsBlockMover : IFilesystemBlockMover, IFilesystemMetadataMover {
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _clusterSize;
  private long _mftCluster;
  private int _mftRecordSize;
  private long _mftOffset;
  private int _maxRecords;

  private const int FirstUserRecord = 16;

  /// <summary>Initialises the mover by parsing the NTFS boot sector fields.</summary>
  public void Init(byte[] image) {
    InitFromBootSector(image.AsSpan(0, Math.Min(image.Length, 512)));

    // Determine max MFT records from record 0's $DATA.
    _maxRecords = FirstUserRecord;
    if (_mftOffset >= 0 && _mftOffset + _mftRecordSize <= image.Length) {
      var rec0 = image.AsSpan((int)_mftOffset, _mftRecordSize).ToArray();
      if (rec0[0] == 'F' && rec0[1] == 'I' && rec0[2] == 'L' && rec0[3] == 'E') {
        ApplyFixup(rec0);
        var dataRuns = FindDefaultDataRuns(rec0);
        if (dataRuns != null) {
          long totalMftBytes = 0;
          foreach (var run in dataRuns) totalMftBytes += run.ClusterCount * _clusterSize;
          var bounded = (int)(totalMftBytes / _mftRecordSize);
          if (bounded > _maxRecords) _maxRecords = bounded;
        }
      }
    }
    var maxFromImage = (int)((image.Length - _mftOffset) / _mftRecordSize);
    if (_maxRecords > maxFromImage) _maxRecords = maxFromImage;
  }

  /// <summary>
  /// Stream-based initialisation. Reads only the 512-byte boot sector and the
  /// first MFT record (typically 1 KB) — used by the streaming code paths so
  /// multi-TB images don't have to be loaded into memory.
  /// </summary>
  public void Init(Stream image) {
    Span<byte> boot = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(boot);
    InitFromBootSector(boot);

    // Determine max MFT records from record 0's $DATA.
    _maxRecords = FirstUserRecord;
    if (_mftOffset >= 0 && _mftOffset + _mftRecordSize <= image.Length) {
      var rec0 = new byte[_mftRecordSize];
      image.Position = _mftOffset;
      image.ReadExactly(rec0);
      if (rec0[0] == 'F' && rec0[1] == 'I' && rec0[2] == 'L' && rec0[3] == 'E') {
        ApplyFixup(rec0);
        var dataRuns = FindDefaultDataRuns(rec0);
        if (dataRuns != null) {
          long totalMftBytes = 0;
          foreach (var run in dataRuns) totalMftBytes += run.ClusterCount * _clusterSize;
          var bounded = (int)(totalMftBytes / _mftRecordSize);
          if (bounded > _maxRecords) _maxRecords = bounded;
        }
      }
    }
    var maxFromImage = (int)((image.Length - _mftOffset) / _mftRecordSize);
    if (_maxRecords > maxFromImage) _maxRecords = maxFromImage;
  }

  private void InitFromBootSector(ReadOnlySpan<byte> boot) {
    _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
    if (_bytesPerSector == 0) _bytesPerSector = 512;
    _sectorsPerCluster = boot[13];
    if (_sectorsPerCluster == 0) _sectorsPerCluster = 8;
    _clusterSize = _bytesPerSector * _sectorsPerCluster;
    _mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot[48..]);

    var clustersPerRecord = (sbyte)boot[64];
    _mftRecordSize = clustersPerRecord < 0
      ? 1 << (-clustersPerRecord)
      : clustersPerRecord * _clusterSize;

    _mftOffset = _mftCluster * _clusterSize;
  }

  /// <summary>
  /// Byte offset past all known metadata regions. For NTFS, the boot sector,
  /// MFT, and system file data are all marked MetadataReserved by the extent
  /// map. User data can safely be placed at or after this offset.
  /// Computed from the MFT location + its extent size as a conservative lower bound.
  /// The actual usable origin should be derived from the extent map (see
  /// <see cref="NtfsFormatDescriptor.DefragmentWithPlanner"/>).
  /// </summary>
  public long FirstDataByte => _mftOffset;

  /// <summary>Bytes per cluster.</summary>
  public int ClusterSize => _clusterSize;

  // ── IFilesystemBlockMover ──────────────────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Power-fail-safe in-place metadata update via three targeted-write steps:
  ///   1. Allocate new clusters in $Bitmap.
  ///   2. Patch $DATA data runs in the MFT record (and rewrite that record).
  ///   3. Free old clusters in $Bitmap.
  /// After each step the stream is flushed so the OS commits that step before
  /// starting the next. The image is never loaded whole into memory.
  /// <para>Crash semantics:
  /// <list type="bullet">
  ///   <item>Mid-step-1: bits set in bitmap but no file references them yet —
  ///   fsck reports the new clusters as cross-linked or orphaned and frees them.</item>
  ///   <item>Mid-step-2: a single-record write — typically atomic at the sector
  ///   level. The fixup-array ensures torn writes are detectable.</item>
  ///   <item>Mid-step-3: dir/MFT points at new clusters (file reachable), old
  ///   clusters still marked allocated. fsck reports them as cross-linked and
  ///   frees the orphan bits.</item>
  /// </list></para>
  /// </remarks>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    using var cache = new SectorCache(image);

    var oldLcn = oldOffset / _clusterSize;
    var newLcn = newOffset / _clusterSize;
    var clusterCount = (length + _clusterSize - 1) / _clusterSize;

    // 1. Find the MFT record for the file (streamed via cache).
    var recordIndex = FindMftRecordStream(cache, fileName);
    if (recordIndex < 0)
      throw new InvalidOperationException($"NTFS: MFT record for '{fileName}' not found.");

    // Locate $Bitmap once (also via cache).
    var bitmapRuns = LoadBitmapRunsStream(cache);

    // Step 1: Set new cluster bits in $Bitmap (RMW per byte).
    if (bitmapRuns != null && bitmapRuns.Count > 0)
      MutateBitmapBitsStream(image, bitmapRuns, newLcn, clusterCount, setBits: true);
    image.Flush();
    cache.InvalidateAll(); // bitmap pages changed.

    // Step 2: Patch the MFT record's data runs and write it back.
    PatchMftDataRunsStream(image, cache, recordIndex, oldLcn, newLcn, clusterCount);
    image.Flush();
    cache.InvalidateAll(); // MFT record changed.

    // Step 3: Clear old cluster bits in $Bitmap.
    if (bitmapRuns != null && bitmapRuns.Count > 0)
      MutateBitmapBitsStream(image, bitmapRuns, oldLcn, clusterCount, setBits: false);
    image.Flush();
  }

  // ── IFilesystemMetadataMover ──────────────────────────────────────────

  /// <summary>
  /// MFT record numbers of the system files whose position is recorded
  /// somewhere that can be rewritten. Record 7 ($Boot) is absent on purpose:
  /// the boot sector is what tells everything else where to look, and nothing
  /// points at it. $Volume and root are resident inside their records, so they
  /// occupy no clusters of their own to move.
  /// </summary>
  private static readonly Dictionary<string, int> SystemFileRecords =
    new(StringComparer.OrdinalIgnoreCase) {
      ["$MFT"] = 0, ["$MFTMirr"] = 1, ["$LogFile"] = 2, ["$AttrDef"] = 4,
      ["$Bitmap"] = 6, ["$Secure"] = 9, ["$UpCase"] = 10,
    };

  /// <inheritdoc />
  public IReadOnlySet<string> RelocatableMetadata { get; } =
    SystemFileRecords.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Moves one of the volume's own structures. Each system file's clusters are
  /// described by the data runs in its MFT record, so repointing it is the same
  /// edit a file gets — except for the two the boot sector names directly, and
  /// for the two that describe themselves.
  /// </summary>
  /// <remarks>
  /// <para>$MFT is the awkward one: its own record lives inside it, so the copy
  /// that must be patched is the one at the destination, not the one the boot
  /// sector still points at. The boot sector is rewritten afterwards, which is
  /// also the point of no return — until then the volume still reads through
  /// the old copy, which is intact.</para>
  ///
  /// <para>$Bitmap is the other: its bits record its own allocation. Its record
  /// is patched first so the bits are then read and written through the runs
  /// that describe where it now lives, rather than through the ones that
  /// describe where it used to.</para>
  /// </remarks>
  public void UpdateMetadataAfterMove(Stream image, string metadataName,
      long oldOffset, long newOffset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadataName);
    if (!SystemFileRecords.TryGetValue(metadataName, out var recordIndex))
      throw new NotSupportedException(
        $"NTFS: '{metadataName}' is not a structure this volume can be repointed at.");

    var oldLcn = oldOffset / _clusterSize;
    var newLcn = newOffset / _clusterSize;
    var clusterCount = (length + _clusterSize - 1) / _clusterSize;
    if (clusterCount <= 0 || oldLcn == newLcn) return;

    // The MFT's own record travels with it, so the copy to patch is the one at
    // the destination. Everything below then reads records through it.
    var movingMft = recordIndex == 0;
    if (movingMft) _mftOffset = newOffset;

    using (var cache = new SectorCache(image)) {
      PatchMftDataRunsStream(image, cache, recordIndex, oldLcn, newLcn, clusterCount);
    }
    image.Flush();

    // Once the runs say where the structure now is, the allocation follows.
    using (var cache = new SectorCache(image)) {
      var bitmapRuns = LoadBitmapRunsStream(cache);
      if (bitmapRuns is { Count: > 0 }) {
        MutateBitmapBitsStream(image, bitmapRuns, newLcn, clusterCount, setBits: true);
        image.Flush();

        // The old clusters are free again — except the ones something else has
        // moved onto, which are not this structure's to release.
        for (var i = 0L; i < clusterCount; i++) {
          var clusterOffset = (oldLcn + i) * _clusterSize;
          if (IsLive(clusterOffset, _clusterSize, liveRanges)) continue;
          MutateBitmapBitsStream(image, bitmapRuns, oldLcn + i, 1, setBits: false);
        }
        image.Flush();
      }
    }

    // The boot sector names two of them outright; without this the volume would
    // still look for the MFT where it no longer is.
    if (movingMft || recordIndex == 1) {
      Span<byte> lcn = stackalloc byte[8];
      BinaryPrimitives.WriteInt64LittleEndian(lcn, newLcn);
      image.Position = movingMft ? 0x30 : 0x38;
      image.Write(lcn);
      image.Flush();
      if (movingMft) _mftCluster = newLcn;
      RefreshBackupBootSector(image);
    }

    // $MFTMirr carries a copy of the first four MFT records, and a driver
    // compares the two. Repointing any of those records leaves the copy stale —
    // ntfsfix reports "$MFTMirr does not match $MFT" and refuses the volume —
    // so the mirror is refreshed from the records it mirrors.
    if (recordIndex <= 3 || recordIndex == 1)
      RefreshMftMirror(image);
  }

  /// <summary>Whether any live range covers part of this cluster.</summary>
  private static bool IsLive(long offset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges) {
    if (liveRanges == null) return false;
    foreach (var (start, len) in liveRanges)
      if (offset < start + len && start < offset + length)
        return true;
    return false;
  }

  /// <summary>
  /// Copies the boot sector over the backup NTFS keeps in the volume's last
  /// sector. The two are compared, so changing where the MFT lives in one and
  /// not the other leaves the volume looking damaged.
  /// </summary>
  private void RefreshBackupBootSector(Stream image) {
    if (_bytesPerSector <= 0 || image.Length < 2L * _bytesPerSector) return;

    var boot = new byte[_bytesPerSector];
    image.Position = 0;
    image.ReadExactly(boot);
    image.Position = image.Length - _bytesPerSector;
    image.Write(boot);
    image.Flush();
  }

  /// <summary>
  /// Rewrites $MFTMirr from the first four MFT records. Its own position comes
  /// from the boot sector, which is authoritative at this point: it has already
  /// been updated if the mirror itself was what moved.
  /// </summary>
  private void RefreshMftMirror(Stream image) {
    Span<byte> field = stackalloc byte[8];
    image.Position = 0x38;
    image.ReadExactly(field);
    var mirrorOffset = BinaryPrimitives.ReadInt64LittleEndian(field) * _clusterSize;
    if (mirrorOffset <= 0 || mirrorOffset + 4L * _mftRecordSize > image.Length) return;
    if (_mftOffset + 4L * _mftRecordSize > image.Length) return;

    var records = new byte[4 * _mftRecordSize];
    image.Position = _mftOffset;
    image.ReadExactly(records);
    image.Position = mirrorOffset;
    image.Write(records);
    image.Flush();
  }

  // ── Streaming MFT record search ───────────────────────────────────────

  private int FindMftRecordStream(SectorCache cache, string fileName) {
    var recordBuf = ArrayPool<byte>.Shared.Rent(_mftRecordSize);
    try {
      for (var i = FirstUserRecord; i < _maxRecords; i++) {
        var recordOffset = _mftOffset + (long)i * _mftRecordSize;
        if (recordOffset + _mftRecordSize > cache.Length) break;

        cache.Read(recordOffset, recordBuf.AsSpan(0, _mftRecordSize));
        if (recordBuf[0] != 'F' || recordBuf[1] != 'I' || recordBuf[2] != 'L' || recordBuf[3] != 'E')
          continue;

        // Apply fixup on a sized copy.
        var record = recordBuf.AsSpan(0, _mftRecordSize).ToArray();
        ApplyFixup(record);

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
        if ((flags & 0x01) == 0) continue;

        if (TryMatchFileName(record, fileName))
          return i;
      }
      return -1;
    } finally {
      ArrayPool<byte>.Shared.Return(recordBuf);
    }
  }

  private static bool TryMatchFileName(byte[] record, string target) {
    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));

    var attrPos = (int)firstAttrOffset;
    while (attrPos + 16 <= usedSize && attrPos + 16 <= record.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;

      var attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > record.Length) break;

      if (attrType == 0x30) {
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 20));
        var dataStart = attrPos + valueOffset;
        if (dataStart + 66 <= record.Length) {
          var nameLength = record[dataStart + 64];
          if (dataStart + 66 + nameLength * 2 <= record.Length) {
            var name = Encoding.Unicode.GetString(record, dataStart + 66, nameLength * 2);
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
              return true;
          }
        }
      }

      attrPos += (int)attrLen;
    }
    return false;
  }

  // ── Streaming data-run patching ───────────────────────────────────────

  /// <summary>
  /// Reads the target MFT record via the cache, patches its non-resident
  /// $DATA attribute data runs, USA-fixes the record, and writes it back at
  /// its absolute offset. A single MFT record is typically 1 KB; the write
  /// is a single-record targeted write.
  /// </summary>
  private void PatchMftDataRunsStream(Stream image, SectorCache cache, int recordIndex,
      long oldLcn, long newLcn, long clusterCount) {
    var recordOffset = _mftOffset + (long)recordIndex * _mftRecordSize;
    var record = new byte[_mftRecordSize];
    cache.Read(recordOffset, record);
    ApplyFixup(record);

    // Locate the default $DATA attribute (type 0x80, unnamed, non-resident).
    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var usedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));

    var attrPos = (int)firstAttrOffset;
    while (attrPos + 16 <= usedSize && attrPos + 16 <= record.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;

      var attrLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > record.Length) break;

      if (attrType == 0x80 && record[attrPos + 8] == 1 && record[attrPos + 9] == 0) {
        // Found the default non-resident $DATA attribute.
        var dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 32));
        var runsStart = attrPos + dataRunsOffset;

        // Decode existing data runs.
        var runs = DecodeDataRuns(record, runsStart);

        // Patch the run(s) covering the moved cluster range.
        PatchRuns(runs, oldLcn, newLcn, clusterCount);

        // Re-encode the data runs.
        var newRunBytes = EncodeDataRuns(runs);
        var oldRunBytes = MeasureDataRunsBytes(record, runsStart);

        // Check if the new encoding fits in the same space.
        if (newRunBytes.Length > oldRunBytes) {
          // Check slack: space between end of this attribute and the next.
          var slack = attrLen - dataRunsOffset - oldRunBytes;
          if (newRunBytes.Length - oldRunBytes > slack)
            throw new NotSupportedException(
              $"NTFS: re-encoded data runs are {newRunBytes.Length - oldRunBytes} bytes longer " +
              $"than original ({oldRunBytes} -> {newRunBytes.Length}) with only {slack} bytes slack. " +
              "Rebuild fallback required.");
        }

        // Write the new data runs into the record.
        // First, clear the old data run area up to the end of the attribute.
        var clearLen = attrLen - dataRunsOffset;
        Array.Clear(record, runsStart, clearLen);
        newRunBytes.CopyTo(record, runsStart);

        // Apply USA fixup and write the (single) record back at its absolute offset.
        ApplyUsaFixup(record);
        image.Position = recordOffset;
        image.Write(record, 0, _mftRecordSize);
        cache.Invalidate(recordOffset, _mftRecordSize);
        return;
      }

      attrPos += attrLen;
    }

    // If we get here, no non-resident $DATA was found. File is resident — no move needed.
  }

  private static void PatchRuns(List<DataRun> runs, long oldLcn, long newLcn, long clusterCount) {
    var oldEnd = oldLcn + clusterCount;

    for (var i = 0; i < runs.Count; i++) {
      var run = runs[i];
      var runEnd = run.Lcn + run.ClusterCount;

      // No overlap with this run.
      if (run.Lcn >= oldEnd || runEnd <= oldLcn)
        continue;

      // Compute the overlap within this run.
      var overlapStart = Math.Max(run.Lcn, oldLcn);
      var overlapEnd = Math.Min(runEnd, oldEnd);
      var delta = newLcn - oldLcn;

      if (overlapStart == run.Lcn && overlapEnd == runEnd) {
        // Entire run is covered by the move: simple LCN shift.
        runs[i] = new DataRun { Lcn = run.Lcn + delta, ClusterCount = run.ClusterCount };
      } else if (overlapStart == run.Lcn) {
        // The move covers the front of this run: split into moved + remainder.
        var movedCount = overlapEnd - overlapStart;
        runs[i] = new DataRun { Lcn = run.Lcn + delta, ClusterCount = movedCount };
        runs.Insert(i + 1, new DataRun { Lcn = overlapEnd, ClusterCount = runEnd - overlapEnd });
        i++; // skip the newly inserted run
      } else if (overlapEnd == runEnd) {
        // The move covers the tail of this run: split into remainder + moved.
        var movedCount = overlapEnd - overlapStart;
        runs[i] = new DataRun { Lcn = run.Lcn, ClusterCount = overlapStart - run.Lcn };
        runs.Insert(i + 1, new DataRun { Lcn = overlapStart + delta, ClusterCount = movedCount });
        i++; // skip the newly inserted run
      } else {
        // The move is in the middle of the run: three-way split.
        var beforeCount = overlapStart - run.Lcn;
        var movedCount = overlapEnd - overlapStart;
        var afterCount = runEnd - overlapEnd;
        runs[i] = new DataRun { Lcn = run.Lcn, ClusterCount = beforeCount };
        runs.Insert(i + 1, new DataRun { Lcn = overlapStart + delta, ClusterCount = movedCount });
        runs.Insert(i + 2, new DataRun { Lcn = overlapEnd, ClusterCount = afterCount });
        i += 2; // skip newly inserted runs
      }
    }
  }

  // ── Streaming cluster-bitmap patching ──────────────────────────────────

  /// <summary>
  /// Loads $Bitmap's data runs (MFT record 6) via the cache so we know where
  /// the on-disk bitmap lives without scanning the whole image.
  /// </summary>
  private List<DataRun>? LoadBitmapRunsStream(SectorCache cache) {
    var rec6Offset = _mftOffset + 6L * _mftRecordSize;
    if (rec6Offset + _mftRecordSize > cache.Length) return null;

    var rec6 = new byte[_mftRecordSize];
    cache.Read(rec6Offset, rec6);
    if (rec6[0] != 'F' || rec6[1] != 'I' || rec6[2] != 'L' || rec6[3] != 'E') return null;
    ApplyFixup(rec6);

    return FindDefaultDataRuns(rec6);
  }

  /// <summary>
  /// Sets or clears a contiguous range of cluster bits in the $Bitmap. The
  /// bitmap may itself be fragmented across multiple data runs — we walk the
  /// run list, compute which runs cover each affected byte, and issue targeted
  /// byte-level RMW writes only for those bytes. No whole-bitmap load.
  /// </summary>
  private void MutateBitmapBitsStream(Stream image, List<DataRun> bitmapRuns,
      long firstCluster, long clusterCount, bool setBits) {
    if (bitmapRuns.Count == 0) return;

    // First bit and last bit (inclusive) to touch.
    var firstBit = firstCluster;
    var lastBit = firstCluster + clusterCount - 1;
    if (lastBit < firstBit) return;

    var firstByte = firstBit / 8;
    var lastByte = lastBit / 8;

    Span<byte> one = stackalloc byte[1];
    for (var byteIdx = firstByte; byteIdx <= lastByte; byteIdx++) {
      // Compute mask of bits within this byte that lie inside [firstBit, lastBit].
      var bitLo = (int)Math.Max(0, firstBit - byteIdx * 8);
      var bitHi = (int)Math.Min(7, lastBit - byteIdx * 8);
      var mask = 0;
      for (var b = bitLo; b <= bitHi; b++) mask |= 1 << b;

      // Locate the absolute byte offset of this bitmap byte by walking the
      // (potentially fragmented) bitmap run list.
      var absOff = BitmapByteOffsetStream(bitmapRuns, byteIdx);
      if (absOff < 0) continue;

      image.Position = absOff;
      image.ReadExactly(one);
      if (setBits) one[0] = (byte)(one[0] | (byte)mask);
      else one[0] = (byte)(one[0] & ~(byte)mask);
      image.Position = absOff;
      image.Write(one);
    }
  }

  /// <summary>
  /// Translates a byte index within the logical $Bitmap stream into an
  /// absolute byte offset on the volume by walking the bitmap's data runs.
  /// Returns -1 if the index falls past the allocated runs.
  /// </summary>
  private long BitmapByteOffsetStream(List<DataRun> bitmapRuns, long logicalByteIdx) {
    long runStartByte = 0;
    foreach (var run in bitmapRuns) {
      var runByteCount = run.ClusterCount * _clusterSize;
      if (logicalByteIdx < runStartByte + runByteCount) {
        var intraRun = logicalByteIdx - runStartByte;
        return run.Lcn * _clusterSize + intraRun;
      }
      runStartByte += runByteCount;
    }
    return -1;
  }

  // ── Data run decoding ─────────────────────────────────────────────────

  private sealed class DataRun {
    public long Lcn;
    public long ClusterCount;
  }

  private static List<DataRun> DecodeDataRuns(byte[] record, int offset) {
    var runs = new List<DataRun>();
    long previousLcn = 0;

    while (offset < record.Length) {
      var header = record[offset];
      if (header == 0) break;

      var lengthBytes = header & 0x0F;
      var offsetBytes = (header >> 4) & 0x0F;
      offset++;
      if (offset + lengthBytes + offsetBytes > record.Length) break;

      long length = 0;
      for (var i = 0; i < lengthBytes; i++)
        length |= (long)record[offset + i] << (i * 8);
      offset += lengthBytes;

      long clusterOffset = 0;
      if (offsetBytes > 0) {
        for (var i = 0; i < offsetBytes; i++)
          clusterOffset |= (long)record[offset + i] << (i * 8);
        if ((record[offset + offsetBytes - 1] & 0x80) != 0)
          for (var i = offsetBytes; i < 8; i++)
            clusterOffset |= (long)0xFF << (i * 8);
        offset += offsetBytes;
      }

      var lcn = previousLcn + clusterOffset;
      runs.Add(new DataRun { Lcn = lcn, ClusterCount = length });
      previousLcn = lcn;
    }
    return runs;
  }

  /// <summary>
  /// Measures the byte length of the encoded data runs starting at <paramref name="offset"/>
  /// in <paramref name="record"/>, including the terminator byte.
  /// </summary>
  private static int MeasureDataRunsBytes(byte[] record, int offset) {
    var start = offset;
    while (offset < record.Length) {
      var header = record[offset];
      if (header == 0) { offset++; break; }
      var lengthBytes = header & 0x0F;
      var offsetBytes = (header >> 4) & 0x0F;
      offset += 1 + lengthBytes + offsetBytes;
    }
    return offset - start;
  }

  // ── Data run encoding ─────────────────────────────────────────────────

  private static byte[] EncodeDataRuns(List<DataRun> runs) {
    using var ms = new MemoryStream();
    long prevLcn = 0;

    foreach (var run in runs) {
      var delta = run.Lcn - prevLcn;
      var lengthBytes = GetUnsignedFieldBytes(run.ClusterCount);
      var offsetBytes = GetSignedFieldBytes(delta);

      ms.WriteByte((byte)((offsetBytes << 4) | lengthBytes));
      WriteField(ms, run.ClusterCount, lengthBytes);
      WriteField(ms, delta, offsetBytes);
      prevLcn = run.Lcn;
    }

    ms.WriteByte(0); // terminator
    return ms.ToArray();
  }

  private static int GetUnsignedFieldBytes(long value) {
    if (value <= 0xFF) return 1;
    if (value <= 0xFFFF) return 2;
    if (value <= 0xFFFFFF) return 3;
    return 4;
  }

  private static int GetSignedFieldBytes(long value) {
    if (value == 0) return 0;
    if (value >= -128 && value <= 127) return 1;
    if (value >= -32768 && value <= 32767) return 2;
    if (value >= -8388608 && value <= 8388607) return 3;
    return 4;
  }

  private static void WriteField(MemoryStream ms, long value, int bytes) {
    for (var i = 0; i < bytes; i++)
      ms.WriteByte((byte)(value >> (i * 8)));
  }

  // ── USA fixup helpers ─────────────────────────────────────────────────

  /// <summary>
  /// Reverses the USN fixup on a record that was read from disk.
  /// Each sector's trailing 2 bytes (which held the USN sentinel on disk) are
  /// restored from the fixup array.
  /// </summary>
  private static void ApplyFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaOffset + usaCount * 2 > record.Length || usaCount < 2) return;

    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;
      var actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd));
      if (actual != usn) continue;
      record.AsSpan(usaOffset + i * 2, 2).CopyTo(record.AsSpan(sectorEnd));
    }
  }

  /// <summary>
  /// Applies USA fixup before writing a record back to disk. Each sector's
  /// trailing 2 bytes are saved into the fixup array and replaced with the USN.
  /// Matches the writer's implementation.
  /// </summary>
  private static void ApplyUsaFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaOffset + usaCount * 2 > record.Length || usaCount < 2) return;

    // Increment the USN for the new write (keeps it non-zero).
    var oldUsn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
    var newUsn = (ushort)(oldUsn == 0 ? 1 : oldUsn);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset), newUsn);

    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;
      // Save the original bytes at the sector boundary into the fixup array.
      record.AsSpan(sectorEnd, 2).CopyTo(record.AsSpan(usaOffset + i * 2));
      // Write the USN at the sector boundary.
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd), newUsn);
    }
  }

  // ── Attribute helpers ─────────────────────────────────────────────────

  /// <summary>
  /// Locates the default (unnamed) $DATA attribute (type 0x80) and decodes its
  /// data runs. Returns null if not found or if resident.
  /// </summary>
  private static List<DataRun>? FindDefaultDataRuns(byte[] record) {
    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));

    var attrPos = (int)firstAttrOffset;
    while (attrPos + 16 <= usedSize && attrPos + 16 <= record.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;

      var attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > record.Length) break;

      if (attrType == 0x80 && record[attrPos + 8] == 1 && record[attrPos + 9] == 0) {
        var dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 32));
        return DecodeDataRuns(record, attrPos + dataRunsOffset);
      }

      attrPos += (int)attrLen;
    }
    return null;
  }
}
