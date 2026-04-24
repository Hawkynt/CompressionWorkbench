#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Variant of the CVF (Compressed Volume File) being produced.
/// </summary>
public enum CvfVariant {
  /// <summary>MS-DOS 6.0 DoubleSpace. OEM name <c>MSDSP6.0</c>, CvfSignature <c>DBLS</c>.</summary>
  DoubleSpace60,
  /// <summary>MS-DOS 6.22 DriveSpace. OEM name <c>MSDSP6.2</c>, CvfSignature <c>DVRS</c>.</summary>
  DriveSpace62,
  /// <summary>Windows 95 OSR2 DriveSpace 3.0. OEM name <c>DRVSPACE</c>, CvfSignature <c>DVRS</c>.</summary>
  DriveSpace30,
}

/// <summary>
/// Builds a spec-compliant Microsoft DoubleSpace / DriveSpace Compressed Volume
/// File (CVF).
/// <para>
/// Layout produced (in sector units, 512 B / sector):
/// </para>
/// <list type="bullet">
///   <item>Sector 0 — <b>MDBPB</b> (Master DoubleSpace BIOS Parameter Block).
///     First 36 bytes are a standard FAT BPB so the host can identify the
///     volume. Offsets 36..71 are the CVF-specific fields (CvfSignature,
///     CvfVersion, MdfatStart/Len, BitFatStart/Len, DataStart/Len,
///     HostFatCopyStart).</item>
///   <item><b>Inner FAT1/FAT2</b> — the inner FAT12/16 tables used by the
///     compressed volume's filesystem.</item>
///   <item><b>Inner root directory</b> — fixed-size FAT12/16 root with 8.3
///     entries plus VFAT LFN chains for names that don't fit 8.3.</item>
///   <item><b>MDFAT</b> — one uint32 entry per logical cluster of the data
///     area; maps logical cluster → first physical sector of the compressed
///     run, run length in sectors and a flags nibble (0=free, 1=stored,
///     2=compressed).</item>
///   <item><b>BitFAT</b> — 1 bit per 8 KB region of the data area marking
///     in-use regions.</item>
///   <item><b>DATA region</b> — compressed clusters packed as <see cref="DsCompression"/>
///     blocks (2-byte header + payload). This writer emits only <b>stored</b>
///     runs (header bit 15 clear) containing the raw cluster contents — the
///     JM/DSS LZ variant is NOT produced. A real DRVSPACE.BIN driver accepts
///     stored runs transparently.</item>
/// </list>
/// <para>
/// <b>NOTE on JM/DSS compression:</b> DoubleSpace 3.0 uses a simple LZ77
/// scheme with 16-bit length-prefixed tokens ("JM" encoding). Implementing
/// it is a future enhancement; the on-disk layout here is already compatible
/// — only the cluster payload bytes would change. Point of extension:
/// replace the stored-run writer call in <see cref="BuildDataRegion"/> with
/// a JM encoder and set MDFAT flag nibble to 2 instead of 1.
/// </para>
/// </summary>
public sealed class DoubleSpaceWriter {
  // ---- On-disk geometry ---------------------------------------------------
  internal const int BytesPerSector = 512;
  internal const int SectorsPerCluster = 8;           // 4 KB inner cluster
  internal const int ClusterBytes = BytesPerSector * SectorsPerCluster;
  internal const int ReservedSectors = 1;              // just the MDBPB
  internal const int InnerFatCount = 2;
  internal const int InnerRootEntryCount = 512;        // 16 sectors
  internal const int BitFatRegionBytes = 8192;         // 1 bit tracks 8 KB

  // ---- User inputs --------------------------------------------------------
  private readonly List<(string Name, byte[] Data, bool Compress)> _files = [];
  private CvfVariant _variant = CvfVariant.DoubleSpace60;

  /// <summary>Which CVF variant to produce (signatures and CvfVersion differ).</summary>
  public CvfVariant Variant { get => _variant; set => _variant = value; }

  /// <summary>Back-compat shim: <c>true</c> = DriveSpace (6.2), <c>false</c> = DoubleSpace (6.0).</summary>
  public bool DriveSpace {
    get => _variant != CvfVariant.DoubleSpace60;
    set => _variant = value ? CvfVariant.DriveSpace62 : CvfVariant.DoubleSpace60;
  }

  /// <summary>
  /// When <c>true</c> (default), per-cluster JM/LZ compression is attempted
  /// and the compressed payload is emitted whenever it shrinks the cluster.
  /// Clusters that do not compress are stored raw (MDFAT flags = 1).
  /// </summary>
  public bool EnableCompression { get; set; } = true;

  /// <summary>Adds a file. <paramref name="name"/> may be a long filename; a VFAT LFN chain is emitted automatically.</summary>
  public void AddFile(string name, byte[] data) => this.AddFile(name, data, compress: true);

  /// <summary>
  /// Adds a file with an explicit per-file compression opt-in. Use
  /// <paramref name="compress"/>=<c>false</c> to force stored runs for that
  /// file even when <see cref="EnableCompression"/> is on (useful for mixed
  /// stored/compressed tests or for already-compressed payloads where LZ
  /// would only waste CPU).
  /// </summary>
  public void AddFile(string name, byte[] data, bool compress) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data, compress));
  }

  // =========================================================================
  //                                 Build
  // =========================================================================

  /// <summary>Builds the complete CVF image.</summary>
  public byte[] Build() {
    // Step 1 — budget the inner FAT volume.
    // Each file takes ceil(size / ClusterBytes) clusters (min 1). Directory
    // entries add 32 B per 8.3 entry plus 32 B per VFAT LFN chain entry.
    var (innerFileClusters, dirEntryCount) = BudgetInnerVolume();
    var rootDirSectors = (InnerRootEntryCount * 32 + BytesPerSector - 1) / BytesPerSector;

    // Round the inner FAT size up so we can address all clusters.
    // Keep it simple: always FAT16 (4-byte entries per cluster-2 slot).
    // Minimum cluster count for FAT16 is 4085 per MS BPB rules, so pad with
    // unused clusters to force FAT16 detection on the host side.
    const int minFat16Clusters = 4085;
    var innerTotalClusters = Math.Max(minFat16Clusters + 4, innerFileClusters + 2);
    var innerFatSize = (innerTotalClusters * 2 + BytesPerSector - 1) / BytesPerSector;

    var innerFirstDataSector = ReservedSectors + InnerFatCount * innerFatSize + rootDirSectors;
    var innerDataSectors = innerTotalClusters * SectorsPerCluster;
    // _ = dirEntryCount; // entries packed in-place in root dir sectors

    // Step 2 — budget MDFAT / BitFAT / outer data region.
    // MDFAT has one uint32 per inner cluster (entire inner volume, not just
    // files — free slots are zeroed). This matches real DoubleSpace 3.0
    // (extended MDFAT with wide entries, selected via CvfVersion).
    var mdfatEntries = innerTotalClusters;
    var mdfatSectors = (mdfatEntries * 4 + BytesPerSector - 1) / BytesPerSector;

    // Each used inner cluster produces one compressed run whose length in
    // physical sectors is bounded by (ClusterBytes + 2) / 512 + 1 = 9 for
    // stored runs (2-byte header + 4096 B data = 4098 B → 9 sectors). We
    // over-allocate to round up the BitFAT region.
    const int maxPhysSectorsPerCluster = 9;
    var maxDataSectors = innerFileClusters * maxPhysSectorsPerCluster + SectorsPerCluster;

    var bitFatRegions = (maxDataSectors * BytesPerSector + BitFatRegionBytes - 1) / BitFatRegionBytes;
    var bitFatSectors = (bitFatRegions + 8 * BytesPerSector - 1) / (8 * BytesPerSector);
    if (bitFatSectors < 1) bitFatSectors = 1;

    // Sector plan:
    //   0                               MDBPB
    //   ReservedSectors..               Inner FAT1 / FAT2
    //   innerRootStart..                Inner root dir
    //   innerFirstDataSector            (first addressable inner data — reserved so host view works)
    //   mdfatStart                      MDFAT
    //   bitFatStart                     BitFAT
    //   dataStart                       Compressed DATA region
    var mdfatStart = innerFirstDataSector + innerDataSectors;
    var bitFatStart = mdfatStart + mdfatSectors;
    var dataStart = bitFatStart + bitFatSectors;

    var totalSectors = dataStart + maxDataSectors;
    if (totalSectors < 2880) totalSectors = 2880;

    var disk = new byte[totalSectors * BytesPerSector];

    // Step 3 — emit the MDBPB.
    WriteMdbpb(disk, totalSectors, innerFatSize, innerTotalClusters,
      mdfatStart, mdfatSectors, bitFatStart, bitFatSectors, dataStart, maxDataSectors);

    // Step 4 — initialize the inner FAT (FAT16) and cluster-2 bootstrap.
    var innerFatOffset = ReservedSectors * BytesPerSector;
    WriteInnerFat16Init(disk, innerFatOffset);

    // Step 5 — write root directory entries (with VFAT LFN support).
    var innerRootOffset = (ReservedSectors + InnerFatCount * innerFatSize) * BytesPerSector;
    var innerFatPlan = WriteRootDirectoryAndAssignClusters(disk, innerRootOffset, innerTotalClusters);

    // Step 6 — build the DATA region and populate MDFAT / BitFAT.
    var innerDataOffset = innerFirstDataSector * BytesPerSector;
    this.BuildDataRegion(disk, innerFatOffset, innerFatPlan, innerDataOffset,
      mdfatStart, bitFatStart, dataStart, maxDataSectors);

    // Step 7 — mirror FAT1 to FAT2.
    Array.Copy(disk, innerFatOffset, disk,
      innerFatOffset + innerFatSize * BytesPerSector, innerFatSize * BytesPerSector);

    return disk;
  }

  // =========================================================================
  //                              MDBPB writer
  // =========================================================================

  private void WriteMdbpb(
    byte[] disk, int totalSectors, int innerFatSize, int innerTotalClusters,
    int mdfatStart, int mdfatSectors, int bitFatStart, int bitFatSectors,
    int dataStart, int dataSectors) {

    // Standard FAT BPB (first 36 bytes).
    disk[0] = 0xEB; disk[1] = 0x58; disk[2] = 0x90;                 // JMP
    Encoding.ASCII.GetBytes(OemName()).CopyTo(disk, 3);             // OEM name, 8 B
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), BytesPerSector);
    disk[13] = SectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), ReservedSectors);
    disk[16] = InnerFatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), InnerRootEntryCount);
    if (totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), (ushort)totalSectors);
    // BPB_Media
    disk[21] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), (ushort)innerFatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), 63);  // SecPerTrk
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), 255); // NumHeads
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(28), 0);   // HiddSec
    if (totalSectors >= 65536)
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(32), (uint)totalSectors);

    // DoubleSpace-specific fields at offset 36 onwards.
    Encoding.ASCII.GetBytes(CvfSignature()).CopyTo(disk, 36);       // 36..39 "DBLS"/"DVRS"
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(40), CvfVersion());
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(44), (uint)mdfatStart);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(48), (uint)mdfatSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(52), (uint)bitFatStart);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(56), (uint)bitFatSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(60), (uint)dataStart);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(64), (uint)dataSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(68), 0);   // HostFatCopyStart (unused)
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(72), (uint)innerTotalClusters);

    // Boot signature.
    disk[510] = 0x55; disk[511] = 0xAA;
  }

  private string OemName() => this._variant switch {
    CvfVariant.DoubleSpace60 => "MSDSP6.0",
    CvfVariant.DriveSpace62 => "MSDSP6.2",
    CvfVariant.DriveSpace30 => "DRVSPACE",
    _ => "MSDSP6.0",
  };

  private string CvfSignature() => this._variant switch {
    CvfVariant.DoubleSpace60 => "DBLS",
    _ => "DVRS",
  };

  private uint CvfVersion() => this._variant switch {
    CvfVariant.DoubleSpace60 => 0x00030000u,
    CvfVariant.DriveSpace62 => 0x00030200u,
    CvfVariant.DriveSpace30 => 0x00030300u,
    _ => 0x00030000u,
  };

  // =========================================================================
  //                     Inner FAT16 + root directory writer
  // =========================================================================

  private static void WriteInnerFat16Init(byte[] disk, int innerFatOffset) {
    // Media byte + EoC marker for clusters 0 and 1.
    disk[innerFatOffset] = 0xF8; disk[innerFatOffset + 1] = 0xFF;
    disk[innerFatOffset + 2] = 0xFF; disk[innerFatOffset + 3] = 0xFF;
  }

  private (int TotalClusters, int EntriesUsed) BudgetInnerVolume() {
    var clusters = 0;
    var entries = 0;
    foreach (var (name, data, _) in this._files) {
      var cNeeded = Math.Max(1, (data.Length + ClusterBytes - 1) / ClusterBytes);
      clusters += cNeeded;
      var lfnEntries = NeedsLfn(name) ? (name.Length + 12) / 13 : 0;
      entries += lfnEntries + 1;
    }
    return (clusters, entries);
  }

  /// <summary>One physical file occupying a contiguous FAT chain.</summary>
  private readonly record struct PlannedFile(string Name, byte[] Data, int FirstCluster, int ClusterCount, bool Compress);

  private List<PlannedFile> WriteRootDirectoryAndAssignClusters(byte[] disk, int rootOffset, int innerTotalClusters) {
    var plan = new List<PlannedFile>();
    var dirPos = rootOffset;
    var nextCluster = 2;

    foreach (var (name, data, compress) in this._files) {
      var clustersNeeded = Math.Max(1, (data.Length + ClusterBytes - 1) / ClusterBytes);
      if (nextCluster + clustersNeeded > innerTotalClusters) break; // out of room

      var shortName = GenerateShortName(name, existingShortNames: null);

      // Emit VFAT LFN chain if needed.
      if (NeedsLfn(name))
        dirPos = WriteLfnChain(disk, dirPos, name, shortName);

      // Emit the 8.3 directory entry.
      WriteShortEntry(disk, dirPos, shortName, nextCluster, data.Length);
      dirPos += 32;

      plan.Add(new PlannedFile(name, data, nextCluster, clustersNeeded, compress));
      nextCluster += clustersNeeded;
    }

    return plan;
  }

  // ---- VFAT LFN support ---------------------------------------------------

  private static bool NeedsLfn(string name) {
    // An 8.3 name is valid if the base <= 8 chars, ext <= 3 chars, and all
    // chars are OEM-safe uppercase. Anything else requires an LFN chain.
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

  private static string GenerateShortName(string longName, HashSet<string>? existingShortNames) {
    var leaf = longName;
    var dotIdx = leaf.LastIndexOf('.');
    var basePart = (dotIdx >= 0 ? leaf[..dotIdx] : leaf).ToUpperInvariant();
    var extPart = (dotIdx >= 0 ? leaf[(dotIdx + 1)..] : "").ToUpperInvariant();
    basePart = new string([.. basePart.Where(IsShortNameChar)]);
    extPart = new string([.. extPart.Where(IsShortNameChar)]);
    if (basePart.Length == 0) basePart = "FILE";
    if (basePart.Length > 8) basePart = string.Concat(basePart.AsSpan(0, 6), "~1");
    if (extPart.Length > 3) extPart = extPart[..3];
    var candidate = extPart.Length > 0 ? $"{basePart}.{extPart}" : basePart;
    if (existingShortNames == null || existingShortNames.Add(candidate)) return candidate;
    // Collision — tail with ~N.
    for (var i = 2; i < 1000; i++) {
      var baseTrim = basePart.Length > 6 ? basePart[..6] : basePart;
      var tag = $"~{i}";
      var trimmed = baseTrim.Length + tag.Length > 8 ? baseTrim[..(8 - tag.Length)] : baseTrim;
      candidate = extPart.Length > 0 ? $"{trimmed}{tag}.{extPart}" : $"{trimmed}{tag}";
      if (existingShortNames.Add(candidate)) return candidate;
    }
    return candidate;
  }

  private static bool IsShortNameChar(char c) =>
    c is >= 'A' and <= 'Z' or >= '0' and <= '9'
    || "!#$%&'()-@^_`{}~".Contains(c);

  /// <summary>VFAT LFN checksum per Microsoft FAT spec.</summary>
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

    // LFN entries are written in reverse order (last one first in the file,
    // with sequence number 1). Spec: 13 UCS-2 chars per entry; last entry
    // has its sequence number OR'd with 0x40.
    var totalEntries = (longName.Length + 12) / 13;
    for (var seq = totalEntries; seq >= 1; seq--) {
      var entry = new byte[32];
      var seqByte = (byte)seq;
      if (seq == totalEntries) seqByte |= 0x40; // last (physically first) entry marker
      entry[0] = seqByte;
      entry[11] = 0x0F;  // LFN attribute
      entry[12] = 0x00;  // reserved (type)
      entry[13] = checksum;
      // entry[26..28] = first cluster (always 0 in LFN)
      // Copy 13 UCS-2 chars from the corresponding slice of longName.
      var startChar = (seq - 1) * 13;
      int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
      for (var i = 0; i < 13; i++) {
        ushort ch;
        if (startChar + i < longName.Length) ch = longName[startChar + i];
        else if (startChar + i == longName.Length) ch = 0x0000;          // NUL terminator
        else ch = 0xFFFF;                                                 // padding
        entry[slots[i]] = (byte)(ch & 0xFF);
        entry[slots[i] + 1] = (byte)((ch >> 8) & 0xFF);
      }
      Array.Copy(entry, 0, disk, dirPos, 32);
      dirPos += 32;
    }
    return dirPos;
  }

  private static void WriteShortEntry(byte[] disk, int dirPos, string shortName, int firstCluster, int fileSize) {
    var name83 = EncodeShortName83(shortName);
    Array.Copy(name83, 0, disk, dirPos, 11);
    disk[dirPos + 11] = 0x20; // Archive attribute
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 26), (ushort)firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 28), (uint)fileSize);
  }

  // =========================================================================
  //                              DATA region
  // =========================================================================

  private void BuildDataRegion(
    byte[] disk, int innerFatOffset, List<PlannedFile> files, int innerDataOffset,
    int mdfatStart, int bitFatStart, int dataStart, int dataSectors) {

    var physSectorPos = 0; // offset in sectors within the DATA region
    var clusterBytes = new byte[ClusterBytes];
    var useDriveSpace = this._variant != CvfVariant.DoubleSpace60;

    foreach (var file in files) {
      // Walk the file's cluster chain. For each cluster, emit either a
      // compressed run (MDFAT flags = 2) or a stored run (MDFAT flags = 1)
      // — the reader dispatches on the per-run 2-byte CVF header bit 15.
      var data = file.Data;
      for (var c = 0; c < file.ClusterCount; c++) {
        var cluster = file.FirstCluster + c;
        var offsetInFile = c * ClusterBytes;
        var remaining = data.Length - offsetInFile;
        var chunkLen = Math.Min(ClusterBytes, remaining);

        // Build the raw cluster buffer (zero-padded if short).
        Array.Clear(clusterBytes);
        if (chunkLen > 0) data.AsSpan(offsetInFile, chunkLen).CopyTo(clusterBytes);

        // Also write the cluster to the inner data region so host tools that
        // ignore the MDFAT indirection can still read the volume.
        var innerClusterOffset = innerDataOffset + (cluster - 2) * ClusterBytes;
        if (innerClusterOffset + ClusterBytes <= disk.Length)
          clusterBytes.CopyTo(disk.AsSpan(innerClusterOffset));

        var validChunk = Math.Max(1, chunkLen);
        var rawSpan = data.AsSpan(offsetInFile, validChunk);

        // Decide stored vs. compressed for this cluster. Compression is only
        // attempted when globally enabled, per-file opted-in, and the cluster
        // has >=32 bytes of real data (tiny inputs rarely shrink usefully).
        byte[] block;
        uint flagsNibble;
        if (this.EnableCompression && file.Compress && validChunk >= 32) {
          block = useDriveSpace
            ? DsCompression.CompressDriveSpace(rawSpan)
            : DsCompression.Compress(rawSpan);
          var headerWord = (ushort)(block[0] | (block[1] << 8));
          var wasCompressed = (headerWord & 0x8000) != 0;
          flagsNibble = wasCompressed ? 0x2u : 0x1u;
        } else {
          block = WrapStoredRun(rawSpan);
          flagsNibble = 0x1u;
        }

        var runStartSector = physSectorPos;
        var runSectors = (block.Length + BytesPerSector - 1) / BytesPerSector;

        if (dataStart + runStartSector + runSectors > disk.Length / BytesPerSector) break;

        var dataOffset = (dataStart + runStartSector) * BytesPerSector;
        block.CopyTo(disk, dataOffset);
        physSectorPos += runSectors;

        // MDFAT entry: bits 0..20 physical sector (relative to DataStart),
        // bits 21..27 run length in sectors, bits 28..31 flags.
        var mdfatEntry = ((uint)runStartSector & 0x1FFFFFu)
          | (((uint)runSectors & 0x7Fu) << 21)
          | (flagsNibble << 28);
        var mdfatEntryOffset = mdfatStart * BytesPerSector + cluster * 4;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(mdfatEntryOffset), mdfatEntry);

        // BitFAT: mark every 8 KB region touched by this run.
        var runByteStart = runStartSector * BytesPerSector;
        var runByteEnd = runByteStart + runSectors * BytesPerSector;
        var firstRegion = runByteStart / BitFatRegionBytes;
        var lastRegion = (runByteEnd - 1) / BitFatRegionBytes;
        for (var r = firstRegion; r <= lastRegion; r++) {
          var bitPos = bitFatStart * BytesPerSector + (r / 8);
          disk[bitPos] |= (byte)(1 << (r & 7));
        }

        // Inner FAT16 chain for this cluster.
        var innerFatEntryOffset = innerFatOffset + cluster * 2;
        var nextVal = (c + 1 < file.ClusterCount) ? cluster + 1 : 0xFFFF; // EoC
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(innerFatEntryOffset), (ushort)nextVal);
      }
    }

    _ = dataSectors;
  }

  /// <summary>
  /// Emits a stored CVF run (2-byte header, bit 15 clear, size-1 in low bits)
  /// followed by the raw bytes. Used when compression is disabled for a
  /// cluster or when the compressed form would not shrink the data.
  /// </summary>
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
