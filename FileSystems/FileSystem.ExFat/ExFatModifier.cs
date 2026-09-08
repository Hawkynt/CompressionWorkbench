#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// In-place exFAT modifier — true random-access I/O. Allocation changes update
/// FAT entries, the allocation bitmap, directory entry sets and PercentInUse.
/// Removal is cross-link aware: a cluster is zeroed/freed only when no other live
/// directory entry still references it, which keeps read-only shared-data aliases
/// produced by the optimizer valid until their last name is removed.
/// </summary>
public static class ExFatModifier {
  private const uint EocMarker = 0xFFFFFFFFu;

  private readonly record struct Layout(
    int BytesPerSector,
    int SectorsPerCluster,
    int ClusterSize,
    int FatOffset,
    int ClusterHeapOffset,
    uint ClusterCount,
    uint RootDirCluster);

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

  private static List<uint> ResolveAllocation(
      Stream image, Layout l, uint firstCluster, byte generalSecondaryFlags, long dataLength) {
    if (firstCluster < 2 || dataLength <= 0) return [];
    // exFAT §7.7.3.1: NoFatChain means one contiguous allocation extent whose
    // length comes from DataLength; otherwise the FAT describes the chain.
    if ((generalSecondaryFlags & 0x02) == 0)
      return WalkChain(image, l, firstCluster);
    var count = checked((int)((dataLength + l.ClusterSize - 1) / l.ClusterSize));
    var result = new List<uint>(count);
    for (var i = 0; i < count; ++i) {
      var cluster = firstCluster + (uint)i;
      if (cluster > l.ClusterCount + 1) break;
      result.Add(cluster);
    }
    return result;
  }

  private readonly record struct BitmapInfo(uint FirstCluster, long Length, int Offset);

  private static BitmapInfo FindBitmap(Stream image, Layout l) {
    var rootChain = WalkChain(image, l, l.RootDirCluster);
    var entryBuf = new byte[32];
    foreach (var cluster in rootChain) {
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      for (var off = 0; off < l.ClusterSize; off += 32) {
        image.Position = clusterAbsOff + off;
        image.ReadExactly(entryBuf);
        var t = entryBuf[0];
        if (t == 0x00) return new BitmapInfo(0, 0, -1);
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

  private static List<uint> AllocateClusters(Stream image, Layout l, BitmapInfo bmp, int count) {
    var allocated = new List<uint>(count);
    if (count == 0) return allocated;
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
        bmpBuf[byteIdx] |= (byte)(1 << (bitIndex % 8));
      }
      c++;
    }
    if (allocated.Count < count)
      throw new IOException($"exFAT: not enough free clusters (needed {count}, got {allocated.Count}).");
    foreach (var cluster in allocated)
      SetBitmapBit(image, bmp, cluster, true);
    for (var i = 0; i < allocated.Count; i++) {
      var next = i + 1 < allocated.Count ? allocated[i + 1] : EocMarker;
      WriteFatEntry(image, l, allocated[i], next);
    }
    return allocated;
  }

  private static long FindFreeRootDirSlots(Stream image, Layout l, BitmapInfo bmp, int entriesNeeded) {
    var slotBuf = new byte[32];
    var rootChain = WalkChain(image, l, l.RootDirCluster);
    var slotsPerCluster = l.ClusterSize / 32;

    foreach (var cluster in rootChain) {
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      long? runStart = null;
      var runCount = 0;
      for (var slot = 0; slot < slotsPerCluster; slot++) {
        var abs = clusterAbsOff + (long)slot * 32;
        image.Position = abs;
        image.ReadExactly(slotBuf);
        var t = slotBuf[0];
        if (t == 0x00) break;
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

    for (var ci = 0; ci < rootChain.Count; ci++) {
      var cluster = rootChain[ci];
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      for (var slot = 0; slot < slotsPerCluster; slot++) {
        var abs = clusterAbsOff + (long)slot * 32;
        image.Position = abs;
        image.ReadExactly(slotBuf);
        if (slotBuf[0] != 0x00) continue;
        if (slotsPerCluster - slot >= entriesNeeded)
          return abs;
        FillUnusedSlots(image, abs, slotsPerCluster - slot);
        return ExtendRootDir(image, l, bmp, rootChain);
      }
    }
    return ExtendRootDir(image, l, bmp, rootChain);
  }

  private static void FillUnusedSlots(Stream image, long absOffset, int count) {
    var pad = new byte[count * 32];
    for (var i = 0; i < count; i++)
      pad[i * 32] = 0x05;
    image.Position = absOffset;
    image.Write(pad);
  }

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

    var clustersNeeded = data.Length == 0 ? 0 : (data.Length + l.ClusterSize - 1) / l.ClusterSize;
    var fileClusters = AllocateClusters(image, l, bmp, clustersNeeded);
    if (clustersNeeded > 0) {
      for (var i = 0; i < fileClusters.Count; i++) {
        var dst = l.ClusterHeapOffset + (long)(fileClusters[i] - 2) * l.ClusterSize;
        var srcStart = i * l.ClusterSize;
        var srcLen = Math.Min(l.ClusterSize, data.Length - srcStart);
        image.Position = dst;
        image.Write(data.AsSpan(srcStart, srcLen));
        if (srcLen < l.ClusterSize)
          image.Write(new byte[l.ClusterSize - srcLen]);
      }
    }

    var nameChars = name.ToCharArray();
    var nameEntries = (nameChars.Length + 14) / 15;
    var secondaryCount = 1 + nameEntries;
    var totalEntries = 1 + secondaryCount;
    var setBytes = totalEntries * 32;
    var setStart = FindFreeRootDirSlots(image, l, bmp, totalEntries);
    var set = new byte[setBytes];
    var firstCluster = clustersNeeded > 0 ? fileClusters[0] : 0u;
    var nowStamp = BuildExFatTimestamp(DateTime.UtcNow);

    set[0] = 0x85;
    set[1] = (byte)secondaryCount;
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(4), 0x0020);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(8), nowStamp);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(12), nowStamp);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(16), nowStamp);

    const int streamOff = 32;
    set[streamOff] = 0xC0;
    set[streamOff + 1] = firstCluster == 0 ? (byte)0 : (byte)0x01;
    set[streamOff + 3] = (byte)nameChars.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(streamOff + 4), ComputeNameHash(name));
    BinaryPrimitives.WriteInt64LittleEndian(set.AsSpan(streamOff + 8), data.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(streamOff + 20), firstCluster);
    BinaryPrimitives.WriteInt64LittleEndian(set.AsSpan(streamOff + 24), data.Length);

    for (var n = 0; n < nameEntries; n++) {
      var off = 64 + n * 32;
      set[off] = 0xC1;
      set[off + 1] = 0;
      var startChar = n * 15;
      var charsToWrite = Math.Min(15, nameChars.Length - startChar);
      for (var c = 0; c < charsToWrite; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(off + 2 + c * 2), nameChars[startChar + c]);
    }

    BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(2), EntrySetChecksum(set));
    image.Position = setStart;
    image.Write(set);
    UpdatePercentInUse(image, l, bmp);
  }

  /// <summary>
  /// Removes a named root-directory file. Clusters still reachable from any other
  /// live exFAT directory entry are retained and, when requested, are not wiped.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var l = ReadLayout(image);
    var bmp = FindBitmap(image, l);
    if (bmp.Offset < 0) throw new InvalidDataException("exFAT: allocation bitmap not found.");

    var found = LocateFileEntry(image, l, name);
    if (found is null) return false;
    var (entrySetOffset, setBytes, firstCluster, streamFlags, dataLength) = found.Value;

    var chain = ResolveAllocation(image, l, firstCluster, streamFlags, dataLength);
    var referencedElsewhere = CollectReferencedClusters(image, l, entrySetOffset);
    foreach (var cluster in chain.Where(c => !referencedElsewhere.Contains(c))) {
      if (wipeData) {
        var dst = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
        image.Position = dst;
        image.Write(new byte[l.ClusterSize]);
      }
      WriteFatEntry(image, l, cluster, 0);
      SetBitmapBit(image, bmp, cluster, false);
    }

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

  private static (long EntrySetOffset, int SetBytes, uint FirstCluster, byte StreamFlags, long DataLength)? LocateFileEntry(
      Stream image, Layout l, string name) {
    var slot = new byte[32];
    var rootChain = WalkChain(image, l, l.RootDirCluster);
    foreach (var cluster in rootChain) {
      var clusterAbsOff = l.ClusterHeapOffset + (long)(cluster - 2) * l.ClusterSize;
      for (var off = 0; off < l.ClusterSize; off += 32) {
        var abs = clusterAbsOff + off;
        image.Position = abs;
        image.ReadExactly(slot);
        var type = slot[0];
        if (type == 0x00) return null;
        if (type != 0x85) continue;
        var secondaryCount = slot[1];
        var setBytes = 32 * (1 + secondaryCount);
        var set = new byte[setBytes];
        image.Position = abs;
        image.ReadExactly(set);
        if (set.Length < 64 || set[32] != 0xC0) continue;
        var nameLength = set[35];
        var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(52));
        var streamFlags = set[33];
        var dataLength = BinaryPrimitives.ReadInt64LittleEndian(set.AsSpan(56));
        if (string.Equals(DecodeName(set, nameLength), name, StringComparison.OrdinalIgnoreCase))
          return (abs, setBytes, firstCluster, streamFlags, dataLength);
      }
    }
    return null;
  }

  private static string DecodeName(ReadOnlySpan<byte> set, int nameLength) {
    var sb = new StringBuilder(nameLength);
    var nameEntries = (nameLength + 14) / 15;
    for (var n = 0; n < nameEntries; n++) {
      var nameOff = 64 + n * 32;
      if (nameOff + 32 > set.Length || set[nameOff] != 0xC1) break;
      var charsToRead = Math.Min(15, nameLength - n * 15);
      for (var c = 0; c < charsToRead; c++) {
        var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(set[(nameOff + 2 + c * 2)..]);
        if (ch == 0) break;
        sb.Append(ch);
      }
    }
    return sb.ToString();
  }

  /// <summary>
  /// Returns every cluster reachable from any live file/directory entry except the
  /// entry set currently being removed. The recursion also sees nested aliases, so
  /// deleting a root alias cannot free storage still referenced below a directory.
  /// </summary>
  private static HashSet<uint> CollectReferencedClusters(Stream image, Layout l, long excludedEntrySetOffset) {
    var referenced = new HashSet<uint>();
    var visitedDirectories = new HashSet<uint>();
    foreach (var cluster in WalkChain(image, l, l.RootDirCluster)) referenced.Add(cluster);
    ScanDirectoryReferences(image, l, l.RootDirCluster, 0x01, l.ClusterSize,
      excludedEntrySetOffset, referenced, visitedDirectories);
    return referenced;
  }

  private static void ScanDirectoryReferences(
      Stream image,
      Layout l,
      uint firstCluster,
      byte streamFlags,
      long dataLength,
      long excludedEntrySetOffset,
      HashSet<uint> referenced,
      HashSet<uint> visitedDirectories) {
    if (firstCluster < 2 || !visitedDirectories.Add(firstCluster)) return;
    var directoryClusters = ResolveAllocation(image, l, firstCluster, streamFlags, Math.Max(dataLength, l.ClusterSize));
    foreach (var directoryCluster in directoryClusters) {
      var clusterAbsOff = l.ClusterHeapOffset + (long)(directoryCluster - 2) * l.ClusterSize;
      for (var off = 0; off < l.ClusterSize; off += 32) {
        var abs = clusterAbsOff + off;
        Span<byte> primary = stackalloc byte[32];
        image.Position = abs;
        image.ReadExactly(primary);
        if (primary[0] == 0x00) return;
        if (primary[0] != 0x85) continue;
        var secondaryCount = primary[1];
        var setBytes = 32 * (1 + secondaryCount);
        var set = new byte[setBytes];
        image.Position = abs;
        image.ReadExactly(set);
        if (set.Length < 64 || set[32] != 0xC0) continue;

        var attributes = BinaryPrimitives.ReadUInt16LittleEndian(set.AsSpan(4));
        var childFlags = set[33];
        var childFirst = BinaryPrimitives.ReadUInt32LittleEndian(set.AsSpan(52));
        var childLength = BinaryPrimitives.ReadInt64LittleEndian(set.AsSpan(56));
        var childClusters = ResolveAllocation(image, l, childFirst, childFlags, childLength);
        if (abs != excludedEntrySetOffset)
          foreach (var cluster in childClusters) referenced.Add(cluster);

        if ((attributes & 0x0010) != 0 && childFirst >= 2)
          ScanDirectoryReferences(image, l, childFirst, childFlags, childLength,
            excludedEntrySetOffset, referenced, visitedDirectories);
        off += secondaryCount * 32;
      }
    }
  }

  private static void UpdatePercentInUse(Stream image, Layout l, BitmapInfo bmp) {
    if (l.ClusterCount == 0) return;
    var bmpLen = (int)Math.Min(bmp.Length, ((long)l.ClusterCount + 7) / 8);
    var bmpBuf = new byte[bmpLen];
    image.Position = bmp.Offset;
    image.ReadExactly(bmpBuf);
    var used = 0u;
    foreach (var b in bmpBuf) used += (uint)System.Numerics.BitOperations.PopCount(b);
    var pct = (byte)Math.Min(100u, used * 100u / l.ClusterCount);
    image.Position = 112;
    image.WriteByte(pct);
    var backupVbrPos = 12L * l.BytesPerSector;
    if (backupVbrPos + 113 > image.Length) return;
    image.Position = backupVbrPos + 3;
    Span<byte> sigBuf = stackalloc byte[8];
    image.ReadExactly(sigBuf);
    if (Encoding.ASCII.GetString(sigBuf) != "EXFAT   ") return;
    image.Position = backupVbrPos + 112;
    image.WriteByte(pct);
  }

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
