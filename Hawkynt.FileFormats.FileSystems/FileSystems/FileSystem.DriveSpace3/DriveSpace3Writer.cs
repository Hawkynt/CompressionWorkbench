#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.DriveSpace3;

/// <summary>
/// Builds a spec-compliant Microsoft DriveSpace 3 CVF (Windows 95 Plus! Pack,
/// 1995). On-disk layout mirrors the DOS 6.x DBLSPACE/DRVSPACE convention:
/// MDBPB → inner FAT16 → inner root dir → MDFAT → BitFAT → DATA region. The
/// only differences from <c>FileSystem.DoubleSpace.DoubleSpaceWriter</c> are:
/// <list type="bullet">
///   <item>OEM name <c>MS_DSP3</c> at offset 3 (vs <c>MSDSP6.0</c>/<c>MSDSP6.2</c>).</item>
///   <item>CvfSignature <c>DVR3</c> at offset 36.</item>
///   <item>Per-cluster compression payload uses the MS LZH codec (see
///         <see cref="MsLzhBlockCodec"/>) instead of DS LZ77.</item>
///   <item>A compression-level byte at offset 75 (DriveSpace 3 BPB extension).</item>
/// </list>
/// <para>
/// This writer emits either stored runs (MDFAT flag = 1) or MS LZH-compressed
/// runs (flag = 2), depending on whether the codec shrinks the cluster. The
/// reader follows the inner FAT chain through the MDFAT indirection back to
/// each compressed run, exactly as DoubleSpace does. Stage 2 self round-trip
/// is the gating requirement; bit-stream parity with Microsoft's reference
/// driver (DRVSPACE.BIN) is a future external-tool conformance gate.
/// </para>
/// </summary>
public sealed class DriveSpace3Writer {
  // ---- On-disk geometry ---------------------------------------------------
  internal const int BytesPerSector = 512;
  internal const int SectorsPerCluster = 8;           // 4 KB inner cluster
  internal const int ClusterBytes = BytesPerSector * SectorsPerCluster;
  internal const int ReservedSectors = 1;             // just the MDBPB
  internal const int InnerFatCount = 2;
  internal const int InnerRootEntryCount = 512;       // 16 sectors
  internal const int BitFatRegionBytes = 8192;        // 1 bit tracks 8 KB

  // ---- User inputs --------------------------------------------------------
  private readonly List<(string Name, byte[] Data, bool Compress)> _files = [];

  /// <summary>
  /// When <c>true</c> (default), per-cluster MS LZH compression is attempted
  /// and the compressed payload is emitted whenever it shrinks the cluster.
  /// Clusters that do not compress are stored raw (MDFAT flags = 1).
  /// </summary>
  public bool EnableCompression { get; set; } = true;

  /// <summary>
  /// DriveSpace 3 compression level byte stored at MDBPB offset 75. Values
  /// 0..2 correspond to the "Standard", "HiPack", and "UltraPack" levels in
  /// the Microsoft tooling. The actual encoder here ignores the field — it
  /// is preserved for round-trip compatibility with third-party readers.
  /// </summary>
  public byte CompressionLevel { get; set; } = 1;

  /// <summary>Adds a file. Long filenames produce a VFAT LFN chain automatically.</summary>
  public void AddFile(string name, byte[] data) => this.AddFile(name, data, compress: true);

  /// <summary>
  /// Adds a file with an explicit per-file compression opt-in. Use
  /// <paramref name="compress"/>=<c>false</c> to force stored runs for that
  /// file even when <see cref="EnableCompression"/> is on.
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
    var (innerFileClusters, _) = BudgetInnerVolume();
    var rootDirSectors = (InnerRootEntryCount * 32 + BytesPerSector - 1) / BytesPerSector;

    // FAT16 minimum cluster count rule — pad to force FAT16 detection.
    const int minFat16Clusters = 4085;
    var innerTotalClusters = Math.Max(minFat16Clusters + 4, innerFileClusters + 2);
    var innerFatSize = (innerTotalClusters * 2 + BytesPerSector - 1) / BytesPerSector;

    var innerFirstDataSector = ReservedSectors + InnerFatCount * innerFatSize + rootDirSectors;
    var innerDataSectors = innerTotalClusters * SectorsPerCluster;

    var mdfatEntries = innerTotalClusters;
    var mdfatSectors = (mdfatEntries * 4 + BytesPerSector - 1) / BytesPerSector;

    // Stored runs cap at 4098 bytes (header + 4096 cluster), so 9 sectors max.
    const int maxPhysSectorsPerCluster = 9;
    var maxDataSectors = innerFileClusters * maxPhysSectorsPerCluster + SectorsPerCluster;

    var bitFatRegions = (maxDataSectors * BytesPerSector + BitFatRegionBytes - 1) / BitFatRegionBytes;
    var bitFatSectors = (bitFatRegions + 8 * BytesPerSector - 1) / (8 * BytesPerSector);
    if (bitFatSectors < 1) bitFatSectors = 1;

    var mdfatStart = innerFirstDataSector + innerDataSectors;
    var bitFatStart = mdfatStart + mdfatSectors;
    var dataStart = bitFatStart + bitFatSectors;

    var totalSectors = dataStart + maxDataSectors;
    if (totalSectors < 2880) totalSectors = 2880;

    var disk = new byte[totalSectors * BytesPerSector];

    this.WriteMdbpb(disk, totalSectors, innerFatSize, innerTotalClusters,
      mdfatStart, mdfatSectors, bitFatStart, bitFatSectors, dataStart, maxDataSectors);

    var innerFatOffset = ReservedSectors * BytesPerSector;
    WriteInnerFat16Init(disk, innerFatOffset);

    var innerRootOffset = (ReservedSectors + InnerFatCount * innerFatSize) * BytesPerSector;
    var innerFatPlan = this.WriteRootDirectoryAndAssignClusters(disk, innerRootOffset, innerTotalClusters);

    var innerDataOffset = innerFirstDataSector * BytesPerSector;
    this.BuildDataRegion(disk, innerFatOffset, innerFatPlan, innerDataOffset,
      mdfatStart, bitFatStart, dataStart, maxDataSectors);

    // Mirror FAT1 to FAT2.
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
    disk[0] = 0xEB; disk[1] = 0x58; disk[2] = 0x90;             // JMP

    // OEM name: 8 bytes at offset 3. DriveSpace 3 uses "MS_DSP3" (7 chars)
    // followed by a NUL/pad to fill the BPB OEM field.
    Encoding.ASCII.GetBytes("MS_DSP3").CopyTo(disk, 3);
    disk[10] = 0x00;

    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), BytesPerSector);
    disk[13] = SectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), ReservedSectors);
    disk[16] = InnerFatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), InnerRootEntryCount);
    if (totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), (ushort)totalSectors);
    disk[21] = 0xF8;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), (ushort)innerFatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), 63);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), 255);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(28), 0);
    if (totalSectors >= 65536)
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(32), (uint)totalSectors);

    // CVF-specific fields at offset 36 onwards.
    Encoding.ASCII.GetBytes("DVR3").CopyTo(disk, 36);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(40), 0x00030300u);   // DriveSpace 3
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(44), (uint)mdfatStart);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(48), (uint)mdfatSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(52), (uint)bitFatStart);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(56), (uint)bitFatSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(60), (uint)dataStart);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(64), (uint)dataSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(68), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(72), (uint)innerTotalClusters);

    // DriveSpace 3 extension: compression-level byte at offset 75.
    disk[76] = this.CompressionLevel;

    // Boot signature.
    disk[510] = 0x55; disk[511] = 0xAA;
  }

  // =========================================================================
  //                     Inner FAT16 + root directory writer
  // =========================================================================

  private static void WriteInnerFat16Init(byte[] disk, int innerFatOffset) {
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

  private readonly record struct PlannedFile(string Name, byte[] Data, int FirstCluster, int ClusterCount, bool Compress);

  private List<PlannedFile> WriteRootDirectoryAndAssignClusters(byte[] disk, int rootOffset, int innerTotalClusters) {
    var plan = new List<PlannedFile>();
    var dirPos = rootOffset;
    var nextCluster = 2;

    foreach (var (name, data, compress) in this._files) {
      var clustersNeeded = Math.Max(1, (data.Length + ClusterBytes - 1) / ClusterBytes);
      if (nextCluster + clustersNeeded > innerTotalClusters) break;

      var shortName = GenerateShortName(name);

      if (NeedsLfn(name))
        dirPos = WriteLfnChain(disk, dirPos, name, shortName);

      WriteShortEntry(disk, dirPos, shortName, nextCluster, data.Length);
      dirPos += 32;

      plan.Add(new PlannedFile(name, data, nextCluster, clustersNeeded, compress));
      nextCluster += clustersNeeded;
    }

    return plan;
  }

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
    for (var seq = totalEntries; seq >= 1; seq--) {
      var entry = new byte[32];
      var seqByte = (byte)seq;
      if (seq == totalEntries) seqByte |= 0x40;
      entry[0] = seqByte;
      entry[11] = 0x0F;
      entry[12] = 0x00;
      entry[13] = checksum;
      var startChar = (seq - 1) * 13;
      int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
      for (var i = 0; i < 13; i++) {
        ushort ch;
        if (startChar + i < longName.Length) ch = longName[startChar + i];
        else if (startChar + i == longName.Length) ch = 0x0000;
        else ch = 0xFFFF;
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
    disk[dirPos + 11] = 0x20;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(dirPos + 26), (ushort)firstCluster);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(dirPos + 28), (uint)fileSize);
  }

  // =========================================================================
  //                              DATA region
  // =========================================================================

  private void BuildDataRegion(
    byte[] disk, int innerFatOffset, List<PlannedFile> files, int innerDataOffset,
    int mdfatStart, int bitFatStart, int dataStart, int dataSectors) {

    var physSectorPos = 0;
    var clusterBytes = new byte[ClusterBytes];

    foreach (var file in files) {
      var data = file.Data;
      for (var c = 0; c < file.ClusterCount; c++) {
        var cluster = file.FirstCluster + c;
        var offsetInFile = c * ClusterBytes;
        var remaining = data.Length - offsetInFile;
        var chunkLen = Math.Min(ClusterBytes, remaining);

        Array.Clear(clusterBytes);
        if (chunkLen > 0) data.AsSpan(offsetInFile, chunkLen).CopyTo(clusterBytes);

        // Mirror the raw cluster into the inner FAT data region as a
        // fallback for host-side tools that ignore the MDFAT indirection.
        var innerClusterOffset = innerDataOffset + (cluster - 2) * ClusterBytes;
        if (innerClusterOffset + ClusterBytes <= disk.Length)
          clusterBytes.CopyTo(disk.AsSpan(innerClusterOffset));

        var validChunk = Math.Max(1, chunkLen);
        var rawSpan = data.AsSpan(offsetInFile, validChunk);

        byte[] block;
        uint flagsNibble;
        if (this.EnableCompression && file.Compress && validChunk >= 32) {
          block = MsLzhBlockCodec.Compress(rawSpan);
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

        var mdfatEntry = ((uint)runStartSector & 0x1FFFFFu)
          | (((uint)runSectors & 0x7Fu) << 21)
          | (flagsNibble << 28);
        var mdfatEntryOffset = mdfatStart * BytesPerSector + cluster * 4;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(mdfatEntryOffset), mdfatEntry);

        var runByteStart = runStartSector * BytesPerSector;
        var runByteEnd = runByteStart + runSectors * BytesPerSector;
        var firstRegion = runByteStart / BitFatRegionBytes;
        var lastRegion = (runByteEnd - 1) / BitFatRegionBytes;
        for (var r = firstRegion; r <= lastRegion; r++) {
          var bitPos = bitFatStart * BytesPerSector + (r / 8);
          disk[bitPos] |= (byte)(1 << (r & 7));
        }

        var innerFatEntryOffset = innerFatOffset + cluster * 2;
        var nextVal = (c + 1 < file.ClusterCount) ? cluster + 1 : 0xFFFF;
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(innerFatEntryOffset), (ushort)nextVal);
      }
    }

    _ = dataSectors;
  }

  /// <summary>
  /// Emits a stored CVF run (2-byte header, bit 15 clear, size-1 in low bits)
  /// followed by the raw bytes. Used when compression is disabled or fails to
  /// shrink a cluster.
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
