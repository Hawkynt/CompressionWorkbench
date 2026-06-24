#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// In-place exFAT modifier — true O(touched bytes) random-access I/O.
/// Touches only: VBR primary+backup (3 bytes — PercentInUse), the FAT entries
/// for the new/freed clusters, the allocation-bitmap byte(s) covering those
/// clusters, the root-directory cluster(s) holding the entry-set, and the
/// new file's data clusters. The up-case table and other files are never read.
/// <para>
/// Layout reminders (matches <see cref="ExFatWriter"/>):
/// <list type="bullet">
///   <item>VBR at sector 0; backup VBR at sector 12.</item>
///   <item>FAT starts at <c>fatOffsetSectors</c>; 4 bytes per cluster, EOC = 0xFFFFFFFF.</item>
///   <item>Cluster heap at <c>clusterHeapOffsetSectors</c>; cluster numbering starts at 2.</item>
///   <item>Cluster 2 = root dir, cluster 3 = allocation bitmap, cluster 4 = up-case table.</item>
///   <item>Root entry-set order: 0x83 VolumeLabel, 0x81 AllocationBitmap, 0x82 UpCase, then files.</item>
///   <item>Per-file entry-set: 0x85 File + 0xC0 StreamExtension + N × 0xC1 FileName (15 UTF-16 chars each).</item>
///   <item>Entry-set checksum per spec §7.4.3 — rotate-right-add over every byte except bytes 2-3 of the File entry.</item>
/// </list></para>
/// </summary>
public static class ExFatModifier {
  private const uint EocMarker = 0xFFFFFFFFu;

  // ── VBR struct decoded once per call ─────────────────────────────────

  private readonly record struct Layout(
    int BytesPerSector,
    int SectorsPerCluster,
    int ClusterSize,
    int FatOffset,
    int ClusterHeapOffset,
    uint ClusterCount,
    uint RootDirCluster
  );

  private static Layout ReadLayout(Stream image) {
    Span<byte> hdr = stackalloc byte[120];
    image.Position = 0;
    image.ReadExactly(hdr);
    if (Encoding.ASCII.GetString(hdr.Slice(3, 8)) != "EXFAT   ")
      throw new InvalidDataException("exFAT: invalid signature.");
    var bytesPerSector = 1 << hdr[108];
    var sectorsPerCluster = 1 << hdr[109];
    var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(hdr[80..]);
    var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(hdr[88..]);
    var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr[92..]);
    var rootDirCluster = BinaryPrimitives.ReadUInt32LittleEndian(hdr[96..]);
    return new Layout(
      bytesPerSector, sectorsPerCluster, bytesPerSector * sectorsPerCluster,
      (int)(fatOffsetSectors * (uint)bytesPerSector),
      (int)(clusterHeapOffsetSectors * (uint)bytesPerSector),
      clusterCount, rootDirCluster);
  }

  // ── FAT helpers ──────────────────────────────────────────────────────

  private static uint ReadFatEntry(Stream image, Layout l, uint cluster) {
    Span<byte> buf = stackalloc byte[4];
    image.Position = l.FatOffset + (long)cluster * 4;
    image.ReadExactly(buf);
    return BinaryPrimitives.ReadUInt32LittleEndian(buf);
  }

  private static void WriteFatEntry(Stream image, Layout l, uint cluster, uint value) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
    image.Position = l.FatOffset + (long)cluster * 4;
    image.Write(buf);
  }

  private static List<uint> WalkChain(Stream image, Layout l, uint startCluster) {
    var chain = new List<uint>();
    var seen = new HashSet<uint>();
    var cluster = startCluster;
    while (cluster >= 2 && cluster <= l.ClusterCount + 1 && seen.Add(cluster) && chain.Count <= l.ClusterCount) {
      chain.Add(cluster);
      var next = ReadFatEntry(image, l, cluster);
      if (next >= 0xFFFFFFF8) break;
      cluster = next;
    }
    return chain;
  }

  // ── Allocation-bitmap helpers ────────────────────────────────────────

  private readonly record struct BitmapInfo(uint FirstCluster, long Length, int Offset);

  private static BitmapInfo FindBitmap(Stream image, Layout l) {
    // Bitmap is one of the first three special root-dir entries (Volume/Bitmap/UpCase
    // in any order). Scan the whole root chain conservatively.
    var rootChain = WalkChain(image, l, l.RootDirCluster);
    var entryBuf = new byte[32];
    foreach (var cluster in rootChain) {
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      for (var off = 0; off < l.ClusterSize; off += 32) {
        image.Position = clusterAbsOff + off;
        image.ReadExactly(entryBuf);
        var t = entryBuf[0];
        if (t == 0x00) return new BitmapInfo(0, 0, -1); // end of dir
        if (t != 0x81) continue;
        var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf.AsSpan(20));
        var length = BinaryPrimitives.ReadInt64LittleEndian(entryBuf.AsSpan(24));
        return new BitmapInfo(firstCluster, length, l.ClusterHeapOffset + (int)(firstCluster - 2) * l.ClusterSize);
      }
    }
    return new BitmapInfo(0, 0, -1);
  }

  private static bool BitmapBitIsSet(Stream image, BitmapInfo bmp, uint cluster) {
    var bitIndex = (int)(cluster - 2);
    var byteIdx = bmp.Offset + bitIndex / 8;
    image.Position = byteIdx;
    var b = image.ReadByte();
    return (b & (1 << (bitIndex % 8))) != 0;
  }

  private static void SetBitmapBit(Stream image, BitmapInfo bmp, uint cluster, bool set) {
    var bitIndex = (int)(cluster - 2);
    var byteIdx = bmp.Offset + bitIndex / 8;
    image.Position = byteIdx;
    var b = (byte)image.ReadByte();
    var mask = (byte)(1 << (bitIndex % 8));
    var updated = set ? (byte)(b | mask) : (byte)(b & ~mask);
    if (updated == b) return;
    image.Position = byteIdx;
    image.WriteByte(updated);
  }

  // ── Cluster allocation ───────────────────────────────────────────────

  private static List<uint> AllocateClusters(Stream image, Layout l, BitmapInfo bmp, int count) {
    var allocated = new List<uint>(count);
    if (count == 0) return allocated;
    // Linear scan from cluster 2 — simple but bounded by clusterCount, fine for
    // small images where the touched-bytes target is dominant.
    uint c = 2;
    var bmpBuf = new byte[Math.Max(1, ((int)l.ClusterCount + 7) / 8)];
    image.Position = bmp.Offset;
    image.ReadExactly(bmpBuf.AsSpan(0, Math.Min(bmpBuf.Length, (int)bmp.Length)));
    while (allocated.Count < count && c < l.ClusterCount + 2) {
      var bitIndex = (int)(c - 2);
      var byteIdx = bitIndex / 8;
      if (byteIdx >= bmpBuf.Length) break;
      if ((bmpBuf[byteIdx] & (1 << (bitIndex % 8))) == 0) {
        allocated.Add(c);
        bmpBuf[byteIdx] |= (byte)(1 << (bitIndex % 8)); // claim locally so we don't pick again
      }
      c++;
    }
    if (allocated.Count < count)
      throw new IOException($"exFAT: not enough free clusters (needed {count}, got {allocated.Count}).");
    // Persist bitmap bits + FAT chain.
    foreach (var cluster in allocated)
      SetBitmapBit(image, bmp, cluster, true);
    for (var i = 0; i < allocated.Count; i++) {
      var next = i + 1 < allocated.Count ? allocated[i + 1] : EocMarker;
      WriteFatEntry(image, l, allocated[i], next);
    }
    return allocated;
  }

  // ── Directory walking ────────────────────────────────────────────────

  /// <summary>
  /// Finds where to place a new entry set of <paramref name="entriesNeeded"/> 32-byte
  /// slots in the root directory, returning the absolute file offset of the first slot.
  /// <para>
  /// exFAT directory layout rules this must respect:
  /// <list type="bullet">
  ///   <item>The first slot whose type byte is <c>0x00</c> is the <em>end-of-directory</em>
  ///   marker; no in-use entry may follow it. So an entry set must never be placed in a
  ///   way that leaves a <c>0x00</c> gap ahead of it.</item>
  ///   <item>An entry set must lie wholly inside one cluster — a directory's clusters are
  ///   not necessarily physically adjacent on disk, so a set may not straddle a boundary.</item>
  /// </list>
  /// Placement strategy: first try to reuse a run of <em>deleted</em> slots (type byte with
  /// bit 7 cleared but not <c>0x00</c>) that fits inside a single cluster — those are not
  /// end-markers, so reuse is safe. Otherwise append at the end-of-directory point. If the
  /// set does not fit in the remaining slots of the cluster holding that point, allocate a
  /// fresh cluster, link it onto the FAT chain, place the set at its start, and turn the
  /// old end-marker into the chain continuation (the old trailing <c>0x00</c> slots become
  /// the directory tail of an earlier cluster, which is legal only when no in-use entry
  /// follows them in that cluster — guaranteed here because we append, never inserting a
  /// gap before live entries).
  /// </para>
  /// </summary>
  private static long FindFreeRootDirSlots(Stream image, Layout l, BitmapInfo bmp, int entriesNeeded) {
    var slotBuf = new byte[32];
    var rootChain = WalkChain(image, l, l.RootDirCluster);
    var slotsPerCluster = l.ClusterSize / 32;

    // Pass 1: reuse a run of deleted (bit-7-cleared, non-zero) slots inside one cluster.
    foreach (var cluster in rootChain) {
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      long? runStart = null;
      var runCount = 0;
      for (var slot = 0; slot < slotsPerCluster; slot++) {
        var abs = clusterAbsOff + (long)slot * 32;
        image.Position = abs;
        image.ReadExactly(slotBuf);
        var t = slotBuf[0];
        if (t == 0x00) break; // end-of-directory within this cluster — stop reuse scan here
        var isDeleted = (t & 0x80) == 0;
        if (isDeleted) {
          runStart ??= abs;
          runCount++;
          if (runCount >= entriesNeeded) return runStart.Value;
        } else {
          runStart = null;
          runCount = 0;
        }
      }
    }

    // Pass 2: append at the end-of-directory point. Locate the cluster + slot holding the
    // first 0x00 marker (or the implicit end past the last fully-used cluster).
    for (var ci = 0; ci < rootChain.Count; ci++) {
      var cluster = rootChain[ci];
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      for (var slot = 0; slot < slotsPerCluster; slot++) {
        var abs = clusterAbsOff + (long)slot * 32;
        image.Position = abs;
        image.ReadExactly(slotBuf);
        if (slotBuf[0] != 0x00) continue;
        // Found end-of-directory at (ci, slot). Does the set fit in the rest of this cluster?
        if (slotsPerCluster - slot >= entriesNeeded)
          return abs;
        // Doesn't fit in this cluster's tail. A non-last directory cluster must not carry
        // a 0x00 end-marker (fsck stops there and treats everything in later clusters as
        // orphaned), so fill the [slot, clusterEnd) gap with benign "unused" markers
        // (type byte 0x05 = bit 7 cleared, non-zero → readers skip, not an end-marker),
        // then place the set at the start of a fresh appended cluster.
        FillUnusedSlots(image, abs, slotsPerCluster - slot);
        return ExtendRootDir(image, l, bmp, rootChain);
      }
    }

    // No 0x00 found anywhere — every cluster is packed full. Extend.
    return ExtendRootDir(image, l, bmp, rootChain);
  }

  /// <summary>
  /// Writes <paramref name="count"/> "unused" 32-byte directory slots starting at
  /// <paramref name="absOffset"/>. Each slot's type byte is set to <c>0x05</c> — bit 7
  /// (InUse) cleared and a non-zero type code, so an exFAT reader skips it without
  /// treating it as the <c>0x00</c> end-of-directory marker. Used to pad a non-last
  /// directory cluster's tail when an entry set is pushed to the next cluster.
  /// </summary>
  private static void FillUnusedSlots(Stream image, long absOffset, int count) {
    var pad = new byte[count * 32];
    for (var i = 0; i < count; i++)
      pad[i * 32] = 0x05;
    image.Position = absOffset;
    image.Write(pad);
  }

  /// <summary>
  /// Allocates one cluster, links it onto the end of the directory chain, zero-fills it,
  /// and returns its start offset. The fresh cluster's whole span (≤ 4 KB by typical
  /// geometry, but always ≥ one entry set) holds any single entry set (max 18 × 32 B).
  /// </summary>
  private static long ExtendRootDir(Stream image, Layout l, BitmapInfo bmp, List<uint> rootChain) {
    var newClusters = AllocateClusters(image, l, bmp, 1);
    var newCluster = newClusters[0];
    var lastRoot = rootChain[^1];
    WriteFatEntry(image, l, lastRoot, newCluster);
    WriteFatEntry(image, l, newCluster, EocMarker);
    var zero = new byte[l.ClusterSize];
    image.Position = l.ClusterHeapOffset + (long)(newCluster - 2) * l.ClusterSize;
    image.Write(zero);
    return l.ClusterHeapOffset + (long)(newCluster - 2) * l.ClusterSize;
  }

  // ── Public API ───────────────────────────────────────────────────────

  /// <summary>Adds a file with O(touched bytes) I/O.</summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length is 0 or > 255)
      throw new ArgumentException("exFAT names must be 1..255 chars.", nameof(name));

    var l = ReadLayout(image);
    var bmp = FindBitmap(image, l);
    if (bmp.Offset < 0) throw new InvalidDataException("exFAT: allocation bitmap not found.");

    // Allocate file data clusters.
    var clustersNeeded = data.Length == 0 ? 0 : (data.Length + l.ClusterSize - 1) / l.ClusterSize;
    var fileClusters = AllocateClusters(image, l, bmp, clustersNeeded);
    if (clustersNeeded > 0) {
      // Write data into cluster heap.
      for (var i = 0; i < fileClusters.Count; i++) {
        var dst = l.ClusterHeapOffset + (long)(fileClusters[i] - 2) * l.ClusterSize;
        var srcStart = i * l.ClusterSize;
        var srcLen = Math.Min(l.ClusterSize, data.Length - srcStart);
        image.Position = dst;
        image.Write(data.AsSpan(srcStart, srcLen));
        if (srcLen < l.ClusterSize) {
          // Zero-pad cluster tail so leftover bytes can't leak.
          var tail = new byte[l.ClusterSize - srcLen];
          image.Write(tail);
        }
      }
    }

    // Build entry set.
    var nameChars = name.ToCharArray();
    var nameEntries = (nameChars.Length + 14) / 15;
    var secondaryCount = 1 + nameEntries;
    var totalEntries = 1 + secondaryCount;
    var setBytes = totalEntries * 32;

    var setStart = FindFreeRootDirSlots(image, l, bmp, totalEntries);

    var set = new byte[setBytes];
    var firstCluster = clustersNeeded > 0 ? fileClusters[0] : 0u;
    var nowStamp = BuildExFatTimestamp(DateTime.UtcNow);

    // 0x85 File entry
    set[0] = 0x85;
    set[1] = (byte)secondaryCount;
    // 2..3 = SetChecksum (filled at end)
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(4), 0x0020); // archive
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(8), nowStamp);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(12), nowStamp);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(16), nowStamp);

    // 0xC0 Stream Extension
    var streamOff = 32;
    set[streamOff] = 0xC0;
    set[streamOff + 1] = 0x01; // AllocationPossible; NoFatChain=0 → use FAT chain
    set[streamOff + 3] = (byte)nameChars.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(streamOff + 4), ComputeNameHash(name));
    BinaryPrimitives.WriteInt64LittleEndian(set.AsSpan(streamOff + 8), data.Length);  // ValidDataLength
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(streamOff + 20), firstCluster);
    BinaryPrimitives.WriteInt64LittleEndian(set.AsSpan(streamOff + 24), data.Length); // DataLength

    // N × 0xC1 File Name entries
    for (var n = 0; n < nameEntries; n++) {
      var off = 64 + n * 32;
      set[off] = 0xC1;
      set[off + 1] = 0;
      var startChar = n * 15;
      var charsToWrite = Math.Min(15, nameChars.Length - startChar);
      for (var c = 0; c < charsToWrite; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(off + 2 + c * 2), nameChars[startChar + c]);
    }

    // Compute and stamp set checksum.
    var checksum = EntrySetChecksum(set);
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(2), checksum);

    // Write entry set.
    image.Position = setStart;
    image.Write(set);

    // Update PercentInUse on primary + backup VBR.
    UpdatePercentInUse(image, l, bmp);
  }

  /// <summary>Removes a named file with O(touched bytes) I/O. Returns false if not found.</summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var l = ReadLayout(image);
    var bmp = FindBitmap(image, l);
    if (bmp.Offset < 0) throw new InvalidDataException("exFAT: allocation bitmap not found.");

    // Find file's entry set.
    var found = LocateFileEntry(image, l, name);
    if (found is null) return false;
    var (entrySetOffset, setBytes, firstCluster) = found.Value;

    // Walk and free cluster chain.
    if (firstCluster >= 2) {
      var chain = WalkChain(image, l, firstCluster);
      foreach (var cluster in chain) {
        if (wipeData) {
          var dst = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
          var zero = new byte[l.ClusterSize];
          image.Position = dst;
          image.Write(zero);
        }
        WriteFatEntry(image, l, cluster, 0);
        SetBitmapBit(image, bmp, cluster, false);
      }
    }

    // Wipe entry set: clear bit 7 of each EntryType byte (in-use → unused), zero rest.
    var wipeBuf = new byte[setBytes];
    image.Position = entrySetOffset;
    image.ReadExactly(wipeBuf);
    for (var off = 0; off < setBytes; off += 32) {
      wipeBuf[off] = (byte)(wipeBuf[off] & 0x7F);
      wipeBuf.AsSpan(off + 1, 31).Clear();
    }
    image.Position = entrySetOffset;
    image.Write(wipeBuf);

    UpdatePercentInUse(image, l, bmp);
    return true;
  }

  /// <summary>
  /// Locates a file's entry set in the root directory by name (case-insensitive).
  /// Returns absolute offset, total set size in bytes, and first data cluster.
  /// </summary>
  private static (long EntrySetOffset, int SetBytes, uint FirstCluster)? LocateFileEntry(Stream image, Layout l, string name) {
    var slot = new byte[32];
    var rootChain = WalkChain(image, l, l.RootDirCluster);
    foreach (var cluster in rootChain) {
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      for (var off = 0; off < l.ClusterSize; off += 32) {
        var abs = clusterAbsOff + off;
        image.Position = abs;
        image.ReadExactly(slot);
        var type = slot[0];
        if (type == 0x00) return null; // end of directory
        if (type != 0x85) continue;
        var secondaryCount = slot[1];
        var setBytes = 32 * (1 + secondaryCount);

        // Read whole entry set (still within touch budget — small).
        var set = new byte[setBytes];
        image.Position = abs;
        image.ReadExactly(set);

        if (set[32] != 0xC0) continue; // malformed — skip
        var nameLength = set[32 + 3];
        var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(32 + 20));

        // Reconstruct file name from 0xC1 entries.
        var sb = new StringBuilder();
        var nameEntries = (nameLength + 14) / 15;
        for (var n = 0; n < nameEntries; n++) {
          var nameOff = 64 + n * 32;
          if (nameOff + 32 > set.Length) break;
          if (set[nameOff] != 0xC1) break;
          var charsToRead = Math.Min(15, nameLength - n * 15);
          for (var c = 0; c < charsToRead; c++) {
            var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(set.AsSpan(nameOff + 2 + c * 2));
            if (ch == 0) break;
            sb.Append(ch);
          }
        }

        if (string.Equals(sb.ToString(), name, StringComparison.OrdinalIgnoreCase))
          return (abs, setBytes, firstCluster);
      }
    }
    return null;
  }

  // ── PercentInUse update ──────────────────────────────────────────────

  private static void UpdatePercentInUse(Stream image, Layout l, BitmapInfo bmp) {
    if (l.ClusterCount == 0) return;
    // Count set bits in bitmap.
    var bmpLen = (int)Math.Min(bmp.Length, ((long)l.ClusterCount + 7) / 8);
    var bmpBuf = new byte[bmpLen];
    image.Position = bmp.Offset;
    image.ReadExactly(bmpBuf);
    var used = 0u;
    foreach (var b in bmpBuf) used += (uint)System.Numerics.BitOperations.PopCount(b);
    var pct = (byte)Math.Min(100u, used * 100u / l.ClusterCount);
    image.Position = 112;
    image.WriteByte(pct);
    // Backup VBR at sector 12.
    var backupVbrPos = 12L * l.BytesPerSector;
    if (backupVbrPos + 113 > image.Length) return;
    image.Position = backupVbrPos + 3;
    Span<byte> sigBuf = stackalloc byte[8];
    image.ReadExactly(sigBuf);
    if (Encoding.ASCII.GetString(sigBuf) != "EXFAT   ") return;
    image.Position = backupVbrPos + 112;
    image.WriteByte(pct);
  }

  // ── Checksum + name-hash + timestamp (mirrors ExFatWriter) ───────────

  private static ushort EntrySetChecksum(ReadOnlySpan<byte> set) {
    ushort checksum = 0;
    for (var i = 0; i < set.Length; i++) {
      if (i == 2 || i == 3) continue;
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]) & 0xFFFF);
    }
    return checksum;
  }

  private static ushort ComputeNameHash(string name) {
    ushort hash = 0;
    foreach (var ch in name.ToUpperInvariant()) {
      hash = (ushort)(((hash << 15) | (hash >> 1)) + (ch & 0xFF));
      hash = (ushort)(((hash << 15) | (hash >> 1)) + (ch >> 8));
    }
    return hash;
  }

  private static uint BuildExFatTimestamp(DateTime dt) {
    uint year = dt.Year >= 1980 ? (uint)(dt.Year - 1980) : 0u;
    uint time = ((uint)dt.Hour << 11) | ((uint)dt.Minute << 5) | ((uint)(dt.Second / 2));
    uint date = (year << 9) | ((uint)dt.Month << 5) | (uint)dt.Day;
    return (date << 16) | time;
  }
}
