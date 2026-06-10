#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// True in-place modifier for Microsoft DoubleSpace / DriveSpace CVF images.
/// Mutates the MDBPB-driven volume structure directly: BitFAT bits flip,
/// MDFAT cluster-allocation entries are written in place, inner FAT chains
/// are extended/zeroed at the cluster slot, VFAT root dirents are added/
/// scratched in place, and physical compressed runs are placed in the
/// DATA region without rewriting any unrelated bytes.
///
/// <para>Unlike the <see cref="ModifyRebuilder"/> path, this modifier never
/// rebuilds the image. Bytes outside the touched cluster slot, MDFAT entry,
/// BitFAT byte, FAT chain entry, dirent record and freshly-allocated
/// physical run are guaranteed byte-identical.</para>
///
/// <para><b>Scope.</b> Add, Remove, and Replace target the inner root
/// directory only. Subdirectory mutation is not supported (legacy CVF
/// images created by DBLSPACE/DRVSPACE never used subdirs as a normal
/// authoring pattern). The variant (DoubleSpace 6.0 / DriveSpace 6.22 /
/// DriveSpace 3.0) is auto-detected from the OEM signature so the same
/// modifier services both descriptors.</para>
/// </summary>
public static class DoubleSpaceInPlaceModifier {

  private const int BitFatRegionBytes = 8192;
  private const int DirEntryBytes = 32;
  private const int LfnAttribute = 0x0F;
  private const int FatEoc = 0xFFFF;

  /// <summary>
  /// Adds (or replaces) files in-place. For each input: walks the inner FAT
  /// to find <c>count</c> free logical clusters, allocates a contiguous
  /// physical run at the end of the in-use DATA region, compresses each
  /// cluster, writes MDFAT + BitFAT + inner FAT entries in place, and
  /// writes the VFAT dirent (+ LFN chain) into the first free dirent slots
  /// of the root directory. If an input name matches an existing entry, the
  /// old entry's clusters are freed first (so the slot can be re-used and
  /// the old physical run is wiped).
  /// </summary>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);

    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var disk = ms.ToArray();

    var ctx = ParseContext(disk);

    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs)) {
      // Replace semantics — free the existing entry first so the new content
      // takes the slot (and any cluster reuse is honest).
      RemoveByName(disk, ctx, name);
      AddOne(disk, ctx, name, data);
    }

    image.Position = 0;
    image.Write(disk, 0, disk.Length);
    image.SetLength(disk.Length);
    image.Flush();
  }

  /// <summary>
  /// Removes the named entries in place. For each entry: walks the inner FAT
  /// chain, zeros each physical compressed run, clears BitFAT bits for the
  /// freed sectors, zeros the MDFAT entries, zeros the inner FAT chain, and
  /// scratches the dirent + LFN chain by writing 0xE5 into byte 0 of each
  /// dirent. Bytes outside those allocation-table slots and the freed
  /// physical runs are guaranteed byte-identical to the source image.
  /// </summary>
  public static void Remove(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);

    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var disk = ms.ToArray();

    var ctx = ParseContext(disk);

    foreach (var name in entryNames)
      RemoveByName(disk, ctx, name);

    image.Position = 0;
    image.Write(disk, 0, disk.Length);
    image.SetLength(disk.Length);
    image.Flush();
  }

  // =========================================================================
  //                              Context
  // =========================================================================

  private sealed class Context {
    public int BytesPerSector;
    public int SectorsPerCluster;
    public int ReservedSectors;
    public int FatCount;
    public int RootEntryCount;
    public int FatSize;
    public int InnerTotalClusters;
    public int MdfatStartSector;
    public int MdfatLenSectors;
    public int BitFatStartSector;
    public int BitFatLenSectors;
    public int DataStartSector;
    public int DataLenSectors;
    public int InnerFatOffset;
    public int InnerFat2Offset;
    public int RootDirOffset;
    public int RootDirSectors;
    public int InnerDataOffset;
    public int FirstDataSector;
    public CvfVariant Variant;
  }

  private static Context ParseContext(byte[] disk) {
    if (disk.Length < 512)
      throw new InvalidDataException("CVF image too small for MDBPB.");

    var oem = Encoding.ASCII.GetString(disk, 3, 8);
    var oem7 = Encoding.ASCII.GetString(disk, 3, 7);

    var variant = oem switch {
      "MSDSP6.0" => CvfVariant.DoubleSpace60,
      "MSDSP6.2" => CvfVariant.DriveSpace62,
      "DRVSPACE" => CvfVariant.DriveSpace30,
      _ when oem7 == "MS_DSP3" => CvfVariant.DriveSpace3,
      _ => throw new InvalidDataException($"Unknown CVF OEM signature '{oem}'."),
    };

    var ctx = new Context { Variant = variant };
    ctx.BytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    if (ctx.BytesPerSector is 0 or > 4096) ctx.BytesPerSector = 512;
    ctx.SectorsPerCluster = disk[13] == 0 ? 1 : disk[13];
    ctx.ReservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(14));
    if (ctx.ReservedSectors == 0) ctx.ReservedSectors = 1;
    ctx.FatCount = disk[16] == 0 ? 2 : disk[16];
    ctx.RootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(17));
    ctx.FatSize = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(22));

    ctx.MdfatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(44));
    ctx.MdfatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(48));
    ctx.BitFatStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(52));
    ctx.BitFatLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(56));
    ctx.DataStartSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(60));
    ctx.DataLenSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(64));
    ctx.InnerTotalClusters = (int)BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(72));

    ctx.InnerFatOffset = ctx.ReservedSectors * ctx.BytesPerSector;
    ctx.InnerFat2Offset = ctx.InnerFatOffset + ctx.FatSize * ctx.BytesPerSector;
    ctx.RootDirSectors = (ctx.RootEntryCount * DirEntryBytes + ctx.BytesPerSector - 1) / ctx.BytesPerSector;
    ctx.RootDirOffset = (ctx.ReservedSectors + ctx.FatCount * ctx.FatSize) * ctx.BytesPerSector;
    ctx.FirstDataSector = ctx.ReservedSectors + ctx.FatCount * ctx.FatSize + ctx.RootDirSectors;
    ctx.InnerDataOffset = ctx.FirstDataSector * ctx.BytesPerSector;

    return ctx;
  }

  // =========================================================================
  //                              Add
  // =========================================================================

  private static void AddOne(byte[] disk, Context ctx, string name, byte[] data) {
    var clusterBytes = ctx.SectorsPerCluster * ctx.BytesPerSector;
    var clustersNeeded = Math.Max(1, (data.Length + clusterBytes - 1) / clusterBytes);

    // 1. Find free logical clusters in the inner FAT.
    var logicalClusters = AllocateLogicalClusters(disk, ctx, clustersNeeded);
    if (logicalClusters == null)
      throw new IOException($"CVF inner volume full: cannot allocate {clustersNeeded} clusters.");

    // 2. Find the lowest free physical sector at the tail of the in-use
    //    DATA region. For each cluster we may emit up to 9 physical sectors
    //    (2-byte header + 4096 cluster bytes = 4098 B → 9 sectors).
    var nextPhysSector = FindFirstFreePhysicalSector(disk, ctx);

    // 3. For each cluster: compress, write physical run, set MDFAT + BitFAT,
    //    write inner FAT16 entry pointing at next cluster (or EoC for last).
    for (var i = 0; i < clustersNeeded; i++) {
      var logicalCluster = logicalClusters[i];
      var offsetInFile = i * clusterBytes;
      var remaining = data.Length - offsetInFile;
      var chunkLen = Math.Min(clusterBytes, remaining);

      var validChunk = Math.Max(1, chunkLen);
      var rawSpan = chunkLen <= 0 ? ReadOnlySpan<byte>.Empty : data.AsSpan(offsetInFile, chunkLen);

      // Mirror the cluster to the inner-data area for host-tool fallback.
      var innerClusterOffset = ctx.InnerDataOffset + (logicalCluster - 2) * clusterBytes;
      if (innerClusterOffset + clusterBytes <= disk.Length) {
        Array.Clear(disk, innerClusterOffset, clusterBytes);
        if (chunkLen > 0)
          rawSpan.CopyTo(disk.AsSpan(innerClusterOffset));
      }

      byte[] block;
      uint flagsNibble;
      if (validChunk >= 32 && chunkLen > 0) {
        block = ctx.Variant switch {
          CvfVariant.DriveSpace3 => DsCompression.CompressMsLzh(rawSpan),
          CvfVariant.DoubleSpace60 => DsCompression.Compress(rawSpan),
          _ => DsCompression.CompressDriveSpace(rawSpan),
        };
        var headerWord = (ushort)(block[0] | (block[1] << 8));
        flagsNibble = (headerWord & 0x8000) != 0 ? 0x2u : 0x1u;
      } else {
        block = WrapStoredRun(rawSpan);
        flagsNibble = 0x1u;
      }

      var runSectors = Math.Max(1, (block.Length + ctx.BytesPerSector - 1) / ctx.BytesPerSector);

      // Find a contiguous free physical-sector run of size runSectors starting
      // from nextPhysSector. The BitFAT region granularity (8 KB = 16 sectors)
      // means our run is always within a single region — we extend nextPhys
      // past every found-used sector and re-check.
      var physStart = FindFreePhysicalRun(disk, ctx, runSectors, nextPhysSector);
      if (physStart < 0)
        throw new IOException("CVF DATA region full: cannot place new compressed run.");

      // Write run bytes at the physical position.
      var physByteOffset = (ctx.DataStartSector + physStart) * ctx.BytesPerSector;
      if (physByteOffset + runSectors * ctx.BytesPerSector > disk.Length)
        throw new IOException("CVF physical run would extend past image boundary.");

      // Pad the run with zeros first so any tail bytes after block.Length are clean.
      Array.Clear(disk, physByteOffset, runSectors * ctx.BytesPerSector);
      block.CopyTo(disk, physByteOffset);

      // MDFAT entry.
      var mdfatEntry = ((uint)physStart & 0x1FFFFFu)
        | (((uint)runSectors & 0x7Fu) << 21)
        | (flagsNibble << 28);
      var mdfatEntryOffset = ctx.MdfatStartSector * ctx.BytesPerSector + logicalCluster * 4;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(mdfatEntryOffset), mdfatEntry);

      // BitFAT bits.
      SetBitFatBits(disk, ctx, physStart, runSectors, clear: false);

      // Inner FAT16 chain entry — link to next cluster, or EoC.
      var nextVal = i + 1 < clustersNeeded ? logicalClusters[i + 1] : FatEoc;
      WriteInnerFatEntry(disk, ctx, logicalCluster, nextVal);

      nextPhysSector = physStart + runSectors;
    }

    // 4. Insert the dirent (+ optional LFN chain) into the root directory.
    InsertRootDirent(disk, ctx, name, logicalClusters[0], data.Length);
  }

  // =========================================================================
  //                              Remove
  // =========================================================================

  private static bool RemoveByName(byte[] disk, Context ctx, string name) {
    var locator = LocateRootDirent(disk, ctx, name);
    if (locator == null) return false;

    // 1. Walk inner FAT chain from start cluster — for each cluster, zero
    //    the physical run, clear BitFAT bits, zero the MDFAT entry, zero
    //    the inner FAT entry.
    var cluster = locator.Value.StartCluster;
    var safety = 1_000_000;
    var freedPhysSectors = new List<(int Start, int Count)>();
    while (cluster >= 2 && cluster < ctx.InnerTotalClusters && safety-- > 0) {
      var mdfatEntryOffset = ctx.MdfatStartSector * ctx.BytesPerSector + cluster * 4;
      if (mdfatEntryOffset + 4 > disk.Length) break;

      var mdfatEntry = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(mdfatEntryOffset));
      var physSector = (int)(mdfatEntry & 0x1FFFFFu);
      var runSectors = (int)((mdfatEntry >> 21) & 0x7Fu);
      var flags = (int)((mdfatEntry >> 28) & 0xFu);

      if (flags is 1 or 2 && runSectors > 0) {
        // Zero physical run.
        var physByteOffset = (ctx.DataStartSector + physSector) * ctx.BytesPerSector;
        var physByteLen = runSectors * ctx.BytesPerSector;
        if (physByteOffset >= 0 && physByteOffset + physByteLen <= disk.Length)
          Array.Clear(disk, physByteOffset, physByteLen);

        freedPhysSectors.Add((physSector, runSectors));
      }

      // Zero MDFAT entry.
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(mdfatEntryOffset), 0);

      // Read next cluster from inner FAT before zeroing this slot.
      var nextCluster = ReadInnerFatEntry(disk, ctx, cluster);

      // Zero inner FAT16 entry (both FAT1 and FAT2 mirror).
      WriteInnerFatEntry(disk, ctx, cluster, 0);

      // Also zero the mirror cluster in the inner data area so the host-tool
      // fallback cannot recover the plaintext.
      var clusterBytes = ctx.SectorsPerCluster * ctx.BytesPerSector;
      var innerClusterOffset = ctx.InnerDataOffset + (cluster - 2) * clusterBytes;
      if (innerClusterOffset >= 0 && innerClusterOffset + clusterBytes <= disk.Length)
        Array.Clear(disk, innerClusterOffset, clusterBytes);

      if (nextCluster is 0 or >= 0xFFF8 and <= 0xFFFF) break;
      cluster = nextCluster;
    }

    // 2. Clear BitFAT bits for the freed physical regions. Only clear a
    //    region bit when EVERY sector in that 8 KB region is now free —
    //    a region may carry other clusters' runs too.
    foreach (var (start, count) in freedPhysSectors)
      ClearBitFatIfRegionFullyFree(disk, ctx, start, count);

    // 3. Scratch the dirent + LFN chain. Replace byte 0 of each entry with
    //    0xE5 (deleted marker) so the bytes after it stay byte-identical.
    for (var i = 0; i < locator.Value.DirentCount; i++) {
      var entryOffset = ctx.RootDirOffset + (locator.Value.FirstDirentSlot + i) * DirEntryBytes;
      if (entryOffset + DirEntryBytes > disk.Length) break;
      disk[entryOffset] = 0xE5;
    }

    return true;
  }

  // =========================================================================
  //                            Allocation helpers
  // =========================================================================

  private static int[]? AllocateLogicalClusters(byte[] disk, Context ctx, int count) {
    var result = new int[count];
    var found = 0;
    for (var c = 2; c < ctx.InnerTotalClusters && found < count; c++) {
      if (ReadInnerFatEntry(disk, ctx, c) == 0) {
        // Also sanity-check the MDFAT is zero (free flags). Allocated-but-zero
        // FAT slots shouldn't occur but we don't want to clobber a cluster the
        // host considers live.
        var mdfatEntryOffset = ctx.MdfatStartSector * ctx.BytesPerSector + c * 4;
        if (mdfatEntryOffset + 4 > disk.Length) return null;
        var mdfatEntry = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(mdfatEntryOffset));
        var mdfatFlags = (mdfatEntry >> 28) & 0xFu;
        if (mdfatFlags == 0)
          result[found++] = c;
      }
    }
    return found == count ? result : null;
  }

  private static int FindFirstFreePhysicalSector(byte[] disk, Context ctx) {
    // Walk every MDFAT entry, find max(physSector + runSectors) for any used
    // entry — this is the first sector at the tail of the in-use region.
    var maxEnd = 0;
    var entryCount = ctx.MdfatLenSectors * ctx.BytesPerSector / 4;
    for (var i = 0; i < entryCount; i++) {
      var entryOffset = ctx.MdfatStartSector * ctx.BytesPerSector + i * 4;
      if (entryOffset + 4 > disk.Length) break;
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(entryOffset));
      var flags = (entry >> 28) & 0xFu;
      if (flags == 0) continue;
      var physSector = (int)(entry & 0x1FFFFFu);
      var runSectors = (int)((entry >> 21) & 0x7Fu);
      maxEnd = Math.Max(maxEnd, physSector + runSectors);
    }
    return maxEnd;
  }

  /// <summary>
  /// Finds a contiguous free physical run of <paramref name="runSectors"/>
  /// sectors starting at or after <paramref name="startHint"/>. Returns -1
  /// if no such run fits inside <see cref="Context.DataLenSectors"/>.
  /// </summary>
  private static int FindFreePhysicalRun(byte[] disk, Context ctx, int runSectors, int startHint) {
    // Build a quick "used" bitmap by scanning the MDFAT.
    var used = new bool[ctx.DataLenSectors];
    var entryCount = ctx.MdfatLenSectors * ctx.BytesPerSector / 4;
    for (var i = 0; i < entryCount; i++) {
      var entryOffset = ctx.MdfatStartSector * ctx.BytesPerSector + i * 4;
      if (entryOffset + 4 > disk.Length) break;
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(entryOffset));
      var flags = (entry >> 28) & 0xFu;
      if (flags == 0) continue;
      var physSector = (int)(entry & 0x1FFFFFu);
      var nSect = (int)((entry >> 21) & 0x7Fu);
      for (var s = 0; s < nSect && physSector + s < ctx.DataLenSectors; s++)
        used[physSector + s] = true;
    }

    for (var s = Math.Max(0, startHint); s + runSectors <= ctx.DataLenSectors; s++) {
      var ok = true;
      for (var k = 0; k < runSectors; k++)
        if (used[s + k]) { ok = false; break; }
      if (ok) return s;
    }
    return -1;
  }

  // =========================================================================
  //                           BitFAT helpers
  // =========================================================================

  private static void SetBitFatBits(byte[] disk, Context ctx, int physSectorStart, int runSectors, bool clear) {
    var runByteStart = physSectorStart * ctx.BytesPerSector;
    var runByteEnd = runByteStart + runSectors * ctx.BytesPerSector;
    var firstRegion = runByteStart / BitFatRegionBytes;
    var lastRegion = (runByteEnd - 1) / BitFatRegionBytes;
    for (var r = firstRegion; r <= lastRegion; r++) {
      var bitPos = ctx.BitFatStartSector * ctx.BytesPerSector + r / 8;
      if (bitPos < 0 || bitPos >= disk.Length) break;
      var bitMask = (byte)(1 << (r & 7));
      if (clear)
        disk[bitPos] &= (byte)~bitMask;
      else
        disk[bitPos] |= bitMask;
    }
  }

  /// <summary>
  /// For each 8 KB region touched by the freed run, clears the BitFAT bit
  /// only when every sector in that region is now free. This preserves the
  /// invariant that the BitFAT bit is set whenever any sector in the
  /// corresponding 8 KB region is in use.
  /// </summary>
  private static void ClearBitFatIfRegionFullyFree(byte[] disk, Context ctx, int physSectorStart, int runSectors) {
    var runByteStart = physSectorStart * ctx.BytesPerSector;
    var runByteEnd = runByteStart + runSectors * ctx.BytesPerSector;
    var firstRegion = runByteStart / BitFatRegionBytes;
    var lastRegion = (runByteEnd - 1) / BitFatRegionBytes;
    var sectorsPerRegion = BitFatRegionBytes / ctx.BytesPerSector;

    // Re-scan MDFAT to see if any other cluster still occupies sectors in
    // each region we want to clear.
    var entryCount = ctx.MdfatLenSectors * ctx.BytesPerSector / 4;
    for (var r = firstRegion; r <= lastRegion; r++) {
      var regionFirstSector = r * sectorsPerRegion;
      var regionLastSector = regionFirstSector + sectorsPerRegion - 1;
      var anyUsed = false;
      for (var i = 0; i < entryCount && !anyUsed; i++) {
        var entryOffset = ctx.MdfatStartSector * ctx.BytesPerSector + i * 4;
        if (entryOffset + 4 > disk.Length) break;
        var entry = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(entryOffset));
        var flags = (entry >> 28) & 0xFu;
        if (flags == 0) continue;
        var physSector = (int)(entry & 0x1FFFFFu);
        var nSect = (int)((entry >> 21) & 0x7Fu);
        if (physSector + nSect <= regionFirstSector) continue;
        if (physSector > regionLastSector) continue;
        anyUsed = true;
      }
      if (!anyUsed) {
        var bitPos = ctx.BitFatStartSector * ctx.BytesPerSector + r / 8;
        if (bitPos >= 0 && bitPos < disk.Length)
          disk[bitPos] &= (byte)~(1 << (r & 7));
      }
    }
  }

  // =========================================================================
  //                           FAT16 helpers
  // =========================================================================

  private static int ReadInnerFatEntry(byte[] disk, Context ctx, int cluster) {
    var entryOffset = ctx.InnerFatOffset + cluster * 2;
    if (entryOffset + 2 > disk.Length) return FatEoc;
    return BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(entryOffset));
  }

  private static void WriteInnerFatEntry(byte[] disk, Context ctx, int cluster, int value) {
    var entry1 = ctx.InnerFatOffset + cluster * 2;
    if (entry1 + 2 > disk.Length) return;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(entry1), (ushort)value);
    // Mirror to FAT2 if present.
    if (ctx.FatCount >= 2) {
      var entry2 = ctx.InnerFat2Offset + cluster * 2;
      if (entry2 + 2 <= disk.Length)
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(entry2), (ushort)value);
    }
  }

  // =========================================================================
  //                       Root directory walk
  // =========================================================================

  private readonly record struct DirentLocator(int FirstDirentSlot, int DirentCount, int StartCluster, long Size, string Name);

  /// <summary>
  /// Scans the root directory for an entry whose visible name matches
  /// <paramref name="name"/>. Returns the slot index of the FIRST dirent
  /// (LFN chain head, if any) and the total dirent count (LFN entries +
  /// 8.3 short entry) so callers can scratch the whole chain in one pass.
  /// </summary>
  private static DirentLocator? LocateRootDirent(byte[] disk, Context ctx, string name) {
    var pendingLfn = new List<string>();
    var lfnStartSlot = -1;

    for (var i = 0; i < ctx.RootEntryCount; i++) {
      var off = ctx.RootDirOffset + i * DirEntryBytes;
      if (off + DirEntryBytes > disk.Length) break;

      var firstByte = disk[off];
      if (firstByte == 0x00) break;
      if (firstByte == 0xE5) { pendingLfn.Clear(); lfnStartSlot = -1; continue; }

      var attr = disk[off + 11];
      if ((attr & 0x3F) == LfnAttribute) {
        var seq = firstByte & 0x3F;
        var chars = new char[13];
        int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        for (var k = 0; k < 13; k++) {
          var ch = (ushort)(disk[off + slots[k]] | (disk[off + slots[k] + 1] << 8));
          chars[k] = (char)ch;
        }
        while (pendingLfn.Count < seq) pendingLfn.Add("");
        pendingLfn[seq - 1] = new string(chars);
        if (lfnStartSlot < 0) lfnStartSlot = i;
        continue;
      }

      if ((attr & 0x08) != 0) { pendingLfn.Clear(); lfnStartSlot = -1; continue; }
      if ((attr & 0x10) != 0) { pendingLfn.Clear(); lfnStartSlot = -1; continue; } // subdirs out of scope

      var shortName = GetShortName(disk, off);

      string visible = shortName;
      var direntCount = 1;
      var firstSlot = i;
      if (pendingLfn.Count > 0) {
        var combined = string.Concat(pendingLfn);
        var endIdx = combined.IndexOfAny(['\0', '￿']);
        if (endIdx >= 0) combined = combined[..endIdx];
        if (combined.Length > 0) visible = combined;
        direntCount = pendingLfn.Count + 1;
        if (lfnStartSlot >= 0) firstSlot = lfnStartSlot;
      }

      pendingLfn.Clear();
      lfnStartSlot = -1;

      if (string.Equals(visible, name, StringComparison.OrdinalIgnoreCase)
          || string.Equals(shortName, name, StringComparison.OrdinalIgnoreCase)) {
        var startCluster = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(off + 26));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(off + 28));
        return new DirentLocator(firstSlot, direntCount, startCluster, size, visible);
      }
    }
    return null;
  }

  private static string GetShortName(byte[] data, int offset) {
    var name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd();
    var ext = Encoding.ASCII.GetString(data, offset + 8, 3).TrimEnd();
    return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
  }

  /// <summary>
  /// Inserts a dirent (and optional VFAT LFN chain) into the first run of
  /// free root-directory slots that's large enough. Free slots are: 0x00
  /// (end-of-dir) or 0xE5 (scratched). The end-of-dir marker terminates
  /// reader walks, so we always re-write it after the inserted entry to
  /// preserve the terminator semantics.
  /// </summary>
  private static void InsertRootDirent(byte[] disk, Context ctx, string name, int startCluster, long size) {
    var shortName = GenerateShortName(name);
    var needsLfn = NeedsLfn(name);
    var lfnCount = needsLfn ? (name.Length + 12) / 13 : 0;
    var direntsNeeded = lfnCount + 1;

    var firstFree = FindFreeDirentRun(disk, ctx, direntsNeeded);
    if (firstFree < 0)
      throw new IOException("CVF root directory full: no free dirent slot.");

    // Capture the end-of-dir terminator preservation policy: if the slot we're
    // taking carries an end-of-dir marker (byte 0 == 0x00), the slot
    // immediately after our insertion must also become 0x00.
    var hadTerminator = false;
    var termSlot = firstFree;
    for (var k = 0; k < direntsNeeded && termSlot + k < ctx.RootEntryCount; k++) {
      var off = ctx.RootDirOffset + (termSlot + k) * DirEntryBytes;
      if (off + DirEntryBytes > disk.Length) break;
      if (disk[off] == 0x00) { hadTerminator = true; break; }
    }

    // Write LFN entries (reverse-order chain).
    var dirPos = ctx.RootDirOffset + firstFree * DirEntryBytes;
    if (needsLfn)
      dirPos = WriteLfnChain(disk, dirPos, name, shortName);

    WriteShortEntry(disk, dirPos, shortName, startCluster, (int)size);

    // Restore end-of-dir terminator in the slot after our insertion.
    if (hadTerminator) {
      var nextSlot = firstFree + direntsNeeded;
      if (nextSlot < ctx.RootEntryCount) {
        var nextOff = ctx.RootDirOffset + nextSlot * DirEntryBytes;
        if (nextOff + DirEntryBytes <= disk.Length && disk[nextOff] != 0x00)
          disk[nextOff] = 0x00;
      }
    }
  }

  /// <summary>
  /// Walks the root directory looking for a contiguous run of
  /// <paramref name="count"/> free dirent slots. Free means byte 0 is 0x00
  /// (end-of-dir) or 0xE5 (scratched). For end-of-dir markers we may
  /// "extend" — every slot from the marker onward is free — so a single
  /// 0x00 yields infinitely many free slots up to <see cref="Context.RootEntryCount"/>.
  /// </summary>
  private static int FindFreeDirentRun(byte[] disk, Context ctx, int count) {
    var run = 0;
    var runStart = -1;
    var sawTerminator = false;
    for (var i = 0; i < ctx.RootEntryCount; i++) {
      var off = ctx.RootDirOffset + i * DirEntryBytes;
      if (off + DirEntryBytes > disk.Length) break;
      var fb = disk[off];
      if (sawTerminator || fb is 0x00 or 0xE5) {
        if (fb == 0x00) sawTerminator = true;
        if (run == 0) runStart = i;
        run++;
        if (run >= count) return runStart;
      } else {
        run = 0;
        runStart = -1;
      }
    }
    return -1;
  }

  // =========================================================================
  //                       VFAT / 8.3 dirent writers
  // =========================================================================

  private static bool NeedsLfn(string name) {
    if (name.Length == 0) return false;
    var dotIdx = name.LastIndexOf('.');
    var basePart = dotIdx >= 0 ? name[..dotIdx] : name;
    var extPart = dotIdx >= 0 ? name[(dotIdx + 1)..] : "";
    if (basePart.Length == 0 || basePart.Length > 8 || extPart.Length > 3) return true;
    foreach (var c in name) {
      if (c == '.') continue;
      if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') continue;
      if ("!#$%&'()-@^_`{}~".Contains(c)) continue;
      return true;
    }
    return false;
  }

  private static string GenerateShortName(string longName) {
    var leaf = longName;
    var dotIdx = leaf.LastIndexOf('.');
    var basePart = (dotIdx >= 0 ? leaf[..dotIdx] : leaf).ToUpperInvariant();
    var extPart = (dotIdx >= 0 ? leaf[(dotIdx + 1)..] : "").ToUpperInvariant();
    basePart = new string([.. basePart.Where(IsShortNameChar)]);
    extPart = new string([.. extPart.Where(IsShortNameChar)]);
    if (basePart.Length == 0) basePart = "FILE";
    if (basePart.Length > 8) basePart = string.Concat(basePart.AsSpan(0, 6), "~1");
    if (extPart.Length > 3) extPart = extPart[..3];
    return extPart.Length > 0 ? $"{basePart}.{extPart}" : basePart;
  }

  private static bool IsShortNameChar(char c) =>
    c is >= 'A' and <= 'Z' or >= '0' and <= '9'
    || "!#$%&'()-@^_`{}~".Contains(c);

  private static byte LfnChecksum(ReadOnlySpan<byte> shortName83) {
    byte sum = 0;
    for (var i = 0; i < 11; i++)
      sum = (byte)((((sum & 1) != 0) ? 0x80 : 0) + (sum >> 1) + shortName83[i]);
    return sum;
  }

  private static byte[] EncodeShortName83(string shortName) {
    var dotIdx = shortName.LastIndexOf('.');
    var basePart = (dotIdx >= 0 ? shortName[..dotIdx] : shortName).ToUpperInvariant();
    var extPart = (dotIdx >= 0 ? shortName[(dotIdx + 1)..] : "").ToUpperInvariant();
    var buf = new byte[11];
    for (var i = 0; i < 11; i++) buf[i] = 0x20;
    Encoding.ASCII.GetBytes(basePart.Length > 8 ? basePart[..8] : basePart).CopyTo(buf, 0);
    if (extPart.Length > 0)
      Encoding.ASCII.GetBytes(extPart.Length > 3 ? extPart[..3] : extPart).CopyTo(buf, 8);
    return buf;
  }

  private static int WriteLfnChain(byte[] disk, int dirPos, string longName, string shortName) {
    var name83 = EncodeShortName83(shortName);
    var checksum = LfnChecksum(name83);
    var totalEntries = (longName.Length + 12) / 13;
    Span<byte> entry = stackalloc byte[DirEntryBytes];
    int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
    for (var seq = totalEntries; seq >= 1; seq--) {
      entry.Clear();
      var seqByte = (byte)seq;
      if (seq == totalEntries) seqByte |= 0x40;
      entry[0] = seqByte;
      entry[11] = LfnAttribute;
      entry[12] = 0x00;
      entry[13] = checksum;
      var startChar = (seq - 1) * 13;
      for (var i = 0; i < 13; i++) {
        ushort ch;
        if (startChar + i < longName.Length) ch = longName[startChar + i];
        else if (startChar + i == longName.Length) ch = 0x0000;
        else ch = 0xFFFF;
        entry[slots[i]] = (byte)(ch & 0xFF);
        entry[slots[i] + 1] = (byte)((ch >> 8) & 0xFF);
      }
      entry.CopyTo(disk.AsSpan(dirPos, DirEntryBytes));
      dirPos += DirEntryBytes;
    }
    return dirPos;
  }

  private static void WriteShortEntry(byte[] disk, int dirPos, string shortName, int firstCluster, int fileSize) {
    Array.Clear(disk, dirPos, DirEntryBytes);
    var name83 = EncodeShortName83(shortName);
    Array.Copy(name83, 0, disk, dirPos, 11);
    disk[dirPos + 11] = 0x20; // Archive attribute
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 26), (ushort)firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 28), (uint)fileSize);
  }

  private static byte[] WrapStoredRun(ReadOnlySpan<byte> input) {
    if (input.Length == 0) return [0x00, 0x00];
    var result = new byte[2 + input.Length];
    var header = (ushort)(input.Length - 1);
    result[0] = (byte)(header & 0xFF);
    result[1] = (byte)((header >> 8) & 0xFF);
    input.CopyTo(result.AsSpan(2));
    return result;
  }
}
