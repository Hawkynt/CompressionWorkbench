#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// In-place FAT12/16/32 block mover. Moves cluster-aligned extents within a FAT
/// image and patches the FAT chain + directory entries so the file remains
/// reachable at its new location.
///
/// <para>Designed for use with the planner-driven defrag path. The caller
/// (typically <see cref="FatFormatDescriptor.Defragment(System.IO.Stream, DefragOptions)"/>)
/// enumerates extents, feeds them to the planner, then applies each planned
/// move via <see cref="MoveExtent"/> + <see cref="UpdateAllocationAfterMove"/>.</para>
/// </summary>
public sealed class FatBlockMover : IFilesystemBlockMover, IFilesystemMetadataMover {
  // BPB fields cached once per image
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _reservedSectors;
  private int _fatCount;
  private int _rootEntryCount;
  private int _totalSectors;
  private int _fatSize;
  private int _rootDirSectors;
  private int _firstDataSector;
  private int _totalDataClusters;
  private int _fatType;
  private int _clusterSize;
  private long _firstDataByte;

  /// <summary>
  /// Initialises the mover by parsing BPB fields from <paramref name="image"/>.
  /// Must be called before any move operations.
  /// </summary>
  public void Init(byte[] image) => InitFromBpb(image.AsSpan(0, Math.Min(image.Length, 512)));

  /// <summary>
  /// Stream-based initialisation. Reads only the first 512 bytes (BPB) — used
  /// by the streaming code paths so multi-GB images don't have to be loaded
  /// into memory.
  /// </summary>
  public void Init(Stream image) {
    Span<byte> bpb = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(bpb);
    InitFromBpb(bpb);
  }

  private void InitFromBpb(ReadOnlySpan<byte> bpb) {
    _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bpb[11..]);
    if (_bytesPerSector is 0 or > 4096) _bytesPerSector = 512;
    _sectorsPerCluster = bpb[13] == 0 ? 1 : bpb[13];
    _reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bpb[14..]);
    _fatCount = bpb[16] == 0 ? 2 : bpb[16];
    _rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(bpb[17..]);

    var ts16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[19..]);
    _totalSectors = ts16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(bpb[32..]) : ts16;

    var fs16 = (int)BinaryPrimitives.ReadUInt16LittleEndian(bpb[22..]);
    _fatSize = fs16 == 0 ? BinaryPrimitives.ReadInt32LittleEndian(bpb[36..]) : fs16;

    _rootDirSectors = (_rootEntryCount * 32 + _bytesPerSector - 1) / _bytesPerSector;
    _firstDataSector = _reservedSectors + _fatCount * _fatSize + _rootDirSectors;
    _totalDataClusters = (_totalSectors - _firstDataSector) / _sectorsPerCluster;
    // FAT32 is the variant that keeps its FAT size in the 32-bit field and has
    // no fixed root directory; the cluster count alone called a small volume
    // formatted as FAT32 a FAT16 one, and the chain arithmetic then addressed
    // the wrong entry width.
    var isFat32ByBpb = fs16 == 0 && _rootEntryCount == 0;
    _fatType = isFat32ByBpb ? 32
      : _totalDataClusters < 4085 ? 12
      : _totalDataClusters < 65525 ? 16
      : 32;
    _clusterSize = _sectorsPerCluster * _bytesPerSector;
    _firstDataByte = (long)_firstDataSector * _bytesPerSector;
  }

  // ── Cluster ↔ byte offset helpers ──────────────────────────────────────

  /// <summary>Byte offset of the first byte of a data cluster.</summary>
  private long ClusterToOffset(int cluster) => _firstDataByte + (long)(cluster - 2) * _clusterSize;

  /// <summary>Data cluster number containing the given byte offset (rounds down).</summary>
  private int OffsetToCluster(long byteOffset) => (int)((byteOffset - _firstDataByte) / _clusterSize) + 2;

  // ── IFilesystemBlockMover ──────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

    /// <summary>
  /// Performs the move extent operation.
  /// </summary>
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
  /// allocate new chain → patch dir entry → free old chain. After each step
  /// the stream is flushed so the OS commits that step before starting the
  /// next. The image is never loaded whole into memory — multi-GB images
  /// require only a few sector reads/writes per move.
  /// <para>Crash semantics:
  /// <list type="bullet">
  ///   <item>Mid-step-1: new FAT chain partially allocated, dir still points
  ///   at old chain. File still reachable; new chain is an orphan that fsck
  ///   can free.</item>
  ///   <item>Mid-step-2: dir partially updated. Most patches are single-sector
  ///   writes — atomic at the device level. Worst case: LFN bytes inconsistent
  ///   with 8.3 entry, fsck can detect.</item>
  ///   <item>Mid-step-3: dir points at new chain (file reachable), old chain
  ///   partially freed. fsck cleans up the orphan.</item>
  /// </list></para>
  /// </remarks>
    /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var clusterCount = (int)((length + _clusterSize - 1) / _clusterSize);
    var oldFirstCluster = OffsetToCluster(oldOffset);
    var newFirstCluster = OffsetToCluster(newOffset);
    var fatStart = _reservedSectors * _bytesPerSector;

    // Step 1: Allocate new FAT chain. Writes the primary FAT first, flushes,
    // then mirrors to FAT2. If we crash between FAT1 and FAT2 writes, fsck
    // trusts FAT1 by convention and rebuilds FAT2.
    for (var fatIdx = 0; fatIdx < _fatCount; fatIdx++) {
      var fatBase = fatStart + fatIdx * _fatSize * _bytesPerSector;
      for (var i = 0; i < clusterCount; i++) {
        var nextVal = (i + 1 < clusterCount) ? newFirstCluster + i + 1 : EocMarker();
        WriteFatEntryStream(image, fatBase, newFirstCluster + i, nextVal);
      }
      image.Flush(); // Each FAT copy committed before mirroring.
    }

    // Step 2: Patch the directory entry. Single-sector 32-byte write.
    PatchDirectoryEntriesStream(image, fileName, oldFirstCluster, newFirstCluster);
    image.Flush();

    // Step 3: Free old FAT entries (dir entry no longer references them).
    for (var fatIdx = 0; fatIdx < _fatCount; fatIdx++) {
      var fatBase = fatStart + fatIdx * _fatSize * _bytesPerSector;
      for (var i = 0; i < clusterCount; i++)
        WriteFatEntryStream(image, fatBase, oldFirstCluster + i, 0);
      image.Flush();
    }
  }

  // ── IFilesystemMetadataMover ──────────────────────────────────────────

  /// <summary>The name the extent map gives the FAT32 root directory chain.</summary>
  private const string Fat32RootDirectory = "FAT32 root directory";

  /// <summary>
  /// Nothing, for now. On FAT12 and FAT16 there is genuinely nothing to move:
  /// the root lives in a fixed area between the FATs and the first data
  /// cluster, sized at format time and named by nothing, and the FATs and boot
  /// sector are pinned for the same reason. On FAT32 the root <em>is</em> an
  /// ordinary chain the BPB names, and <see cref="UpdateMetadataAfterMove" />
  /// repoints it correctly — but this descriptor relinks files in a second pass
  /// that replays the moves to work out where each cluster ended up, and that
  /// replay does not model a staged metadata hop. Offering the root while the
  /// two disagree produces a volume that passes fsck with the wrong bytes in
  /// its files, which is worse than leaving the root where it is.
  /// </summary>
  public IReadOnlySet<string> RelocatableMetadata { get; } =
    new HashSet<string>(StringComparer.OrdinalIgnoreCase);

  /// <inheritdoc />
    /// <summary>
  /// Performs the update metadata after move operation.
  /// </summary>
public void UpdateMetadataAfterMove(Stream image, string metadataName,
      long oldOffset, long newOffset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges = null) {
    ArgumentNullException.ThrowIfNull(image);
    if (_fatType != 32 || !string.Equals(metadataName, Fat32RootDirectory, StringComparison.OrdinalIgnoreCase))
      throw new NotSupportedException(
        $"FAT: '{metadataName}' is not a structure this volume can be repointed at.");

    var clusterCount = (int)((length + _clusterSize - 1) / _clusterSize);
    var oldFirstCluster = OffsetToCluster(oldOffset);
    var newFirstCluster = OffsetToCluster(newOffset);
    if (clusterCount <= 0 || oldFirstCluster == newFirstCluster) return;

    var fatStart = _reservedSectors * _bytesPerSector;

    // The new chain first: until the BPB names it, nothing reads it, so a crash
    // here costs only the clusters it claimed.
    for (var fatIdx = 0; fatIdx < _fatCount; fatIdx++) {
      var fatBase = fatStart + fatIdx * _fatSize * _bytesPerSector;
      for (var i = 0; i < clusterCount; i++) {
        var nextVal = i + 1 < clusterCount ? newFirstCluster + i + 1 : EocMarker();
        WriteFatEntryStream(image, fatBase, newFirstCluster + i, nextVal);
      }
      image.Flush();
    }

    // BPB_RootClus at offset 44 is the whole of the root's identity on FAT32.
    Span<byte> field = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)newFirstCluster);
    image.Position = 44;
    image.Write(field);
    image.Flush();

    // Only now are the old clusters unreferenced — except the ones a file has
    // moved onto in the meantime. Freeing those cut the file's chain, and it
    // read back short with the volume still looking consistent.
    for (var fatIdx = 0; fatIdx < _fatCount; fatIdx++) {
      var fatBase = fatStart + fatIdx * _fatSize * _bytesPerSector;
      for (var i = 0; i < clusterCount; i++) {
        var clusterOffset = ClusterToOffset(oldFirstCluster + i);
        if (IsLive(clusterOffset, _clusterSize, liveRanges)) continue;
        WriteFatEntryStream(image, fatBase, oldFirstCluster + i, 0);
      }
      image.Flush();
    }
  }

  /// <summary>Whether any live range covers part of this block.</summary>
  private static bool IsLive(long offset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges) {
    if (liveRanges == null) return false;
    foreach (var (start, len) in liveRanges)
      if (offset < start + len && start < offset + length)
        return true;
    return false;
  }

  // ── Stream-based FAT-entry helpers (targeted RMW) ──────────────────────

  /// <summary>
  /// Targeted write of a single FAT entry directly to the stream. For FAT12
  /// adjacent entries share a byte, so it's a 2-byte read-modify-write; for
  /// FAT16/32 the entry is byte-aligned so it's a direct write. Preserves the
  /// reserved upper 4 bits of FAT32 entries per the spec.
  /// </summary>
  private void WriteFatEntryStream(Stream image, long fatBase, int cluster, int value) {
    switch (_fatType) {
      case 12: {
        var pos = fatBase + cluster * 3 / 2;
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
        var pos = fatBase + cluster * 2;
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)value);
        image.Position = pos;
        image.Write(buf);
        break;
      }
      default: { // 32
        var pos = fatBase + cluster * 4;
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

  /// <summary>Reads a FAT entry directly from the stream (for chain walks).</summary>
  private int ReadFatEntryStream(Stream image, long fatBase, int cluster) {
    Span<byte> buf = stackalloc byte[4];
    switch (_fatType) {
      case 12: {
        var pos = fatBase + cluster * 3 / 2;
        image.Position = pos;
        image.ReadExactly(buf[..2]);
        var val = BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
        return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
      }
      case 16: {
        var pos = fatBase + cluster * 2;
        image.Position = pos;
        image.ReadExactly(buf[..2]);
        return BinaryPrimitives.ReadUInt16LittleEndian(buf[..2]);
      }
      default: { // 32
        var pos = fatBase + cluster * 4;
        image.Position = pos;
        image.ReadExactly(buf[..4]);
        return BinaryPrimitives.ReadInt32LittleEndian(buf[..4]) & 0x0FFFFFFF;
      }
    }
  }

  // ── Stream-based directory walking + 32-byte targeted patch ─────────────
  //
  // Walks the directory tree from disk one cluster (or sector) at a time. On
  // finding the entry whose start-cluster + reconstructed full path matches the
  // target, writes the patched 32-byte directory entry at its absolute offset.
  // No full-image load required — memory cost is O(clusterSize × recursion depth).

  private void PatchDirectoryEntriesStream(Stream image, string fileName, int oldFirstCluster, int newFirstCluster) {
    var target = fileName.TrimEnd('/');
    if (_fatType == 32) {
      // FAT32 root cluster lives at BPB offset 44.
      Span<byte> rcBuf = stackalloc byte[4];
      image.Position = 44;
      image.ReadExactly(rcBuf);
      var rootCluster = BinaryPrimitives.ReadInt32LittleEndian(rcBuf);
      PatchClusterDirStream(image, rootCluster, "", target, oldFirstCluster, newFirstCluster, []);
    } else {
      var rootOff = (long)(_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
      var rootSize = _rootDirSectors * _bytesPerSector;
      PatchFixedDirStream(image, rootOff, rootSize, "", target, oldFirstCluster, newFirstCluster);
    }
  }

  private void PatchClusterDirStream(Stream image, int dirCluster, string path, string target,
      int oldFirst, int newFirst, HashSet<int> seenDirs) {
    // Collect the cluster chain WITHOUT reading the cluster contents into a
    // single buffer — we'll read each cluster one at a time during the entry
    // walk, keeping the working set bounded to one clusterSize at a time.
    var cluster = dirCluster;
    var dirClusters = new List<int>();
    var seen = new HashSet<int>();
    var fatBase = _reservedSectors * _bytesPerSector;
    while (cluster >= 2 && cluster <= _totalDataClusters + 1 && !IsEoc(cluster) && seen.Add(cluster)) {
      dirClusters.Add(cluster);
      cluster = ReadFatEntryStream(image, fatBase, cluster);
    }
    PatchDirEntriesStream(image, dirClusters, isFixedRoot: false, fixedRootOffset: 0,
      path, target, oldFirst, newFirst, seenDirs);
  }

  private void PatchFixedDirStream(Stream image, long rootOff, int rootSize, string path,
      string target, int oldFirst, int newFirst) {
    // Fixed root: process as a single contiguous range. Pass the byte offset
    // directly and let the entry walker stream it.
    _ = rootSize; // size implied by max-entries iteration limit
    PatchDirEntriesStream(image, dirClusters: null, isFixedRoot: true, fixedRootOffset: rootOff,
      path, target, oldFirst, newFirst, []);
  }

  /// <summary>
  /// Walks 32-byte directory entries one cluster (or sector) at a time, reading
  /// from the stream. Tracks LFN parts across entries. On match: writes 32
  /// patched bytes at the entry's absolute offset (single-sector write — atomic
  /// at the device level on most hardware).
  /// </summary>
  private void PatchDirEntriesStream(Stream image, List<int>? dirClusters, bool isFixedRoot,
      long fixedRootOffset, string path, string target, int oldFirst, int newFirst,
      HashSet<int> seenDirs) {
    var lfnParts = new SortedDictionary<int, string>();

    // Iteration unit: clusterSize for chained dirs, sectorSize batches for the
    // fixed root. Either way, read one chunk at a time.
    var chunkSize = isFixedRoot ? _bytesPerSector : _clusterSize;
    var entriesPerChunk = chunkSize / 32;
    var buf = ArrayPool<byte>.Shared.Rent(chunkSize);

    try {
      // Pending recursion targets — accumulated during the walk so we don't
      // disturb the LFN-tracking iteration with a re-entrant call.
      var pendingSubdirs = new List<(int Cluster, string Path)>();

      // Compute how many chunks to walk. Fixed root: derived from _rootEntryCount.
      var chunkCount = isFixedRoot
        ? (_rootDirSectors * _bytesPerSector + chunkSize - 1) / chunkSize
        : (dirClusters?.Count ?? 0);

      var fileFound = false;
      for (var ci = 0; ci < chunkCount && !fileFound; ci++) {
        var chunkAbsOff = isFixedRoot
          ? fixedRootOffset + (long)ci * chunkSize
          : _firstDataByte + (long)(dirClusters![ci] - 2) * _clusterSize;
        image.Position = chunkAbsOff;
        image.ReadExactly(buf, 0, chunkSize);

        for (var i = 0; i < entriesPerChunk; i++) {
          var entryOff = i * 32;
          var firstByte = buf[entryOff];
          if (firstByte == 0x00) { fileFound = true; break; } // end of directory
          if (firstByte == 0xE5) { lfnParts.Clear(); continue; }

          var attr = buf[entryOff + 11];
          if ((attr & 0x3F) == 0x0F) {
            var seq = buf[entryOff] & 0x3F;
            var part = new StringBuilder();
            ReadLfn(buf, entryOff + 1, 5, part);
            ReadLfn(buf, entryOff + 14, 6, part);
            ReadLfn(buf, entryOff + 28, 2, part);
            lfnParts[seq] = part.ToString();
            continue;
          }
          if ((attr & 0x08) != 0) { lfnParts.Clear(); continue; } // volume label

          var shortName = GetShortName(buf, entryOff);
          string name;
          if (lfnParts.Count > 0) {
            var sb = new StringBuilder();
            foreach (var p in lfnParts.Values) sb.Append(p);
            name = sb.ToString().TrimEnd('\0', '\xFFFF');
            lfnParts.Clear();
          } else {
            name = shortName;
          }

          var isDir = (attr & 0x10) != 0;
          var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(entryOff + 26));
          if (_fatType == 32)
            startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(entryOff + 20)) << 16;

          if (name is "." or "..") continue;
          var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

          // Match: patch the start-cluster fields and write 32 bytes back.
          if (startCluster == oldFirst &&
              (fullPath.Equals(target, StringComparison.OrdinalIgnoreCase)
               || target.Equals("*", StringComparison.Ordinal))) {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(entryOff + 26), (ushort)(newFirst & 0xFFFF));
            if (_fatType == 32)
              BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(entryOff + 20), (ushort)((newFirst >> 16) & 0xFFFF));
            image.Position = chunkAbsOff + entryOff;
            image.Write(buf, entryOff, 32);
            // Found and patched — exit. (Caller will Flush.)
            fileFound = true;
            break;
          }

          // Subdir entry: defer recursion so the LFN state stays consistent
          // for the rest of this chunk.
          if (isDir && startCluster >= 2 && seenDirs.Add(startCluster))
            pendingSubdirs.Add((startCluster, fullPath));
        }
      }

      // Recurse into subdirs after finishing the current dir's walk.
      if (!fileFound) {
        foreach (var (cluster, subPath) in pendingSubdirs)
          PatchClusterDirStream(image, cluster, subPath, target, oldFirst, newFirst, seenDirs);
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }

  /// <summary>
  /// Patches the FAT chain and directory entry for a file whose clusters have been
  /// scattered to non-contiguous locations (e.g. interleaved defragmentation).
  /// Unlike <see cref="UpdateAllocationAfterMove"/> which assumes the old and new
  /// positions are contiguous runs, this method accepts an explicit list of old
  /// cluster numbers and an explicit list of new cluster numbers, frees the old ones,
  /// writes a chain linking the new ones in order, and patches the directory entry
  /// start-cluster to point at the first new cluster.
  /// </summary>
  /// <param name="image">The FAT image stream (readable, writable, seekable).</param>
  /// <param name="fileName">File name to match in directory entries.</param>
  /// <param name="oldClusters">Cluster numbers of the file's current chain (in chain order).</param>
  /// <param name="newClusters">Cluster numbers of the file's new chain (in desired order).</param>
  /// <inheritdoc />
  public int AllocationBlockSize => _clusterSize;

  /// <inheritdoc />
    /// <summary>
  /// Gets a value indicating whether supports scattered relink.
  /// </summary>
public bool SupportsScatteredRelink => true;

  /// <summary>
  /// The interface's shape of the relink below: the shared executor speaks in
  /// byte offsets because it does not know a format's allocation unit, so the
  /// offsets are turned into cluster numbers and handed to the same code the
  /// FAT descriptor's own two-phase pass uses.
  /// </summary>
  public void UpdateAllocationScattered(Stream image, string fileName,
      IReadOnlyList<long> oldBlockOffsets, IReadOnlyList<long> newBlockOffsets,
      IReadOnlySet<long>? blocksLiveElsewhere) {
    ArgumentNullException.ThrowIfNull(oldBlockOffsets);
    ArgumentNullException.ThrowIfNull(newBlockOffsets);

    var oldClusters = new int[oldBlockOffsets.Count];
    for (var i = 0; i < oldClusters.Length; ++i) oldClusters[i] = OffsetToCluster(oldBlockOffsets[i]);
    var newClusters = new int[newBlockOffsets.Count];
    for (var i = 0; i < newClusters.Length; ++i) newClusters[i] = OffsetToCluster(newBlockOffsets[i]);

    HashSet<int>? live = null;
    if (blocksLiveElsewhere != null) {
      live = [];
      foreach (var offset in blocksLiveElsewhere) live.Add(OffsetToCluster(offset));
    }

    this.UpdateAllocationScattered(image, fileName, oldClusters, newClusters, live);
  }

    /// <summary>
  /// Performs the update allocation scattered operation.
  /// </summary>
public void UpdateAllocationScattered(Stream image, string fileName, IReadOnlyList<int> oldClusters, IReadOnlyList<int> newClusters)
    => this.UpdateAllocationScattered(image, fileName, oldClusters, newClusters, clustersLiveElsewhere: null);

  /// <summary>
  /// As above, but told which clusters other files have already been relinked
  /// onto. A defragmentation relinks one owner at a time, and an owner's old
  /// clusters are frequently where another owner has just landed; freeing them
  /// blindly cuts that owner's chain and truncates its content.
  /// </summary>
  public void UpdateAllocationScattered(Stream image, string fileName, IReadOnlyList<int> oldClusters,
      IReadOnlyList<int> newClusters, IReadOnlySet<int>? clustersLiveElsewhere) {
    if (newClusters.Count == 0) return;

    var fatStart = _reservedSectors * _bytesPerSector;
    var newSet = new HashSet<int>(newClusters);

    // Step 1: Write the new chain (targeted writes per FAT entry, mirrored to
    // each FAT copy with a Flush() barrier so FAT2 only commits after FAT1).
    for (var fatIdx = 0; fatIdx < _fatCount; fatIdx++) {
      var fatBase = fatStart + fatIdx * _fatSize * _bytesPerSector;
      for (var i = 0; i < newClusters.Count; i++) {
        var nextVal = (i + 1 < newClusters.Count) ? newClusters[i + 1] : EocMarker();
        WriteFatEntryStream(image, fatBase, newClusters[i], nextVal);
      }
      image.Flush();
    }

    // Step 2: Patch the directory entry start-cluster.
    var oldFirstCluster = oldClusters.Count > 0 ? oldClusters[0] : -1;
    var newFirstCluster = newClusters[0];
    if (oldFirstCluster >= 0) {
      PatchDirectoryEntriesStream(image, fileName, oldFirstCluster, newFirstCluster);
      image.Flush();
    }

    // Step 3: Free old clusters that are NOT in this file's new chain, and not
    // in anyone else's either.
    for (var fatIdx = 0; fatIdx < _fatCount; fatIdx++) {
      var fatBase = fatStart + fatIdx * _fatSize * _bytesPerSector;
      foreach (var c in oldClusters)
        if (!newSet.Contains(c) && clustersLiveElsewhere?.Contains(c) != true)
          WriteFatEntryStream(image, fatBase, c, 0);
      image.Flush();
    }
  }

  /// <summary>
  /// After relocated subdirectories have had their cluster chains relinked,
  /// repatches the '.' (self) and '..' (parent) directory entries so they point
  /// at the directories' NEW start clusters. <paramref name="remap"/> maps each
  /// moved directory's OLD first cluster to its NEW first cluster.
  ///
  /// <para>Walks the live directory tree (using the already-corrected FAT chains
  /// and parent dirents), reading one cluster at a time. For a non-root
  /// directory the first 32-byte entry is '.' and the second is '..'; their
  /// start-cluster fields are rewritten in place when their current value is a
  /// key in <paramref name="remap"/>. The root directory has no '.'/'..' to fix.</para>
  /// </summary>
  public void RepatchDotEntries(Stream image, IReadOnlyDictionary<int, int> remap) {
    ArgumentNullException.ThrowIfNull(remap);
    if (remap.Count == 0) return;

    var seenDirs = new HashSet<int>();
    if (_fatType == 32) {
      Span<byte> rcBuf = stackalloc byte[4];
      image.Position = 44;
      image.ReadExactly(rcBuf);
      var rootCluster = BinaryPrimitives.ReadInt32LittleEndian(rcBuf);
      seenDirs.Add(rootCluster);
      WalkRepatchClusterDir(image, rootCluster, remap, seenDirs);
    } else {
      var rootOff = (long)(_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
      WalkRepatchFixedRoot(image, rootOff, remap, seenDirs);
    }
  }

  private void WalkRepatchFixedRoot(Stream image, long rootOff, IReadOnlyDictionary<int, int> remap, HashSet<int> seenDirs) {
    var chunkSize = _bytesPerSector;
    var chunkCount = (_rootDirSectors * _bytesPerSector + chunkSize - 1) / chunkSize;
    var buf = ArrayPool<byte>.Shared.Rent(chunkSize);
    try {
      for (var ci = 0; ci < chunkCount; ci++) {
        var chunkAbsOff = rootOff + (long)ci * chunkSize;
        image.Position = chunkAbsOff;
        image.ReadExactly(buf, 0, chunkSize);
        if (ScanRepatchChunk(image, buf, chunkSize, chunkAbsOff, remap, seenDirs, isRootEntries: true))
          return;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }

  private void WalkRepatchClusterDir(Stream image, int dirCluster, IReadOnlyDictionary<int, int> remap, HashSet<int> seenDirs) {
    // Gather the (already-correct) cluster chain for this directory.
    var fatBase = _reservedSectors * _bytesPerSector;
    var clusters = new List<int>();
    var seen = new HashSet<int>();
    var cluster = dirCluster;
    while (cluster >= 2 && cluster <= _totalDataClusters + 1 && !IsEoc(cluster) && seen.Add(cluster)) {
      clusters.Add(cluster);
      cluster = ReadFatEntryStream(image, fatBase, cluster);
    }

    var buf = ArrayPool<byte>.Shared.Rent(_clusterSize);
    try {
      var firstClusterOfDir = clusters.Count > 0 ? clusters[0] : dirCluster;
      for (var ci = 0; ci < clusters.Count; ci++) {
        var chunkAbsOff = _firstDataByte + (long)(clusters[ci] - 2) * _clusterSize;
        image.Position = chunkAbsOff;
        image.ReadExactly(buf, 0, _clusterSize);

        // '.' and '..' only live in the FIRST cluster of a subdirectory.
        if (ci == 0)
          PatchDotEntriesInFirstCluster(image, buf, chunkAbsOff, firstClusterOfDir, remap);

        if (ScanRepatchChunk(image, buf, _clusterSize, chunkAbsOff, remap, seenDirs, isRootEntries: false))
          return;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }

  /// <summary>
  /// Rewrites the '.' (entry 0) and '..' (entry 1) start-cluster fields in a
  /// subdirectory's first cluster when their stored value appears in the remap.
  /// </summary>
  private void PatchDotEntriesInFirstCluster(Stream image, byte[] buf, long chunkAbsOff, int dirFirstCluster, IReadOnlyDictionary<int, int> remap) {
    // Entry 0 must be '.', entry 1 must be '..' for a well-formed subdir.
    var dirty = false;
    for (var idx = 0; idx < 2; idx++) {
      var entryOff = idx * 32;
      if (entryOff + 32 > buf.Length) break;
      // Only touch genuine dot entries (name begins with '.').
      if (buf[entryOff] != (byte)'.') continue;

      var stored = (int)BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(entryOff + 26));
      if (_fatType == 32)
        stored |= BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(entryOff + 20)) << 16;

      // '.' (idx 0) should equal this directory's own first cluster; if the dir
      // moved its stored value is the OLD start → remap to NEW. '..' (idx 1)
      // names the parent; remap likewise when the parent moved.
      if (stored >= 2 && remap.TryGetValue(stored, out var mapped) && mapped != stored) {
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(entryOff + 26), (ushort)(mapped & 0xFFFF));
        if (_fatType == 32)
          BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(entryOff + 20), (ushort)((mapped >> 16) & 0xFFFF));
        dirty = true;
      }
    }
    if (dirty) {
      image.Position = chunkAbsOff;
      image.Write(buf, 0, Math.Min(64, buf.Length));
      image.Flush();
    }
  }

  /// <summary>
  /// Scans a directory chunk for child subdirectory entries, recursing into each
  /// to repatch its '.'/'..' pointers. Tracks LFN runs so a real 8.3 entry is
  /// recognised. Returns true if the end-of-directory marker (0x00) was hit.
  /// </summary>
  private bool ScanRepatchChunk(Stream image, byte[] buf, int chunkSize, long chunkAbsOff,
      IReadOnlyDictionary<int, int> remap, HashSet<int> seenDirs, bool isRootEntries) {
    _ = chunkAbsOff;
    var entries = chunkSize / 32;
    for (var i = 0; i < entries; i++) {
      var entryOff = i * 32;
      var firstByte = buf[entryOff];
      if (firstByte == 0x00) return true;          // end of directory
      if (firstByte == 0xE5) continue;             // deleted
      if (firstByte == (byte)'.') continue;        // '.' / '..' handled separately
      var attr = buf[entryOff + 11];
      if ((attr & 0x3F) == 0x0F) continue;         // LFN
      if ((attr & 0x08) != 0) continue;            // volume label
      if ((attr & 0x10) == 0) continue;            // not a directory

      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(entryOff + 26));
      if (_fatType == 32)
        startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(entryOff + 20)) << 16;
      if (startCluster >= 2 && seenDirs.Add(startCluster))
        WalkRepatchClusterDir(image, startCluster, remap, seenDirs);
    }
    return false;
  }

  // ── Directory entry patching ───────────────────────────────────────────

  private void PatchDirectoryEntries(byte[] data, string fileName, int oldFirstCluster, int newFirstCluster) {
    // Subdirectory extents arrive with a trailing "/" marker (e.g. "DIR1/" or
    // "DIR1/SUB/"); strip it so the path-equality match against fullPath in
    // PatchDirEntries succeeds — directory entries themselves carry no trailing
    // slash in their reconstructed full-path strings.
    var target = fileName.TrimEnd('/');
    if (_fatType == 32) {
      var rootCluster = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(44));
      PatchClusterDir(data, rootCluster, "", target, oldFirstCluster, newFirstCluster, []);
    } else {
      var rootOff = (_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
      var rootSize = _rootDirSectors * _bytesPerSector;
      PatchFixedDir(data, rootOff, rootSize, "", target, oldFirstCluster, newFirstCluster);
    }
  }

  private void PatchClusterDir(byte[] data, int dirCluster, string path, string target,
      int oldFirst, int newFirst, HashSet<int> seenDirs) {
    // Collect directory bytes from cluster chain
    using var ms = new MemoryStream();
    var cluster = dirCluster;
    var dirClusters = new List<int>();
    var seen = new HashSet<int>();
    while (cluster >= 2 && cluster <= _totalDataClusters + 1 && !IsEoc(cluster) && seen.Add(cluster)) {
      dirClusters.Add(cluster);
      var off = _firstDataSector + (long)(cluster - 2) * _sectorsPerCluster;
      var byteOff = (int)(off * _bytesPerSector);
      if (byteOff + _clusterSize > data.Length) break;
      ms.Write(data, byteOff, _clusterSize);
      cluster = ReadFatEntry(data, _reservedSectors * _bytesPerSector, cluster);
    }

    var dir = ms.ToArray();
    PatchDirEntries(data, dir, dirClusters, path, target, oldFirst, newFirst, seenDirs);
  }

  private void PatchFixedDir(byte[] data, int rootOff, int rootSize, string path, string target,
      int oldFirst, int newFirst) {
    var dir = data.AsSpan(rootOff, Math.Min(rootSize, data.Length - rootOff)).ToArray();
    var dirClusters = new List<int>(); // fixed root: not in cluster chain
    PatchDirEntries(data, dir, dirClusters, path, target, oldFirst, newFirst, []);
  }

  private void PatchDirEntries(byte[] data, byte[] dir, List<int> dirClusters, string path,
      string target, int oldFirst, int newFirst, HashSet<int> seenDirs) {
    var lfnParts = new SortedDictionary<int, string>();
    var maxEntries = dir.Length / 32;

    for (var i = 0; i < maxEntries; i++) {
      var off = i * 32;
      if (off + 32 > dir.Length) break;
      var firstByte = dir[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) { lfnParts.Clear(); continue; }

      var attr = dir[off + 11];
      if ((attr & 0x3F) == 0x0F) {
        var seq = dir[off] & 0x3F;
        var part = new StringBuilder();
        ReadLfn(dir, off + 1, 5, part);
        ReadLfn(dir, off + 14, 6, part);
        ReadLfn(dir, off + 28, 2, part);
        lfnParts[seq] = part.ToString();
        continue;
      }
      if ((attr & 0x08) != 0) { lfnParts.Clear(); continue; }

      var shortName = GetShortName(dir, off);
      string name;
      if (lfnParts.Count > 0) {
        var sb = new StringBuilder();
        foreach (var p in lfnParts.Values) sb.Append(p);
        name = sb.ToString().TrimEnd('\0', '\xFFFF');
        lfnParts.Clear();
      } else {
        name = shortName;
      }

      var isDir = (attr & 0x10) != 0;
      var startCluster = (int)BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 26));
      if (_fatType == 32)
        startCluster |= BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 20)) << 16;

      if (name is "." or "..") continue;
      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";

      // Check if this entry's start-cluster matches the old range
      if (startCluster == oldFirst &&
          (fullPath.Equals(target, StringComparison.OrdinalIgnoreCase) ||
           target.Equals("*", StringComparison.Ordinal))) {
        // Patch the start-cluster in the directory entry. We need to write back
        // into the actual image data, not the local copy.
        var imageOff = ResolveEntryImageOffset(data, dir, dirClusters, off);
        if (imageOff >= 0 && imageOff + 32 <= data.Length) {
          BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(imageOff + 26), (ushort)(newFirst & 0xFFFF));
          if (_fatType == 32)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(imageOff + 20), (ushort)((newFirst >> 16) & 0xFFFF));
        }
      }

      // Recurse into subdirectories
      if (isDir && startCluster >= 2 && seenDirs.Add(startCluster))
        PatchClusterDir(data, startCluster, fullPath, target, oldFirst, newFirst, seenDirs);
    }
  }

  /// <summary>
  /// Given a directory entry's offset within its local <paramref name="dir"/>
  /// buffer, resolve the corresponding offset in the full image byte array.
  /// For fixed root directories (FAT12/16), the <paramref name="dirClusters"/>
  /// list is empty and we fall back to the fixed root offset.
  /// </summary>
  private int ResolveEntryImageOffset(byte[] data, byte[] dir, List<int> dirClusters, int localOff) {
    if (dirClusters.Count == 0) {
      // Fixed root directory (FAT12/16)
      var rootOff = (_reservedSectors + _fatCount * _fatSize) * _bytesPerSector;
      return rootOff + localOff;
    }

    // Cluster-chain directory (FAT32 or subdirectory)
    var clusterIdx = localOff / _clusterSize;
    var intraClusterOff = localOff % _clusterSize;
    if (clusterIdx >= dirClusters.Count) return -1;
    var c = dirClusters[clusterIdx];
    return (int)(_firstDataByte + (long)(c - 2) * _clusterSize + intraClusterOff);
  }

  // ── FAT entry read/write helpers ───────────────────────────────────────

  private int ReadFatEntry(byte[] data, int fatStart, int cluster) => _fatType switch {
    12 => ReadFat12(data, fatStart, cluster),
    16 => fatStart + cluster * 2 + 2 <= data.Length
      ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fatStart + cluster * 2))
      : 0xFFF,
    _ => fatStart + cluster * 4 + 4 <= data.Length
      ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(fatStart + cluster * 4)) & 0x0FFFFFFF
      : 0x0FFFFFF8,
  };

  private static int ReadFat12(byte[] data, int fatStart, int cluster) {
    var off = fatStart + cluster * 3 / 2;
    if (off + 2 > data.Length) return 0xFFF;
    var val = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off));
    return (cluster & 1) != 0 ? val >> 4 : val & 0xFFF;
  }

  private void WriteFatEntry(byte[] data, int fatBase, int cluster, int value) {
    switch (_fatType) {
      case 12: {
        var pos = fatBase + cluster * 3 / 2;
        if (pos + 1 >= data.Length) return;
        if ((cluster & 1) == 0) {
          data[pos] = (byte)(value & 0xFF);
          data[pos + 1] = (byte)((data[pos + 1] & 0xF0) | ((value >> 8) & 0x0F));
        } else {
          data[pos] = (byte)((data[pos] & 0x0F) | ((value << 4) & 0xF0));
          data[pos + 1] = (byte)((value >> 4) & 0xFF);
        }
        break;
      }
      case 16: {
        var pos = fatBase + cluster * 2;
        if (pos + 2 <= data.Length)
          BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), (ushort)value);
        break;
      }
      default: {
        var pos = fatBase + cluster * 4;
        if (pos + 4 <= data.Length)
          BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), (uint)value & 0x0FFFFFFFu);
        break;
      }
    }
  }

  private bool IsEoc(int cluster) => _fatType switch {
    12 => cluster >= 0xFF8,
    16 => cluster >= 0xFFF8,
    _ => cluster >= 0x0FFFFFF8,
  };

  private int EocMarker() => _fatType switch {
    12 => 0xFFF,
    16 => 0xFFFF,
    _ => 0x0FFFFFFF,
  };

  // ── Helpers ────────────────────────────────────────────────────────────

  private static void ReadLfn(byte[] data, int offset, int count, StringBuilder sb) {
    for (var j = 0; j < count; j++) {
      var charOff = offset + j * 2;
      if (charOff + 2 > data.Length) break;
      var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(charOff));
      if (c == 0 || c == 0xFFFF) break;
      sb.Append(c);
    }
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  // ── Public helpers for the planner ─────────────────────────────────────

  /// <summary>Byte offset of the first data cluster in the image.</summary>
  public long FirstDataByte => _firstDataByte;

  /// <summary>Bytes per cluster.</summary>
  public int ClusterSize => _clusterSize;

  /// <summary>FAT type (12, 16, or 32).</summary>
  public int FatType => _fatType;

  /// <summary>Total data clusters in the image.</summary>
  public int TotalDataClusters => _totalDataClusters;

  /// <summary>
  /// Walks the FAT chain for a given file and returns its clusters as a list.
  /// </summary>
  public List<int> GetChain(byte[] data, int startCluster) {
    var chain = new List<int>();
    var cluster = startCluster;
    var fatStart = _reservedSectors * _bytesPerSector;
    while (cluster >= 2 && cluster <= _totalDataClusters + 1 && !IsEoc(cluster) && chain.Count <= _totalDataClusters) {
      chain.Add(cluster);
      cluster = ReadFatEntry(data, fatStart, cluster);
    }
    return chain;
  }

  /// <summary>Converts a cluster number to a byte offset.</summary>
  public long ClusterOffset(int cluster) => ClusterToOffset(cluster);

  /// <summary>Converts a byte offset to a cluster number.</summary>
  public int OffsetCluster(long offset) => OffsetToCluster(offset);
}
