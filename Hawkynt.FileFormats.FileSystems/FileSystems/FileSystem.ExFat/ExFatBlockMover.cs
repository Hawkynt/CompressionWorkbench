#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.ExFat;

/// <summary>
/// In-place exFAT block mover. Moves cluster-aligned extents and patches
/// FAT chain entries, allocation bitmap, directory entry sets, and VBR PercentInUse.
/// <para>
/// Streaming: never loads the whole image. All metadata updates are targeted
/// writes with <see cref="Stream.Flush"/> barriers between the four steps so
/// a crash mid-operation leaves the image in an fsck-recoverable state. The
/// FAT (potentially 50 GB on a 50 TB volume) is navigated via
/// <see cref="SectorCache"/> with a bounded ~256 MB memory cap.
/// </para>
/// </summary>
public sealed class ExFatBlockMover : IFilesystemBlockMover, IFilesystemMetadataMover {
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _clusterSize;
  private long _fatOffset;
  private long _clusterHeapOffset;
  private uint _clusterCount;
  private uint _rootCluster;

  /// <summary>Initialises the mover by parsing exFAT VBR from a byte buffer.</summary>
  public void Init(byte[] image) => InitFromVbr(image.AsSpan(0, Math.Min(image.Length, 512)));

  /// <summary>Stream-based init — reads only the 512-byte VBR.</summary>
  public void Init(Stream image) {
    Span<byte> vbr = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(vbr);
    InitFromVbr(vbr);
  }

  private void InitFromVbr(ReadOnlySpan<byte> vbr) {
    _bytesPerSector = 1 << vbr[108];
    _sectorsPerCluster = 1 << vbr[109];
    _clusterSize = _bytesPerSector * _sectorsPerCluster;
    var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(vbr[80..]);
    var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(vbr[88..]);
    _clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(vbr[92..]);
    _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(vbr[96..]);
    _fatOffset = (long)fatOffsetSectors * _bytesPerSector;
    _clusterHeapOffset = (long)clusterHeapOffsetSectors * _bytesPerSector;
  }

  /// <summary>
  /// Gets the first data byte.
  /// </summary>
  public long FirstDataByte => _clusterHeapOffset;
  /// <summary>
  /// Gets the cluster size.
  /// </summary>
  public int ClusterSize => _clusterSize;

  /// <summary>
  /// Upper bound of the exFAT volume as declared by the VBR — clusterHeapOffset
  /// + clusterCount × clusterSize. The defrag planner must use THIS as its
  /// "imageSize" rather than the stream length: when the exFAT image sits
  /// inside a larger container (partition window, sparse VHD), the stream
  /// length includes padding bytes that are outside the volume. Targeting
  /// offsets above this bound corrupts the FAT (cluster N's entry lives at
  /// fatOffset + N*4 — large N writes into the cluster heap).
  /// </summary>
  public long VolumeSize => _clusterHeapOffset + (long)_clusterCount * _clusterSize;

  private uint OffsetToCluster(long offset) => (uint)((offset - _clusterHeapOffset) / _clusterSize) + 2;

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
  /// Power-fail-safe four-step update:
  ///   1. Allocate new FAT chain (targeted writes, flush).
  ///   2. Patch directory entry-set (32-byte targeted write + checksum recompute, flush).
  ///   3. Free old FAT entries (targeted writes, flush).
  ///   4. Update allocation bitmap + PercentInUse (targeted RMW writes, flush).
  /// </remarks>
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var clusterCount = (int)((length + _clusterSize - 1) / _clusterSize);
    var oldFirstCluster = OffsetToCluster(oldOffset);
    var newFirstCluster = OffsetToCluster(newOffset);

    using var cache = new SectorCache(image);
    var entry = FindEntrySetStream(image, cache, fileName, oldFirstCluster)
      ?? throw new InvalidOperationException(
        $"exFAT: no directory entry for '{fileName}' starts at cluster {oldFirstCluster}; nothing was moved.");

    // Step 1: Allocate new FAT chain.
    for (var i = 0; i < clusterCount; i++) {
      var next = i + 1 < clusterCount ? newFirstCluster + (uint)(i + 1) : 0xFFFFFFFFu;
      WriteFatStream(image, newFirstCluster + (uint)i, next);
      cache.Invalidate(_fatOffset + (long)(newFirstCluster + i) * 4, 4);
    }
    image.Flush();

    // Step 2: Patch directory entry — find Stream Extension by old FirstCluster
    // + name match, write new FirstCluster (32-byte rewrite + checksum).
    PatchEntrySetStream(image, cache, entry, newFirstCluster, contiguous: true);
    image.Flush();

    // Step 3: Free old FAT entries.
    for (var i = 0; i < clusterCount; i++) {
      WriteFatStream(image, oldFirstCluster + (uint)i, 0);
      cache.Invalidate(_fatOffset + (long)(oldFirstCluster + i) * 4, 4);
    }
    image.Flush();

    // Step 4: Update allocation bitmap (RMW per bit) + PercentInUse.
    var bmpOffset = FindBitmapOffsetStream(image, cache);
    if (bmpOffset >= 0) {
      for (var i = 0; i < clusterCount; i++) {
        ClearBitmapBitStream(image, bmpOffset, oldFirstCluster + (uint)i);
        SetBitmapBitStream(image, bmpOffset, newFirstCluster + (uint)i);
      }
      image.Flush();
      UpdatePercentInUseStream(image, bmpOffset);
      image.Flush();
    }
  }

  // ── IFilesystemMetadataMover ──────────────────────────────────────────

  /// <summary>Directory-entry type of the allocation bitmap.</summary>
  private const byte BitmapEntryType = 0x81;

  /// <summary>Directory-entry type of the up-case table.</summary>
  private const byte UpcaseEntryType = 0x82;

  /// <summary>
  /// The allocation bitmap and the up-case table. exFAT keeps both as ordinary
  /// files: each has a directory entry in the root recording its first cluster,
  /// which is the whole of what says where it is. The FAT and the boot region
  /// are pinned — their positions are fields in the boot sector, and rewriting
  /// those means recomputing the boot checksum sector as well, which is a
  /// different operation from repointing a file. The root directory is pinned
  /// for the same reason.
  /// </summary>
  public IReadOnlySet<string> RelocatableMetadata { get; } =
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "<bitmap>", "<upcase>" };

  /// <inheritdoc />
  public void UpdateMetadataAfterMove(Stream image, string metadataName,
      long oldOffset, long newOffset, long length,
      IReadOnlyList<(long Offset, long Length)>? liveRanges = null) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadataName);

    var entryType = metadataName switch {
      "<bitmap>" => BitmapEntryType,
      "<upcase>" => UpcaseEntryType,
      _ => throw new NotSupportedException(
        $"exFAT: '{metadataName}' is not a structure this volume can be repointed at."),
    };

    var clusterCount = (int)((length + _clusterSize - 1) / _clusterSize);
    var oldFirstCluster = OffsetToCluster(oldOffset);
    var newFirstCluster = OffsetToCluster(newOffset);
    if (clusterCount <= 0 || oldFirstCluster == newFirstCluster) return;

    using var cache = new SectorCache(image);

    // The new chain first: nothing reads it until the entry names it.
    for (var i = 0; i < clusterCount; i++) {
      var next = i + 1 < clusterCount ? newFirstCluster + (uint)(i + 1) : 0xFFFFFFFFu;
      WriteFatStream(image, newFirstCluster + (uint)i, next);
      cache.Invalidate(_fatOffset + (long)(newFirstCluster + i) * 4, 4);
    }
    image.Flush();

    if (!PatchRootEntryFirstCluster(image, entryType, newFirstCluster))
      throw new NotSupportedException(
        $"exFAT: the root directory has no {metadataName} entry to repoint.");
    image.Flush();
    cache.InvalidateAll();

    // The bitmap is read through its entry, so this now finds it at its new
    // home — which is what makes moving the bitmap itself work.
    var bitmapOffset = FindBitmapOffsetStream(image, cache);
    if (bitmapOffset >= 0) {
      for (var i = 0; i < clusterCount; i++)
        SetBitmapBitStream(image, bitmapOffset, newFirstCluster + (uint)i);
      image.Flush();

      for (var i = 0; i < clusterCount; i++) {
        var releasing = _clusterHeapOffset + (long)(oldFirstCluster + (uint)i - 2) * _clusterSize;
        if (IsLive(releasing, _clusterSize, liveRanges)) continue;
        ClearBitmapBitStream(image, bitmapOffset, oldFirstCluster + (uint)i);
      }
      image.Flush();
      UpdatePercentInUseStream(image, bitmapOffset);
      image.Flush();
    }

    // Only now is the old chain unreferenced — minus whatever moved onto it.
    for (var i = 0; i < clusterCount; i++) {
      var releasing = _clusterHeapOffset + (long)(oldFirstCluster + (uint)i - 2) * _clusterSize;
      if (IsLive(releasing, _clusterSize, liveRanges)) continue;
      WriteFatStream(image, oldFirstCluster + (uint)i, 0);
    }
    image.Flush();
  }

  /// <summary>
  /// Writes a new first cluster into the root's bitmap or up-case entry. Both
  /// are single 32-byte records carrying the cluster at offset 20 — unlike a
  /// file, which spreads its name and stream across a set of entries.
  /// </summary>
  private bool PatchRootEntryFirstCluster(Stream image, byte entryType, uint newFirstCluster) {
    using var cache = new SectorCache(image);
    var cluster = _rootCluster;
    var visited = new HashSet<uint>();
    var entry = new byte[32];

    while (cluster >= 2 && cluster != 0xFFFFFFFF && visited.Add(cluster)) {
      var clusterOffset = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize;
      if (clusterOffset + _clusterSize > image.Length) return false;

      for (var i = 0; i < _clusterSize; i += 32) {
        image.Position = clusterOffset + i;
        image.ReadExactly(entry);
        if (entry[0] == 0x00) return false;          // end of directory
        if (entry[0] != entryType) continue;

        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(20), newFirstCluster);
        image.Position = clusterOffset + i;
        image.Write(entry);
        return true;
      }

      cluster = ReadFatStream(cache, _fatOffset, cluster);
    }
    return false;
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

  // ── Scattered relink ──────────────────────────────────────────────────

  /// <inheritdoc />
  public int AllocationBlockSize => _clusterSize;

  /// <inheritdoc />
  public bool SupportsScatteredRelink => true;

  /// <summary>
  /// Rewrites one file's whole allocation in a single pass, after every byte
  /// has moved.
  /// </summary>
  /// <remarks>
  /// Relinking per move released each run's old clusters immediately, and one
  /// file's old clusters are routinely where the next file has just landed — so
  /// the second file's chain was cut and it read back as its first cluster
  /// alone. Doing it once per file, with the set of clusters that are live after
  /// the moves in hand, is what keeps a plan that shuffles files past each other
  /// intact.
  /// </remarks>
  public void UpdateAllocationScattered(Stream image, string fileName,
      IReadOnlyList<long> oldBlockOffsets, IReadOnlyList<long> newBlockOffsets,
      IReadOnlySet<long>? blocksLiveElsewhere) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(oldBlockOffsets);
    ArgumentNullException.ThrowIfNull(newBlockOffsets);
    if (newBlockOffsets.Count == 0 || oldBlockOffsets.Count != newBlockOffsets.Count) return;

    var oldClusters = new uint[oldBlockOffsets.Count];
    var newClusters = new uint[newBlockOffsets.Count];
    for (var i = 0; i < oldClusters.Length; ++i) {
      oldClusters[i] = OffsetToCluster(oldBlockOffsets[i]);
      newClusters[i] = OffsetToCluster(newBlockOffsets[i]);
    }
    if (oldClusters[0] == newClusters[0] && oldClusters.SequenceEqual(newClusters)) return;

    // The stream extension says whether the file is one contiguous run
    // (NoFatChain) or follows the FAT. A result that is contiguous keeps the flag;
    // a scattered one clears it, and the chain written below is what it follows.
    var contiguous = true;
    for (var i = 1; i < newClusters.Length && contiguous; ++i)
      contiguous = newClusters[i] == newClusters[i - 1] + 1;

    using var cache = new SectorCache(image);

    // Find the entry before anything is written: a file whose entry cannot be
    // found must not have its chain moved out from under it.
    var entry = FindEntrySetStream(image, cache, fileName, oldClusters[0])
      ?? throw new InvalidOperationException(
        $"exFAT: no directory entry for '{fileName}' starts at cluster {oldClusters[0]}; nothing was moved.");

    // The new chain, in the file's own order.
    for (var i = 0; i < newClusters.Length; ++i) {
      var next = i + 1 < newClusters.Length ? newClusters[i + 1] : 0xFFFFFFFFu;
      WriteFatStream(image, newClusters[i], next);
      cache.Invalidate(_fatOffset + (long)newClusters[i] * 4, 4);
    }
    image.Flush();

    PatchEntrySetStream(image, cache, entry, newClusters[0], contiguous);
    image.Flush();

    // Old clusters are free again — except where this or another file now sits.
    var claimed = new HashSet<uint>(newClusters);
    for (var i = 0; i < oldClusters.Length; ++i) {
      if (claimed.Contains(oldClusters[i])) continue;
      if (blocksLiveElsewhere?.Contains(oldBlockOffsets[i]) == true) continue;
      WriteFatStream(image, oldClusters[i], 0);
      cache.Invalidate(_fatOffset + (long)oldClusters[i] * 4, 4);
    }
    image.Flush();

    var bitmapOffset = FindBitmapOffsetStream(image, cache);
    if (bitmapOffset < 0) return;

    foreach (var cluster in newClusters)
      SetBitmapBitStream(image, bitmapOffset, cluster);
    for (var i = 0; i < oldClusters.Length; ++i) {
      if (claimed.Contains(oldClusters[i])) continue;
      if (blocksLiveElsewhere?.Contains(oldBlockOffsets[i]) == true) continue;
      ClearBitmapBitStream(image, bitmapOffset, oldClusters[i]);
    }
    image.Flush();
    UpdatePercentInUseStream(image, bitmapOffset);
    image.Flush();
  }

  // ── Streaming FAT / bitmap helpers ─────────────────────────────────────

  private void WriteFatStream(Stream image, uint cluster, uint value) {
    var pos = _fatOffset + (long)cluster * 4;
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
    image.Position = pos;
    image.Write(buf);
  }

  private static uint ReadFatStream(SectorCache cache, long fatOffset, uint cluster) {
    Span<byte> buf = stackalloc byte[4];
    cache.Read(fatOffset + (long)cluster * 4, buf);
    return BinaryPrimitives.ReadUInt32LittleEndian(buf);
  }

  private static void SetBitmapBitStream(Stream image, long bmpOffset, uint cluster) {
    var bit = (int)(cluster - 2);
    var bytePos = bmpOffset + bit / 8;
    Span<byte> buf = stackalloc byte[1];
    image.Position = bytePos;
    image.ReadExactly(buf);
    buf[0] |= (byte)(1 << (bit % 8));
    image.Position = bytePos;
    image.Write(buf);
  }

  private static void ClearBitmapBitStream(Stream image, long bmpOffset, uint cluster) {
    var bit = (int)(cluster - 2);
    var bytePos = bmpOffset + bit / 8;
    Span<byte> buf = stackalloc byte[1];
    image.Position = bytePos;
    image.ReadExactly(buf);
    buf[0] &= (byte)~(1 << (bit % 8));
    image.Position = bytePos;
    image.Write(buf);
  }

  /// <summary>Locates the allocation-bitmap data offset by walking the root dir.</summary>
  private long FindBitmapOffsetStream(Stream image, SectorCache cache) {
    var clusterBuf = ArrayPool<byte>.Shared.Rent(_clusterSize);
    try {
      var cluster = _rootCluster;
      var seen = new HashSet<uint>();
      while (cluster >= 2 && cluster <= _clusterCount + 1 && cluster < 0xFFFFFFF8 && seen.Add(cluster)) {
        var off = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize;
        if (off + _clusterSize > image.Length) return -1;
        image.Position = off;
        image.ReadExactly(clusterBuf, 0, _clusterSize);
        for (var i = 0; i < _clusterSize; i += 32) {
          if (clusterBuf[i] == 0x00) return -1;
          if (clusterBuf[i] == 0x81) {
            var bmpCluster = BinaryPrimitives.ReadUInt32LittleEndian(clusterBuf.AsSpan(i + 20));
            return _clusterHeapOffset + (long)(bmpCluster - 2) * _clusterSize;
          }
        }
        cluster = ReadFatStream(cache, _fatOffset, cluster);
      }
      return -1;
    } finally {
      ArrayPool<byte>.Shared.Return(clusterBuf);
    }
  }

  /// <summary>Where one file's directory entry set sits on disk.</summary>
  private sealed record EntrySetLocation(long Offset, int Length);

  /// <summary>
  /// Finds the entry set of the file named <paramref name="fileName" /> (its full
  /// path or leaf; "*" matches any) whose data starts at <paramref name="firstCluster" />,
  /// searching the root and every folder below it.
  /// </summary>
  private EntrySetLocation? FindEntrySetStream(Stream image, SectorCache cache, string fileName, uint firstCluster) {
    var wanted = fileName.Replace('\\', '/').Trim('/');
    var leaf = wanted[(wanted.LastIndexOf('/') + 1)..];
    var pending = new Stack<(uint First, bool NoFatChain, long Length, string Path)>();
    pending.Push((_rootCluster, false, long.MaxValue, ""));
    var seenDirs = new HashSet<uint>();
    EntrySetLocation? byLeaf = null;
    while (pending.Count > 0) {
      var (dirFirst, noFatChain, dirLength, dirPath) = pending.Pop();
      if (!seenDirs.Add(dirFirst)) continue;
      var bytes = ReadDirectoryStream(image, cache, dirFirst, noFatChain, dirLength);
      for (var i = 0; i + 32 <= bytes.Length; i += 32) {
        if (bytes[i] == 0x00) break;
        if (bytes[i] != 0x85) continue;
        var secCount = bytes[i + 1];
        var streamStart = i + 32;
        if (streamStart + 32 > bytes.Length || bytes[streamStart] != 0xC0) continue;
        var attributes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 4));
        var first = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(streamStart + 20));
        var length = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(streamStart + 24));
        var flags = bytes[streamStart + 1];
        var entryPath = dirPath.Length == 0 ? ReadEntryName(bytes, streamStart) : dirPath + "/" + ReadEntryName(bytes, streamStart);
        if ((attributes & 0x10) != 0) {
          if (first >= 2) pending.Push((first, (flags & 0x02) != 0, length, entryPath));
          i += secCount * 32;
          continue;
        }
        if (first == firstCluster) {
          var nameLength = bytes[streamStart + 3];
          var sb = new StringBuilder();
          for (var n = 0; n < (nameLength + 14) / 15; n++) {
            var nPos = streamStart + 32 + n * 32;
            if (nPos + 32 > bytes.Length || bytes[nPos] != 0xC1) break;
            var chars = Math.Min(15, nameLength - n * 15);
            for (var c = 0; c < chars; c++) {
              var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(nPos + 2 + c * 2));
              if (ch == 0) break;
              sb.Append(ch);
            }
          }
          var exact = entryPath.Equals(wanted, StringComparison.OrdinalIgnoreCase);
          if (exact || sb.ToString().Equals(leaf, StringComparison.OrdinalIgnoreCase) || fileName == "*") {
            var setBytes = (1 + secCount) * 32;
            var offset = DirectoryByteOffset(cache, dirFirst, noFatChain, dirLength, i);
            // Patching an entry set in place needs it inside one cluster.
            if (offset >= 0 && DirectoryByteOffset(cache, dirFirst, noFatChain, dirLength, i + setBytes - 1) == offset + setBytes - 1) {
              var found = new EntrySetLocation(offset, setBytes);
              if (exact) return found;
              byLeaf ??= found;
            }
          }
        }
        i += secCount * 32;
      }
    }
    return byLeaf;
  }

  private static string ReadEntryName(byte[] bytes, int streamStart) {
    var nameLength = bytes[streamStart + 3];
    var sb = new StringBuilder();
    for (var n = 0; n < (nameLength + 14) / 15; n++) {
      var nPos = streamStart + 32 + n * 32;
      if (nPos + 32 > bytes.Length || bytes[nPos] != 0xC1) break;
      var chars = Math.Min(15, nameLength - n * 15);
      for (var c = 0; c < chars; c++) {
        var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(nPos + 2 + c * 2));
        if (ch == 0) break;
        sb.Append(ch);
      }
    }
    return sb.ToString();
  }

  /// <summary>The clusters of a directory, in order.</summary>
  private List<uint> DirectoryClusters(SectorCache cache, uint first, bool noFatChain, long length) {
    var clusters = new List<uint>();
    if (noFatChain) {
      var count = Math.Min((length + _clusterSize - 1) / _clusterSize, (long)_clusterCount + 2 - first);
      for (var k = 0L; k < count; k++) clusters.Add(first + (uint)k);
      return clusters;
    }
    var seen = new HashSet<uint>();
    for (var c = first; c >= 2 && c <= _clusterCount + 1 && c < 0xFFFFFFF8 && seen.Add(c); c = ReadFatStream(cache, _fatOffset, c))
      clusters.Add(c);
    return clusters;
  }

  private byte[] ReadDirectoryStream(Stream image, SectorCache cache, uint first, bool noFatChain, long length) {
    var clusters = DirectoryClusters(cache, first, noFatChain, length);
    var bytes = new byte[clusters.Count * _clusterSize];
    for (var k = 0; k < clusters.Count; k++) {
      var off = _clusterHeapOffset + (long)(clusters[k] - 2) * _clusterSize;
      if (off + _clusterSize > image.Length) break;
      cache.Read(off, bytes.AsSpan(k * _clusterSize, _clusterSize));
    }
    return bytes;
  }

  private long DirectoryByteOffset(SectorCache cache, uint first, bool noFatChain, long length, int index) {
    var clusters = DirectoryClusters(cache, first, noFatChain, length);
    var k = index / _clusterSize;
    if (k >= clusters.Count) return -1;
    return _clusterHeapOffset + (long)(clusters[k] - 2) * _clusterSize + index % _clusterSize;
  }

  /// <summary>
  /// Points an entry set at <paramref name="newFirst" />, sets or clears NoFatChain
  /// for a contiguous or scattered result, and recomputes the entry-set checksum.
  /// </summary>
  private static void PatchEntrySetStream(Stream image, SectorCache cache, EntrySetLocation entry, uint newFirst, bool contiguous) {
    var set = new byte[entry.Length];
    image.Position = entry.Offset;
    image.ReadExactly(set);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(32 + 20), newFirst);
    set[32 + 1] = contiguous ? (byte)(set[32 + 1] | 0x02) : (byte)(set[32 + 1] & ~0x02);
    ushort checksum = 0;
    for (var j = 0; j < set.Length; j++) {
      if (j == 2 || j == 3) continue; // the checksum field itself
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[j]) & 0xFFFF);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(2), checksum);
    image.Position = entry.Offset;
    image.Write(set);
    cache.Invalidate(entry.Offset, set.Length);
  }

  /// <summary>
  /// Computes the PercentInUse field from the allocation bitmap and writes it
  /// to VBR offset 112 + backup VBR (sector 12 + 112). Streams the bitmap
  /// in chunks rather than loading it whole.
  /// </summary>
  private void UpdatePercentInUseStream(Stream image, long bmpOffset) {
    if (_clusterCount == 0) return;
    var bmpLen = (int)((_clusterCount + 7) / 8);
    var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
    try {
      var used = 0u;
      var read = 0;
      while (read < bmpLen) {
        var chunk = Math.Min(buf.Length, bmpLen - read);
        image.Position = bmpOffset + read;
        var n = 0;
        while (n < chunk) {
          var got = image.Read(buf, n, chunk - n);
          if (got <= 0) break;
          n += got;
        }
        for (var i = 0; i < n; i++)
          used += (uint)BitOperations.PopCount(buf[i]);
        read += n;
        if (n < chunk) break;
      }
      var pct = (byte)Math.Min(100u, used * 100u / _clusterCount);
      Span<byte> single = stackalloc byte[1] { pct };
      image.Position = 112;
      image.Write(single);
      var backupOff = 12 * _bytesPerSector;
      if (backupOff + 113 <= image.Length) {
        image.Position = backupOff + 112;
        image.Write(single);
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }
}
