#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Fat;

/// <summary>
/// Builds FAT12 / FAT16 / FAT32 filesystem images from scratch per the Microsoft
/// FAT specification (FATGEN103, EFI FAT32). Auto-selects FAT type based on
/// cluster count. Emits VFAT / LFN (Long File Name) directory entries
/// transparently when the input filename does not fit in 8.3 (mixed-case,
/// non-ASCII, longer than 8 + 3 chars, or with multiple dots) — DOS-era
/// readers see only the short name, modern readers see the long one.
/// </summary>
/// <remarks>
/// FAT32 layout: 32 reserved sectors (boot @0, FSInfo @1, backup boot @6), two
/// FAT copies, root directory at cluster 2 with FAT entry = end-of-chain.
/// LFN format: 32-byte slots with attribute 0x0F immediately preceding the
/// matching 8.3 dirent, written in reverse order so the highest-sequence slot
/// is read first; each holds 13 UTF-16LE code units (5+6+2 split) and a
/// checksum of the associated short name.
/// </remarks>
public sealed class FatWriter {
  private readonly List<(string Name, byte[] Data, DateTime? ModTime)> _files = [];
  // Parallel list of streaming inputs (name, size, factory, modTime). When
  // populated, BuildToStreaming() uses these to size the image and stream
  // file bytes from the factory straight into each pre-allocated cluster
  // run — no byte[] materialisation for entry data.
  private readonly List<(string Name, long Size, Func<Stream> Open, DateTime? ModTime)> _streamingFiles = [];

  /// <summary>
  /// Adds a file to the image. Long names (mixed case, > 8.3, non-ASCII,
  /// multiple dots) are written as VFAT/LFN entries with an auto-generated
  /// 8.3 short-name alias. Plain 8.3 names are written as a single dirent.
  /// </summary>
  public void AddFile(string name, byte[] data, DateTime? modTime = null) => _files.Add((name, data, modTime));

  /// <summary>
  /// Adds a streaming file: its <paramref name="size"/> is known up front
  /// (so the writer can plan the cluster geometry), but its bytes are
  /// fetched on demand from <paramref name="openStream"/> during the
  /// second-pass write — never buffered in memory by the writer.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Pair with <see cref="BuildToStreaming"/> to produce a FAT image whose
  /// peak memory cost is bounded by 64 KB regardless of file count or
  /// size. The factory is invoked at most once per file; the returned
  /// stream is disposed after the write.
  /// </para>
  /// </remarks>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream, DateTime? modTime = null) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    _streamingFiles.Add((name, size, openStream, modTime));
  }

  /// <summary>
  /// Builds the FAT filesystem image.
  /// </summary>
  /// <param name="totalSectors">Total sectors (default 2880 = 1.44 MB floppy).</param>
  /// <param name="bytesPerSector">Bytes per sector (default 512).</param>
  /// <param name="requestedClusterSize">Desired cluster size in bytes (0 = auto-select).
  /// Must be a power of two and a multiple of <paramref name="bytesPerSector"/>.
  /// The actual cluster size may differ if the requested value is incompatible
  /// with the resulting FAT type (e.g. a 64 KB cluster on a tiny FAT12 image).</param>
  /// <param name="volumeLabel">Optional volume label (up to 11 chars). Defaults to "NO NAME" when null.
  /// Written into the BPB's BS_VolLab field (11 ASCII bytes, space-padded, uppercase) via
  /// <see cref="BuildVolumeLabelBytes"/>.</param>
  /// <param name="forcedFatType">Forces a specific FAT variant: 0 = auto-select (default), 12 = FAT12,
  /// 16 = FAT16, 32 = FAT32. If the chosen geometry doesn't yield enough data clusters for the forced
  /// type, throws <see cref="InvalidOperationException"/>. FAT16 requires at least 4085 data clusters;
  /// FAT32 requires at least 65525.</param>
  /// <param name="enableLfn">When false, only 8.3 short-name entries are written (strict DOS compatibility).
  /// Names that exceed 8.3 chars are silently truncated to a short alias.</param>
  /// <param name="transactionFat">Mark the image for transaction-based FAT writes (TFAT / Windows CE style).</param>
  /// <param name="requestedRootEntries">Override the root directory entry count for FAT12/16 (0 = use defaults:
  /// 224 for FAT12, 512 for FAT16). DMF distribution disks used 16 to reclaim sectors for data.</param>
  /// <param name="forceLfn">Emit a VFAT long-name entry for every file/dir (with a generated 8.3 alias),
  /// even names that already fit 8.3 — the way Windows always records a long name. Implies <paramref name="enableLfn"/>.</param>
  /// <returns>Complete disk image as byte array.</returns>
  public byte[] Build(int totalSectors = 2880, int bytesPerSector = 512, int requestedClusterSize = 0,
    string? volumeLabel = null, int forcedFatType = 0, bool enableLfn = true, bool transactionFat = false,
    int requestedRootEntries = 0, bool forceLfn = false) {
    if (forceLfn) enableLfn = true; // force-LFN implies VFAT is on
    const int fatCount = 2;

    // Start with FAT12 floppy defaults
    var reservedSectors = 1;
    var sectorsPerCluster = 1;
    var rootEntryCount = 224;
    var fatSize = 9; // sectors per FAT for 1.44MB floppy

    // Apply requested cluster size if valid.
    if (requestedClusterSize > 0 && requestedClusterSize >= bytesPerSector
        && (requestedClusterSize & (requestedClusterSize - 1)) == 0
        && requestedClusterSize % bytesPerSector == 0)
      sectorsPerCluster = requestedClusterSize / bytesPerSector;

    // Determine FAT type — honour the caller's forced choice if any, else auto-select
    // based on the data-cluster count for the chosen geometry.
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = forcedFatType is 12 or 16 or 32
      ? forcedFatType
      : (totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32);

    // Adjust parameters for each FAT type.
    // requestedRootEntries overrides the per-type default (224/512) for FAT12/16.
    // DMF distribution disks used 16 entries; zero means "use the type's default".
    if (fatType == 16) {
      if (requestedClusterSize <= 0) sectorsPerCluster = 4;
      rootEntryCount = requestedRootEntries > 0 ? requestedRootEntries : 512;
      rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      fatSize = (totalSectors * 2 / bytesPerSector) + 1;
      firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    } else if (fatType == 12 && requestedRootEntries > 0) {
      // FAT12 custom root entry count: recompute rootDirSectors and firstDataSector.
      rootEntryCount = requestedRootEntries;
      rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    } else if (fatType == 32) {
      reservedSectors = 32; // FAT32 requires >=1 but convention is 32 (leaves room for FSInfo+BackupBoot)
      rootEntryCount = 0;   // FAT32 root is in the cluster chain, not a fixed area
      rootDirSectors = 0;
      if (requestedClusterSize <= 0) {
        // Sectors-per-cluster heuristic from FATGEN103 table.
        sectorsPerCluster = totalSectors < 66600 ? 1
          : totalSectors < 532480 ? 1      // up to 260 MB, 512-byte clusters ⇒ 1 spc
          : totalSectors < 16777216 ? 8    // up to 8 GB ⇒ 4 KB clusters
          : totalSectors < 33554432 ? 16
          : totalSectors < 67108864 ? 32
          : 64;
      }
      // Estimate FAT size: (data sectors / spc) entries × 4 bytes each, rounded up.
      var dataSectorsEstimate = totalSectors - reservedSectors;
      var dataClustersEstimate = dataSectorsEstimate / sectorsPerCluster;
      fatSize = (dataClustersEstimate * 4 + bytesPerSector - 1) / bytesPerSector;
      firstDataSector = reservedSectors + fatCount * fatSize;
    }

    // Validate forced-type upper-bound constraints after the layout is finalised.
    // Upper bounds (FAT12 < 4085, FAT16 < 65525) are hard rules of the on-disk
    // format — exceeding them produces an image no FAT driver can interpret
    // correctly. The FAT32 lower bound (< 65525 clusters) is a *recommendation*
    // from FATGEN103 §3.5 for auto-detection; when the caller explicitly forces
    // FAT32 we honour their choice (e.g. forced FAT32 on a 1.44 MB floppy still
    // emits a valid FAT32 BPB that our reader and most modern tools accept).
    // The FAT16 lower bound is also a soft hint — we drop it for the same
    // reason: forced FAT16 on a tiny image is a documented retro use-case.
    if (forcedFatType != 0) {
      var finalClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
      if (forcedFatType == 12 && finalClusters >= 4085)
        throw new InvalidOperationException(
          $"FAT12 supports at most 4084 data clusters but this image has {finalClusters}. " +
          "Reduce the image size or increase the cluster size.");
      if (forcedFatType == 16 && finalClusters >= 65525)
        throw new InvalidOperationException(
          $"FAT16 supports at most 65524 data clusters but this image has {finalClusters}. " +
          "Reduce the image size or switch to FAT32.");
    }

    var disk = new byte[(long)totalSectors * bytesPerSector];

    // Build the 11-byte BS_VolLab payload up front — uppercase ASCII, space-padded
    // or truncated to exactly 11 bytes. Empty / null → the legacy "NO NAME    " default.
    var labelBytes = BuildVolumeLabelBytes(volumeLabel);

    // ── Boot sector (shared base) ──────────────────────────────────────────
    if (fatType == 32) { disk[0] = 0xEB; disk[1] = 0x58; disk[2] = 0x90; }
    else { disk[0] = 0xEB; disk[1] = 0x3C; disk[2] = 0x90; }
    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(disk, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), (ushort)bytesPerSector);
    disk[13] = (byte)sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(14), (ushort)reservedSectors);
    disk[16] = (byte)fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(17), (ushort)rootEntryCount);
    if (fatType != 32 && totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(19), (ushort)totalSectors);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(32), (uint)totalSectors);
    disk[21] = 0xF8; // media: fixed / hard disk
    if (fatType != 32)
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(22), (ushort)fatSize);
    // (FAT32 writes fat_size_32 at offset 36 below.)
    // BPB geometry: match the physical layout of the target medium.
    // Floppy geometries use 2 heads; everything else uses 63 spt / 255 heads
    // (the standard hard-disk / USB-stick / optical-image convention).
    var (spt, numHeads) = totalSectors switch {
      320  => (8,  1),   // 5.25" SS/DD 160 KB
      360  => (9,  1),   // 5.25" SS/DD 180 KB
      640  => (8,  2),   // 5.25" DS/DD 320 KB
      720  => (9,  2),   // 5.25" DS/DD 360 KB
      1440 => (9,  2),   // 3.5" DS/DD 720 KB
      2400 => (15, 2),   // 5.25" DS/HD 1.2 MB
      2880 => (18, 2),   // 3.5" DS/HD 1.44 MB
      3360 => (21, 2),   // DMF 1.68 MB
      5760 => (36, 2),   // 3.5" DS/ED 2.88 MB
      _    => (63, 255), // hard disk / USB / optical image
    };
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(24), (ushort)spt);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(26), (ushort)numHeads);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(28), 0u);  // hidden sectors

    // Volume label: up to 11 ASCII chars, space-padded, upper-cased.
    var label = string.IsNullOrWhiteSpace(volumeLabel)
      ? "NO NAME    "
      : volumeLabel.ToUpperInvariant().PadRight(11)[..11];

    if (fatType == 32) {
      // ── FAT32 extended BPB ───────────────────────────────────────────────
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(36), (uint)fatSize);   // BPB_FATSz32
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(40), 0);               // BPB_ExtFlags: mirror
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(42), 0);               // BPB_FSVer: 0.0
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(44), 2u);              // BPB_RootClus: root at cluster 2
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(48), 1);               // BPB_FSInfo: sector 1
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(50), 6);               // BPB_BkBootSec: backup at sector 6
      // 52-63 reserved (already zero)
      disk[64] = 0x80;                                                             // BS_DrvNum
      disk[65] = transactionFat ? (byte)0x01 : (byte)0x00;                        // BS_Reserved1: TFAT marker
      disk[66] = 0x29;                                                             // BS_BootSig: extended BPB present
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(67), 0x12345678u);     // BS_VolID
      labelBytes.CopyTo(disk.AsSpan(71, 11));                                      // BS_VolLab (11 bytes, sanitised)
      Encoding.ASCII.GetBytes("FAT32   ").CopyTo(disk, 82);                        // BS_FilSysType (8 bytes)
    } else {
      // Short extended BPB (FAT12/16)
      disk[36] = 0x80;
      disk[37] = transactionFat ? (byte)0x01 : (byte)0x00;  // BS_Reserved1: TFAT marker
      disk[38] = 0x29;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(39), 0x12345678u);
      labelBytes.CopyTo(disk.AsSpan(43, 11));                                      // BS_VolLab (11 bytes, sanitised)
      Encoding.ASCII.GetBytes(fatType == 12 ? "FAT12   " : "FAT16   ").CopyTo(disk, 54);
    }

    disk[510] = 0x55; disk[511] = 0xAA;

    // ── FAT32 FSInfo sector (sector 1) ───────────────────────────────────
    if (fatType == 32) {
      var fsInfo = 1 * bytesPerSector;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo), 0x41615252u);           // FSI_LeadSig
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 484), 0x61417272u);     // FSI_StrucSig
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 488), 0xFFFFFFFFu);     // FSI_Free_Count = unknown
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 492), 0xFFFFFFFFu);     // FSI_Nxt_Free = unknown
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 508), 0xAA550000u);     // FSI_TrailSig

      // ── Backup boot sector (sector 6) ──────────────────────────────────
      var bkOff = 6 * bytesPerSector;
      Array.Copy(disk, 0, disk, bkOff, bytesPerSector);
      // Backup FSInfo (sector 7)
      var bkFsInfo = 7 * bytesPerSector;
      Array.Copy(disk, fsInfo, disk, bkFsInfo, bytesPerSector);
    }

    // ── FAT initialisation: media byte + EoC markers for clusters 0 and 1 ─
    var fatOffset = reservedSectors * bytesPerSector;
    if (fatType == 12) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF; disk[fatOffset + 2] = 0xFF;
    } else if (fatType == 16) {
      disk[fatOffset] = 0xF8; disk[fatOffset + 1] = 0xFF;
      disk[fatOffset + 2] = 0xFF; disk[fatOffset + 3] = 0xFF;
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset), 0x0FFFFFF8u);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4), 0x0FFFFFFFu);
    }

    // ── Root directory and file data ──────────────────────────────────────
    var clusterSize = sectorsPerCluster * bytesPerSector;

    // Lay out the directory tree: each subdirectory and file gets a contiguous
    // cluster chain in the data area; the FAT12/16 root lives in its fixed
    // region, the FAT32 root in the cluster chain at cluster 2.
    var dataAreaOffset = firstDataSector * bytesPerSector;
    var placement = PlaceTree(BuildTree(), fatType, clusterSize, enableLfn, forceLfn);

    // For FAT12/16 the root directory is a fixed-size region. Overflow would
    // silently write directory entries into the data clusters that follow it,
    // corrupting both the directory and the file data stored there.
    if (fatType != 32 && placement.RootContentBytes > rootEntryCount * 32)
      throw new InvalidOperationException(
        $"FAT{fatType}: the root directory needs {placement.RootContentBytes / 32} entries " +
        $"but holds at most {rootEntryCount}. " +
        $"Pass totalSectors ≥ 200 000 to get a FAT32 image with an unbounded root directory, " +
        $"or use BuildAutoSized() to let the writer choose automatically.");


    // FAT chains for every allocated run (FAT2 is mirrored from FAT1 below).
    foreach (var (start, count) in placement.Runs)
      for (var c = 0; c < count; c++) {
        var cluster = start + c;
        var nextVal = (c + 1 < count)
          ? cluster + 1
          : (fatType == 12 ? 0xFFF : fatType == 16 ? 0xFFFF : 0x0FFFFFFF);
        WriteFatEntry(disk, fatOffset, cluster, nextVal, fatType);
      }

    // FAT12/16 root directory in its fixed region.
    if (placement.RootFixed is { } rootFixed)
      rootFixed.CopyTo(disk.AsSpan((reservedSectors + fatCount * fatSize) * bytesPerSector));

    // Subdirectory contents (incl. the FAT32 root) and file data, each at its
    // first cluster's byte offset.
    foreach (var (start, content) in placement.DataWrites) {
      var clusterOffset = dataAreaOffset + (long)(start - 2) * clusterSize;
      if (content.Length > 0 && clusterOffset + content.Length <= disk.Length)
        Buffer.BlockCopy(content, 0, disk, (int)clusterOffset, content.Length);
    }

    var nextCluster = placement.LastUsedCluster + 1;

    // ── FSInfo accounting (FAT32 only) ───────────────────────────────────
    if (fatType == 32) {
      var fsInfo = 1 * bytesPerSector;
      // Total data clusters in the volume = (DataSec / SectorsPerCluster).
      var dataSec = totalSectors - firstDataSector;
      var totalClusters = (uint)(dataSec / sectorsPerCluster);
      // Used = clusters allocated from 2..nextCluster-1.
      var used = (uint)(nextCluster - 2);
      var free = used <= totalClusters ? totalClusters - used : 0u;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 488), free);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fsInfo + 492), (uint)(nextCluster - 1));
      // Mirror to backup FSInfo at sector 7.
      Array.Copy(disk, fsInfo, disk, 7 * bytesPerSector, bytesPerSector);
    }

    // Copy FAT1 to FAT2
    Buffer.BlockCopy(disk, fatOffset, disk, fatOffset + fatSize * bytesPerSector, fatSize * bytesPerSector);

    return disk;
  }

  /// <summary>
  /// Streams a FAT image to <paramref name="output"/> without ever materialising the
  /// whole volume in memory, enabling images of any size (e.g. multi-TB FAT32).
  /// Requires a writable, seekable stream: free space is left as sparse zeros via
  /// <see cref="Stream.SetLength"/>, so only metadata + actual file data is physically
  /// written. Produces byte-for-byte identical output to <see cref="Build"/> (verified
  /// by parity tests) for any configuration both can express.
  ///
  /// <para>Peak memory is bounded by O(sector + 64&#160;KB FAT chunk + largest file +
  /// largest dirent blob) — independent of total image size.</para>
  /// </summary>
  public void BuildTo(Stream output, int totalSectors = 2880, int bytesPerSector = 512,
    int requestedClusterSize = 0, string? volumeLabel = null, int forcedFatType = 0,
    bool enableLfn = true, bool transactionFat = false, int requestedRootEntries = 0, bool forceLfn = false) {
    if (forceLfn) enableLfn = true; // force-LFN implies VFAT is on
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildTo requires a writable, seekable stream.", nameof(output));

    const int fatCount = 2;
    var reservedSectors = 1;
    var sectorsPerCluster = 1;
    var rootEntryCount = 224;
    var fatSize = 9;

    if (requestedClusterSize > 0 && requestedClusterSize >= bytesPerSector
        && (requestedClusterSize & (requestedClusterSize - 1)) == 0
        && requestedClusterSize % bytesPerSector == 0)
      sectorsPerCluster = requestedClusterSize / bytesPerSector;

    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    var totalDataClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
    var fatType = forcedFatType is 12 or 16 or 32
      ? forcedFatType
      : (totalDataClusters < 4085 ? 12 : totalDataClusters < 65525 ? 16 : 32);

    if (fatType == 16) {
      if (requestedClusterSize <= 0) sectorsPerCluster = 4;
      rootEntryCount = requestedRootEntries > 0 ? requestedRootEntries : 512;
      rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      fatSize = (totalSectors * 2 / bytesPerSector) + 1;
      firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    } else if (fatType == 12 && requestedRootEntries > 0) {
      rootEntryCount = requestedRootEntries;
      rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
      firstDataSector = reservedSectors + fatCount * fatSize + rootDirSectors;
    } else if (fatType == 32) {
      reservedSectors = 32;
      rootEntryCount = 0;
      rootDirSectors = 0;
      if (requestedClusterSize <= 0) {
        sectorsPerCluster = totalSectors < 66600 ? 1
          : totalSectors < 532480 ? 1
          : totalSectors < 16777216 ? 8
          : totalSectors < 33554432 ? 16
          : totalSectors < 67108864 ? 32
          : 64;
      }
      var dataSectorsEstimate = totalSectors - reservedSectors;
      var dataClustersEstimate = dataSectorsEstimate / sectorsPerCluster;
      fatSize = (int)(((long)dataClustersEstimate * 4 + bytesPerSector - 1) / bytesPerSector);
      firstDataSector = reservedSectors + fatCount * fatSize;
    }

    if (forcedFatType != 0) {
      var finalClusters = (totalSectors - firstDataSector) / sectorsPerCluster;
      if (forcedFatType == 12 && finalClusters >= 4085)
        throw new InvalidOperationException(
          $"FAT12 supports at most 4084 data clusters but this image has {finalClusters}. " +
          "Reduce the image size or increase the cluster size.");
      if (forcedFatType == 16 && finalClusters >= 65525)
        throw new InvalidOperationException(
          $"FAT16 supports at most 65524 data clusters but this image has {finalClusters}. " +
          "Reduce the image size or switch to FAT32.");
    }

    var clusterSize = (long)sectorsPerCluster * bytesPerSector;
    var label = string.IsNullOrWhiteSpace(volumeLabel)
      ? "NO NAME    " : volumeLabel.ToUpperInvariant().PadRight(11)[..11];
    var (spt, numHeads) = totalSectors switch {
      320 => (8, 1), 360 => (9, 1), 640 => (8, 2), 720 => (9, 2), 1440 => (9, 2),
      2400 => (15, 2), 2880 => (18, 2), 3360 => (21, 2), 5760 => (36, 2), _ => (63, 255),
    };

    // Lay out the directory tree onto contiguous cluster runs (no image
    // allocation). The same planner drives Build, so output is byte-identical.
    var placement = PlaceTree(BuildTree(), fatType, (int)clusterSize, enableLfn, forceLfn);
    if (fatType != 32 && placement.RootContentBytes > rootEntryCount * 32)
      throw new InvalidOperationException(
        $"FAT{fatType}: the root directory needs {placement.RootContentBytes / 32} entries " +
        $"but holds at most {rootEntryCount}.");

    var lastUsedCluster = placement.LastUsedCluster;
    var nextCluster = lastUsedCluster + 1;          // for FSInfo accounting
    var runEnds = new HashSet<int>();
    foreach (var (start, count) in placement.Runs) runEnds.Add(start + count - 1);

    // ── Lay out the physical image sparsely ──────────────────────────────
    var totalBytes = (long)totalSectors * bytesPerSector;
    output.SetLength(totalBytes); // free space becomes sparse zeros

    // 1. Boot sector (+ FAT32 extended BPB).
    var boot = new byte[bytesPerSector];
    if (fatType == 32) { boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90; }
    else { boot[0] = 0xEB; boot[1] = 0x3C; boot[2] = 0x90; }
    Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(boot, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11), (ushort)bytesPerSector);
    boot[13] = (byte)sectorsPerCluster;
    BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14), (ushort)reservedSectors);
    boot[16] = (byte)fatCount;
    BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(17), (ushort)rootEntryCount);
    if (fatType != 32 && totalSectors < 65536)
      BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19), (ushort)totalSectors);
    else
      BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), (uint)totalSectors);
    boot[21] = 0xF8;
    if (fatType != 32)
      BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(22), (ushort)fatSize);
    BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(24), (ushort)spt);
    BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(26), (ushort)numHeads);
    if (fatType == 32) {
      BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36), (uint)fatSize);
      BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44), 2u);
      BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(48), 1);
      BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(50), 6);
      boot[64] = 0x80;
      boot[65] = transactionFat ? (byte)0x01 : (byte)0x00;
      boot[66] = 0x29;
      BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(67), 0x12345678u);
      Encoding.ASCII.GetBytes(label).CopyTo(boot, 71);
      Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82);
    } else {
      boot[36] = 0x80;
      boot[37] = transactionFat ? (byte)0x01 : (byte)0x00;
      boot[38] = 0x29;
      BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(39), 0x12345678u);
      Encoding.ASCII.GetBytes(label).CopyTo(boot, 43);
      Encoding.ASCII.GetBytes(fatType == 12 ? "FAT12   " : "FAT16   ").CopyTo(boot, 54);
    }
    boot[510] = 0x55; boot[511] = 0xAA;
    output.Position = 0;
    output.Write(boot);

    // 2. FAT32 FSInfo (sector 1) + backup boot (6) + backup FSInfo (7).
    if (fatType == 32) {
      var fsi = new byte[bytesPerSector];
      BinaryPrimitives.WriteUInt32LittleEndian(fsi.AsSpan(0), 0x41615252u);
      BinaryPrimitives.WriteUInt32LittleEndian(fsi.AsSpan(484), 0x61417272u);
      var dataSec = totalSectors - firstDataSector;
      var totalClusters = (uint)(dataSec / sectorsPerCluster);
      var used = (uint)(nextCluster - 2);
      var freeC = used <= totalClusters ? totalClusters - used : 0u;
      BinaryPrimitives.WriteUInt32LittleEndian(fsi.AsSpan(488), freeC);
      BinaryPrimitives.WriteUInt32LittleEndian(fsi.AsSpan(492), (uint)(nextCluster - 1));
      BinaryPrimitives.WriteUInt32LittleEndian(fsi.AsSpan(508), 0xAA550000u);
      output.Position = 1L * bytesPerSector; output.Write(fsi);
      output.Position = 6L * bytesPerSector; output.Write(boot);
      output.Position = 7L * bytesPerSector; output.Write(fsi);
    }

    // 3. FAT tables (×2). FAT12/16 are tiny → build in memory. FAT32 → stream in chunks.
    var fatByteOffset1 = (long)reservedSectors * bytesPerSector;
    var fatByteLen = (long)fatSize * bytesPerSector;
    if (fatType == 32) {
      WriteFat32Streaming(output, fatByteOffset1, fatByteLen, lastUsedCluster, runEnds, bytesPerSector);
      WriteFat32Streaming(output, fatByteOffset1 + fatByteLen, fatByteLen, lastUsedCluster, runEnds, bytesPerSector);
    } else {
      var fat = new byte[fatByteLen];
      if (fatType == 12) { fat[0] = 0xF8; fat[1] = 0xFF; fat[2] = 0xFF; }
      else { fat[0] = 0xF8; fat[1] = 0xFF; fat[2] = 0xFF; fat[3] = 0xFF; }
      foreach (var (start, count) in placement.Runs)
        for (var c = 0; c < count; c++) {
          var cluster = start + c;
          var nextVal = c + 1 < count ? cluster + 1 : (fatType == 12 ? 0xFFF : 0xFFFF);
          WriteFatEntryBuf(fat, cluster, nextVal, fatType);
        }
      output.Position = fatByteOffset1; output.Write(fat);
      output.Position = fatByteOffset1 + fatByteLen; output.Write(fat);
    }

    // 4. FAT12/16 root directory in its fixed region.
    var dataAreaOffset = (long)firstDataSector * bytesPerSector;
    if (placement.RootFixed is { } rootFixed) {
      output.Position = (long)(reservedSectors + fatCount * fatSize) * bytesPerSector;
      output.Write(rootFixed);
    }

    // 5. Subdirectory contents (incl. the FAT32 root) and file data — seek to
    // each object's first cluster and write its bytes.
    foreach (var (start, content) in placement.DataWrites) {
      if (content.Length == 0) continue;
      output.Position = dataAreaOffset + (start - 2) * clusterSize;
      output.Write(content);
    }

    output.Flush();
  }

  /// <summary>Streams one FAT32 table to <paramref name="output"/> at <paramref name="offset"/>
  /// using contiguous-allocation knowledge: entry(c) = c+1 unless c is a run-end (→ EOC),
  /// with markers at 0/1 and zeros past the last used cluster. Never allocates the whole table.</summary>
  private static void WriteFat32Streaming(
      Stream output, long offset, long fatByteLen, int lastUsedCluster,
      HashSet<int> runEnds, int bytesPerSector) {
    const int chunkBytes = 64 * 1024;
    var chunk = new byte[chunkBytes];
    var entriesPerChunk = chunkBytes / 4;
    var totalEntries = fatByteLen / 4;
    output.Position = offset;
    long entry = 0;
    while (entry < totalEntries) {
      var n = (int)Math.Min(entriesPerChunk, totalEntries - entry);
      var span = chunk.AsSpan(0, n * 4);
      span.Clear();
      for (var j = 0; j < n; j++) {
        var c = (int)(entry + j);
        uint val;
        if (c == 0) val = 0x0FFFFFF8u;
        else if (c == 1) val = 0x0FFFFFFFu;
        else if (c > lastUsedCluster) val = 0u;
        else val = runEnds.Contains(c) ? 0x0FFFFFFFu : (uint)(c + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(j * 4)..], val);
      }
      output.Write(span);
      entry += n;
    }
  }

  /// <summary>Writes a single FAT12/16 entry into an in-memory table buffer.</summary>
  private static void WriteFatEntryBuf(byte[] fat, int cluster, int value, int fatType) {
    if (fatType == 12) {
      var bytePos = cluster * 3 / 2;
      if (bytePos + 1 >= fat.Length) return;
      if ((cluster & 1) == 0) {
        fat[bytePos] = (byte)(value & 0xFF);
        fat[bytePos + 1] = (byte)((fat[bytePos + 1] & 0xF0) | ((value >> 8) & 0x0F));
      } else {
        fat[bytePos] = (byte)((fat[bytePos] & 0x0F) | ((value << 4) & 0xF0));
        fat[bytePos + 1] = (byte)((value >> 4) & 0xFF);
      }
    } else { // FAT16
      var pos = cluster * 2;
      if (pos + 2 <= fat.Length)
        BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(pos), (ushort)value);
    }
  }

  // ── VFAT/LFN encoding ────────────────────────────────────────────────
  //
  // For each input filename we produce a contiguous byte buffer of dirent
  // slots: zero or more 32-byte LFN slots followed by exactly one 32-byte
  // 8.3 short-name entry. The 8.3 portion is left with placeholder zeroes
  // for first-cluster / file-size — the caller patches those once the data
  // location is known.

  /// <summary>Returns true if <paramref name="name"/> can be represented
  /// in pure 8.3 (≤ 8 chars base, ≤ 3 chars ext, single dot, no spaces,
  /// no LFN-only chars). Both uppercase and lowercase ASCII letters
  /// qualify — when the base and/or extension is uniformly lowercase,
  /// the NT case bits at byte 12 of the dirent preserve case without
  /// needing LFN slots; mixed case in the same component still forces
  /// LFN.</summary>
  private static bool IsPlain8Dot3(string name) {
    var dotIdx = name.LastIndexOf('.');
    var basePart = dotIdx >= 0 ? name[..dotIdx] : name;
    var extPart = dotIdx >= 0 ? name[(dotIdx + 1)..] : "";
    if (basePart.Length is 0 or > 8) return false;
    if (extPart.Length > 3) return false;
    // Disallow secondary dots in the base — that always requires LFN.
    if (basePart.Contains('.')) return false;
    if (!IsPlain8Dot3Component(basePart)) return false;
    if (!IsPlain8Dot3Component(extPart)) return false;
    // NT case bits can encode "all uppercase" or "all lowercase" per
    // component, but NOT mixed case — mixed case forces LFN to preserve
    // the user's exact spelling.
    if (HasMixedCaseAscii(basePart)) return false;
    if (HasMixedCaseAscii(extPart)) return false;
    return true;
  }

  /// <summary>A single 8.3 base or extension component is plain-encodable
  /// when every character is a valid uppercase 8.3 char, an ASCII digit/
  /// punct/etc., or a lowercase ASCII letter (which the NT case bits will
  /// preserve).</summary>
  private static bool IsPlain8Dot3Component(string component) {
    foreach (var c in component) {
      if (c is >= 'a' and <= 'z') continue;
      if (!Is83Char(c)) return false;
    }
    return true;
  }

  /// <summary>Returns true if the component mixes uppercase and lowercase
  /// ASCII letters — mixed case can't be encoded with a single NT case
  /// bit, so the writer must fall back to LFN to preserve user case.</summary>
  private static bool HasMixedCaseAscii(string component) {
    var hasUpper = false;
    var hasLower = false;
    foreach (var c in component) {
      if (c is >= 'A' and <= 'Z') hasUpper = true;
      else if (c is >= 'a' and <= 'z') hasLower = true;
      if (hasUpper && hasLower) return true;
    }
    return false;
  }

  /// <summary>Returns true if the component contains at least one lowercase
  /// ASCII letter (so the writer should set the corresponding NT case bit).</summary>
  private static bool HasLowerCaseAscii(string component) {
    foreach (var c in component)
      if (c is >= 'a' and <= 'z') return true;
    return false;
  }

  /// <summary>Characters allowed in a raw 8.3 entry per FATGEN103 §6.1.
  /// Uppercase ASCII, digits, and a small punctuation set. Lowercase
  /// letters force LFN to preserve case (DOS uppercases on display but
  /// VFAT preserves user case via the long name).</summary>
  private static bool Is83Char(char c) =>
    c is >= 'A' and <= 'Z'
    or >= '0' and <= '9'
    or '_' or '-' or '$' or '%' or '\'' or '@' or '~' or '`' or '!'
    or '(' or ')' or '{' or '}' or '^' or '#' or '&';

  /// <summary>Sanitises a single character for the 8.3 alias: uppercase
  /// ASCII, digits, and underscore-substitute everything else.</summary>
  private static char SanitizeForShort(char c) {
    if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
    if (c is >= 'a' and <= 'z') return (char)(c - 32);
    return Is83Char(c) ? c : '_';
  }

  /// <summary>Generates an 8.3 alias for a long filename per the VFAT
  /// algorithm: uppercase, drop spaces and dots from the base, replace
  /// disallowed chars with underscore, truncate the base to 6 chars and
  /// append <c>~N</c> if collisions or truncation occurred.</summary>
  private static string GenerateShortName(string longName, HashSet<string> existing) {
    var lastDot = longName.LastIndexOf('.');
    var rawBase = lastDot > 0 ? longName[..lastDot] : longName;
    var rawExt = lastDot > 0 ? longName[(lastDot + 1)..] : "";

    var basePart = new StringBuilder();
    var lossy = false;
    foreach (var c in rawBase) {
      if (c is ' ' or '.') { lossy = true; continue; }
      if (Is83Char(char.ToUpperInvariant(c))) basePart.Append(char.ToUpperInvariant(c));
      else if (c is >= 'a' and <= 'z') { basePart.Append((char)(c - 32)); lossy = true; }
      else { basePart.Append('_'); lossy = true; }
    }
    if (basePart.Length == 0) basePart.Append("FILE");

    var extPart = new StringBuilder();
    foreach (var c in rawExt) {
      if (extPart.Length >= 3) { lossy = true; break; }
      if (c is ' ' or '.') { lossy = true; continue; }
      if (Is83Char(char.ToUpperInvariant(c))) extPart.Append(char.ToUpperInvariant(c));
      else { extPart.Append('_'); lossy = true; }
    }

    // Truncate base to 6 chars and append ~N when a long name collapses;
    // also when a name was truncated above 8 chars; also when we already
    // have a colliding short name from a previous file.
    var needsTilde = lossy || basePart.Length > 8 || rawBase.Length > 8;
    if (needsTilde) {
      var head = basePart.ToString();
      if (head.Length > 6) head = head[..6];
      for (var n = 1; n < 1_000_000; n++) {
        var candidate = $"{head}~{n}";
        if (extPart.Length > 0) candidate += "." + extPart;
        if (existing.Add(candidate)) return candidate;
      }
      throw new InvalidOperationException("FAT: unable to generate unique 8.3 short name.");
    }

    var simple = basePart.ToString();
    if (extPart.Length > 0) simple += "." + extPart;
    if (!existing.Add(simple)) {
      // Plain-8.3 collision (case-insensitive): fall back to ~N too.
      var head = basePart.Length > 6 ? basePart.ToString(0, 6) : basePart.ToString();
      for (var n = 1; n < 1_000_000; n++) {
        var candidate = $"{head}~{n}";
        if (extPart.Length > 0) candidate += "." + extPart;
        if (existing.Add(candidate)) return candidate;
      }
    }
    return simple;
  }

  /// <summary>FAT/VFAT short-name checksum (FATGEN103 §6.4): unsigned
  /// rotate-right-with-add over the 11 raw 8.3 bytes. Stored in every LFN
  /// slot so a corrupt or out-of-order slot can be detected.</summary>
  private static byte LfnChecksum(ReadOnlySpan<byte> short11) {
    byte sum = 0;
    for (var i = 0; i < 11; i++)
      sum = (byte)((((sum & 1) != 0 ? 0x80 : 0) + (sum >> 1) + short11[i]) & 0xFF);
    return sum;
  }

  /// <summary>Builds the 32-byte raw 8.3 directory entry (offset 0..31)
  /// for a short name like <c>"HELLO   TXT"</c> (already padded). Caller
  /// fills first-cluster + size fields later. The optional
  /// <paramref name="ntCaseBits"/> byte at offset 12 encodes the
  /// VFAT/NTFS case-preservation flags: bit 3 (0x08) = base is lowercase,
  /// bit 4 (0x10) = extension is lowercase. The 11-byte name field always
  /// stores uppercase; the reader applies the case bits on display.</summary>
  private static byte[] BuildShortEntry(string shortName, byte ntCaseBits = 0, DateTime? modTime = null, byte attr = 0x20) {
    var entry = new byte[32];
    var dotIdx = shortName.LastIndexOf('.');
    var basePart = dotIdx >= 0 ? shortName[..dotIdx] : shortName;
    var extPart = dotIdx >= 0 ? shortName[(dotIdx + 1)..] : "";
    var basePad = basePart.ToUpperInvariant().PadRight(8).Substring(0, 8);
    var extPad = extPart.ToUpperInvariant().PadRight(3).Substring(0, 3);
    Encoding.ASCII.GetBytes(basePad).CopyTo(entry, 0);
    Encoding.ASCII.GetBytes(extPad).CopyTo(entry, 8);
    entry[11] = attr; // 0x20 archive (file) or 0x10 directory
    entry[12] = ntCaseBits;

    // FAT timestamps: clamp to the representable range [1980-01-01, 2107-12-31].
    var dt = modTime ?? DateTime.Now;
    if (dt.Year < 1980) dt = new DateTime(1980, 1, 1);
    else if (dt.Year > 2107) dt = new DateTime(2107, 12, 31, 23, 59, 58);
    var fatDate = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
    var fatTime = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(14), fatTime);  // creation time
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(16), fatDate);  // creation date
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(18), fatDate);  // last-access date
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(22), fatTime);  // write time
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(24), fatDate);  // write date

    return entry;
  }

  /// <summary>Builds the slot blob for one file (LFN entries first if the
  /// long name needs them, then the 8.3 entry). Updates <paramref
  /// name="existingShortNames"/> with the chosen alias to detect ~N
  /// collisions across subsequent files.</summary>
  private static byte[] BuildDirentSlots(string longName, HashSet<string> existingShortNames, DateTime? modTime = null, bool enableLfn = true, byte attr = 0x20, bool forceLfn = false) {
    if (!enableLfn) {
      // Strict 8.3 mode: no LFN slots, just generate a short-name alias.
      var shortAlias = IsPlain8Dot3(longName)
        ? longName.ToUpperInvariant()
        : GenerateShortName(longName, existingShortNames);
      existingShortNames.Add(shortAlias.ToUpperInvariant());
      return BuildShortEntry(shortAlias, 0, modTime, attr);
    }
    // forceLfn emits an LFN entry set for every name (Windows-style), even one
    // that already fits 8.3 — so the falls-through-to-LFN path below runs for
    // all names. Otherwise plain 8.3 names use a single dirent with NT case bits.
    if (!forceLfn && IsPlain8Dot3(longName)) {
      existingShortNames.Add(longName.ToUpperInvariant());
      // Compute NT case bits so the reader can restore lower-case spelling
      // without needing LFN slots — bit 3 = base is lowercase, bit 4 =
      // extension is lowercase.
      var dotIdx = longName.LastIndexOf('.');
      var basePart = dotIdx >= 0 ? longName[..dotIdx] : longName;
      var extPart = dotIdx >= 0 ? longName[(dotIdx + 1)..] : "";
      byte caseBits = 0;
      if (HasLowerCaseAscii(basePart)) caseBits |= 0x08;
      if (HasLowerCaseAscii(extPart)) caseBits |= 0x10;
      return BuildShortEntry(longName.ToUpperInvariant(), caseBits, modTime, attr);
    }

    var shortName = GenerateShortName(longName, existingShortNames);
    var shortEntry = BuildShortEntry(shortName, 0, modTime, attr);
    var checksum = LfnChecksum(shortEntry.AsSpan(0, 11));

    // Each LFN slot carries 13 UTF-16 units: pad with NUL (after the real
    // name) plus 0xFFFF for unused trailing slots, per the spec.
    var fragments = (longName.Length + 13) / 13; // include space for trailing NUL
    if (fragments < 1) fragments = 1;
    if (fragments > 20)
      throw new InvalidOperationException("FAT: long name exceeds 255 UTF-16 chars.");

    var blob = new byte[fragments * 32 + 32];
    // Slot N (highest sequence) goes first on disk; per FAT spec it's
    // marked with the 0x40 "last-LFN" flag.
    for (var slotIdx = 0; slotIdx < fragments; slotIdx++) {
      var seq = fragments - slotIdx; // LDIR_Ord 1..N reading on-disk
      var firstChar = (seq - 1) * 13;
      var slotOffset = slotIdx * 32;

      blob[slotOffset + 0] = (byte)(seq | (slotIdx == 0 ? 0x40 : 0));
      blob[slotOffset + 11] = 0x0F;  // attribute: LFN
      blob[slotOffset + 12] = 0;     // type
      blob[slotOffset + 13] = checksum;
      // FstClusLO at offset 26 stays zero per spec.

      // Layout: 5 chars at [1..10], 6 chars at [14..25], 2 chars at [28..31].
      WriteLfnChars(blob, slotOffset + 1, 5, longName, firstChar);
      WriteLfnChars(blob, slotOffset + 14, 6, longName, firstChar + 5);
      WriteLfnChars(blob, slotOffset + 28, 2, longName, firstChar + 11);
    }
    shortEntry.CopyTo(blob, fragments * 32);
    return blob;
  }

  /// <summary>Writes <paramref name="count"/> UTF-16LE chars from
  /// <paramref name="name"/> starting at <paramref name="firstChar"/>;
  /// the first out-of-range char is encoded as NUL (0x0000), every
  /// subsequent slot position is padded with 0xFFFF.</summary>
  private static void WriteLfnChars(byte[] blob, int offset, int count, string name, int firstChar) {
    var pastEnd = false;
    for (var j = 0; j < count; j++) {
      var idx = firstChar + j;
      ushort code;
      if (pastEnd) {
        code = 0xFFFF;
      } else if (idx < name.Length) {
        code = name[idx];
      } else if (idx == name.Length) {
        code = 0x0000;
        pastEnd = true;
      } else {
        code = 0xFFFF;
      }
      BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(offset + j * 2), code);
    }
  }

  // ── Directory tree ───────────────────────────────────────────────────
  //
  // AddFile names may contain path separators ('/' or '\'). Rather than
  // flattening every file into the root directory — which both loses the
  // structure and overflows the fixed FAT12/16 root (forcing FAT32 and a
  // needlessly large image) — we build a tree, give each subdirectory its
  // own cluster chain in the data area, and write proper '.'/'..' entries.

  private sealed class DirNode(string name) {
    public string Name { get; } = name;
    public List<DirNode> Dirs { get; } = [];
    public List<FileNode> Files { get; } = [];
    private Dictionary<string, DirNode> Index { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int StartCluster { get; set; }
    public int ClusterCount { get; set; }
    /// <summary>One entry per child (files first, then subdirs): the raw dirent
    /// slot bytes plus the child it points to, so the caller can patch the
    /// start-cluster field once allocation is known.</summary>
    public List<(byte[] Slots, FileNode? File, DirNode? Dir)> ChildSlots { get; } = [];
    public DirNode GetOrAddDir(string childName) {
      if (Index.TryGetValue(childName, out var existing)) return existing;
      var created = new DirNode(childName);
      Index[childName] = created;
      Dirs.Add(created);
      return created;
    }
  }

  private sealed class FileNode(string name, byte[] data, DateTime? modTime) {
    public string Name { get; } = name;
    public byte[] Data { get; } = data;
    public DateTime? ModTime { get; } = modTime;
    public int StartCluster { get; set; }
    public int ClusterCount { get; set; }
    /// <summary>When non-null, this node represents a streaming input: its
    /// bytes come from <see cref="StreamOpener"/> on demand and the
    /// in-memory <see cref="Data"/> array stays empty.</summary>
    public long? StreamingSize { get; init; }
    public Func<Stream>? StreamOpener { get; init; }
    public long EffectiveLength => this.StreamingSize ?? this.Data.Length;
  }

  /// <summary>Splits each added file's name on path separators and inserts it
  /// into a directory tree rooted at the (anonymous) volume root. Streaming
  /// files (added via <see cref="AddStreamingFile"/>) are inserted alongside
  /// in-memory files, carrying their byte size for layout planning but
  /// deferring data materialisation to the write phase.</summary>
  private DirNode BuildTree() {
    var root = new DirNode("");
    foreach (var (name, data, modTime) in _files) {
      var parts = name.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var dir = root;
      for (var i = 0; i < parts.Length - 1; i++) dir = dir.GetOrAddDir(parts[i]);
      dir.Files.Add(new FileNode(parts[^1], data, modTime));
    }
    foreach (var (name, size, opener, modTime) in _streamingFiles) {
      var parts = name.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var dir = root;
      for (var i = 0; i < parts.Length - 1; i++) dir = dir.GetOrAddDir(parts[i]);
      dir.Files.Add(new FileNode(parts[^1], System.Array.Empty<byte>(), modTime) {
        StreamingSize = size,
        StreamOpener = opener,
      });
    }
    return root;
  }

  /// <summary>Builds the child-entry slot blobs for every directory in the
  /// tree. Short-name uniqueness is scoped per directory (FAT requires unique
  /// 8.3 names only within a single directory). Files are listed before
  /// subdirectories. Leaves the start-cluster/size fields as placeholders.</summary>
  private static void BuildSlots(DirNode dir, bool enableLfn, bool forceLfn = false) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in dir.Files)
      dir.ChildSlots.Add((BuildDirentSlots(f.Name, names, f.ModTime, enableLfn, 0x20, forceLfn), f, null));
    foreach (var d in dir.Dirs)
      dir.ChildSlots.Add((BuildDirentSlots(d.Name, names, null, enableLfn, 0x10, forceLfn), null, d));
    foreach (var d in dir.Dirs) BuildSlots(d, enableLfn, forceLfn);
  }

  /// <summary>Byte length of a directory's on-disk content: '.'/'..' (64 bytes,
  /// non-root only) plus every child's dirent slots.</summary>
  private static int ContentLength(DirNode dir, bool isRoot) {
    var n = isRoot ? 0 : 64;
    foreach (var (slots, _, _) in dir.ChildSlots) n += slots.Length;
    return n;
  }

  /// <summary>Builds the raw 32-byte '.' or '..' directory entry pointing at
  /// <paramref name="startCluster"/> (0 when the parent is the root, per the
  /// FAT spec). Marked ATTR_DIRECTORY with zero size.</summary>
  private static byte[] BuildDotEntry(bool parent, int startCluster, int fatType, DateTime? modTime) {
    var e = new byte[32];
    e[0] = (byte)'.';
    for (var i = 1; i < 11; i++) e[i] = (byte)' ';
    if (parent) e[1] = (byte)'.';
    e[11] = 0x10; // ATTR_DIRECTORY
    var dt = modTime ?? DateTime.Now;
    if (dt.Year < 1980) dt = new DateTime(1980, 1, 1);
    else if (dt.Year > 2107) dt = new DateTime(2107, 12, 31, 23, 59, 58);
    var fatDate = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
    var fatTime = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(14), fatTime);
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(16), fatDate);
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(18), fatDate);
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(22), fatTime);
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(24), fatDate);
    if (fatType == 32)
      BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(20), (ushort)((startCluster >> 16) & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(26), (ushort)(startCluster & 0xFFFF));
    return e;
  }

  /// <summary>Result of laying out the directory tree onto a cluster space:
  /// the FAT chains (contiguous runs), the byte blobs to drop into the data
  /// area (subdirectories + file data), and — for FAT12/16 — the fixed-region
  /// root directory content.</summary>
  private sealed class Placement {
    public byte[]? RootFixed { get; set; }                              // FAT12/16 root dirents
    public List<(int Start, byte[] Content)> DataWrites { get; } = [];  // subdirs (& FAT32 root) + file data
    public List<(int Start, int Count)> Runs { get; } = [];             // contiguous cluster chains
    public int LastUsedCluster { get; set; }
    public int RootContentBytes { get; set; }                           // for the FAT12/16 root-overflow check
  }

  /// <summary>Allocates contiguous cluster runs for every subdirectory and
  /// file, fills in their content (patching child start-cluster/size fields
  /// and writing '.'/'..'), and returns a <see cref="Placement"/> that both
  /// <see cref="Build"/> and <see cref="BuildTo"/> render identically.</summary>
  private static Placement PlaceTree(DirNode root, int fatType, int clusterSize, bool enableLfn, bool forceLfn = false) {
    BuildSlots(root, enableLfn, forceLfn);
    var p = new Placement { RootContentBytes = ContentLength(root, true) };

    // The root either occupies a fixed region (FAT12/16) or a cluster chain at
    // cluster 2 (FAT32). Everything else is allocated after it.
    var nextCluster = 2;
    if (fatType == 32) {
      var rootClusters = Math.Max(1, (p.RootContentBytes + clusterSize - 1) / clusterSize);
      root.StartCluster = 2;
      root.ClusterCount = rootClusters;
      p.Runs.Add((2, rootClusters));
      nextCluster = 2 + rootClusters;
    } else {
      root.StartCluster = 0; // fixed root region; FAT entry/cluster 0 means "root"
    }

    void Allocate(DirNode dir) {
      foreach (var f in dir.Files) {
        var len = f.EffectiveLength;
        f.ClusterCount = Math.Max(1, (int)((len + clusterSize - 1) / clusterSize));
        f.StartCluster = nextCluster;
        p.Runs.Add((nextCluster, f.ClusterCount));
        nextCluster += f.ClusterCount;
      }
      foreach (var d in dir.Dirs) {
        d.ClusterCount = Math.Max(1, (ContentLength(d, false) + clusterSize - 1) / clusterSize);
        d.StartCluster = nextCluster;
        p.Runs.Add((nextCluster, d.ClusterCount));
        nextCluster += d.ClusterCount;
      }
      foreach (var d in dir.Dirs) Allocate(d);
    }
    Allocate(root);
    p.LastUsedCluster = nextCluster - 1;

    void Fill(DirNode dir, int parentStart, bool isRoot) {
      var content = new byte[ContentLength(dir, isRoot)];
      var pos = 0;
      if (!isRoot) {
        BuildDotEntry(false, dir.StartCluster, fatType, null).CopyTo(content, pos); pos += 32;
        BuildDotEntry(true, parentStart, fatType, null).CopyTo(content, pos); pos += 32;
      }
      foreach (var (slots, file, sub) in dir.ChildSlots) {
        var sn = slots.AsSpan(slots.Length - 32, 32);
        var start = file is not null ? file.StartCluster : sub!.StartCluster;
        var size = file?.EffectiveLength ?? 0;
        if (fatType == 32)
          BinaryPrimitives.WriteUInt16LittleEndian(sn[20..], (ushort)((start >> 16) & 0xFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(sn[26..], (ushort)(start & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(sn[28..], (uint)size);
        slots.CopyTo(content, pos); pos += slots.Length;
      }
      if (isRoot && fatType != 32) p.RootFixed = content;
      else p.DataWrites.Add((dir.StartCluster, content));

      // Only emit in-memory file data into the placement. Streaming files
      // are written by BuildToStreaming() directly into their pre-allocated
      // cluster runs — the placement carries their start cluster + size,
      // not their bytes.
      foreach (var f in dir.Files)
        if (f.StreamingSize == null && f.Data.Length > 0)
          p.DataWrites.Add((f.StartCluster, f.Data));
      foreach (var d in dir.Dirs) Fill(d, isRoot ? 0 : dir.StartCluster, false);
    }
    Fill(root, 0, true);

    return p;
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 computes layout from
  /// <see cref="AddStreamingFile"/> sizes, pass 2 writes boot/FAT/root
  /// metadata then streams each entry's bytes from its
  /// <c>Func&lt;Stream&gt;</c> factory straight into its allocated cluster
  /// run via 64 KB chunks. Peak memory cost is bounded by
  /// (cluster_size + dirent_blob + 64 KB) — independent of total image
  /// size or per-file size.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Cluster tail past each file's exact <c>Size</c> stays zero (the
  /// sparse <see cref="Stream.SetLength"/>-backed output never sees those
  /// bytes). Bounded source streams guarantee no slack-byte leakage from
  /// the read side; the writer's exact-byte-count copy guarantees no
  /// excess from the write side.
  /// </para>
  /// </remarks>
  public void BuildToStreaming(Stream output, int bytesPerSector = 512, int requestedClusterSize = 0,
    string? volumeLabel = null, int forcedFatType = 0, bool enableLfn = true, bool transactionFat = false,
    int requestedRootEntries = 0, bool forceLfn = false) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    if (forceLfn) enableLfn = true;
    // Pass 1: lay out the tree using the size-only streaming inputs so we
    // know cluster size, FAT type, total sector count etc. up front.
    var tree = BuildTree();
    BuildSlots(tree, enableLfn, forceLfn);
    var rootDirentBytes = ContentLength(tree, isRoot: true);
    var fileSizes = new List<long>();
    var dirContentBytes = new List<long>();
    void CollectSizes(DirNode dir, bool isRoot) {
      if (!isRoot) dirContentBytes.Add(ContentLength(dir, isRoot: false));
      foreach (var f in dir.Files) fileSizes.Add(f.EffectiveLength);
      foreach (var sub in dir.Dirs) CollectSizes(sub, false);
    }
    CollectSizes(tree, true);

    var clusterBytes = requestedClusterSize > 0
      ? requestedClusterSize
      : SelectOptimalClusterSize(fileSizes, bytesPerSector, forcedFatType, requestedRootEntries);

    long RoundUpToCluster(long bytes) => bytes <= 0 ? 0 : ((bytes + clusterBytes - 1) / clusterBytes) * clusterBytes;
    var clusterAlignedFiles = fileSizes.Sum(s => RoundUpToCluster(s));
    var clusterAlignedDirs = dirContentBytes.Sum(b => Math.Max((long)clusterBytes, RoundUpToCluster(b)));
    var neededBytes = clusterAlignedFiles + clusterAlignedDirs + 8L * clusterBytes + 65536;
    var totalSectors = Math.Max(32, (int)((neededBytes + bytesPerSector - 1) / bytesPerSector));

    var maxRootEntries = requestedRootEntries > 0 ? requestedRootEntries : 224;
    var needFat32 = forcedFatType == 32
      || (forcedFatType == 0 && rootDirentBytes > maxRootEntries * 32);
    if (needFat32) {
      const long fat32MinClusters = 65525;
      const long margin = 2048;
      const int reservedSectors = 32;
      var spc = Math.Max(1, clusterBytes / bytesPerSector);
      var dataClusters = fileSizes.Sum(s => s <= 0 ? 0L : (s + clusterBytes - 1) / clusterBytes)
        + dirContentBytes.Sum(b => Math.Max(1L, (b + clusterBytes - 1) / clusterBytes));
      var rootClusters = Math.Max(1L, (rootDirentBytes + clusterBytes - 1) / clusterBytes);
      var targetClusters = Math.Max(fat32MinClusters + margin, dataClusters + rootClusters + margin);
      var fatSectors = (targetClusters * 4 + bytesPerSector - 1) / bytesPerSector;
      var fat32Sectors = reservedSectors + 2 * fatSectors + targetClusters * spc;
      totalSectors = Math.Max(totalSectors, (int)fat32Sectors);
    }

    // Pass 2: delegate the metadata write to BuildTo. Because PlaceTree
    // skips streaming files in p.DataWrites, the underlying clusters are
    // left as sparse zeros — no per-file data went through the placement.
    BuildTo(output, totalSectors, bytesPerSector, clusterBytes, volumeLabel,
            forcedFatType, enableLfn, transactionFat, requestedRootEntries, forceLfn);

    // Read back the BPB to discover the actual on-disk geometry that
    // BuildTo committed, then re-derive the placement against it so each
    // streaming file's StartCluster lines up with the FAT chain that was
    // already written. Same inputs + same geometry ⇒ identical placement.
    var actualBpb = ReadBpbForStreaming(output);
    var dataAreaOffset = (long)actualBpb.FirstDataSector * actualBpb.BytesPerSector;
    var actualClusterBytes = (long)actualBpb.SectorsPerCluster * actualBpb.BytesPerSector;
    var streamingTree = BuildTree();
    _ = PlaceTree(streamingTree, actualBpb.FatType, (int)actualClusterBytes, enableLfn, forceLfn);

    // Walk the freshly-placed tree and copy each streaming file's bytes
    // straight into the data area at its allocated cluster offset.
    void EmitStreamingFiles(DirNode dir) {
      foreach (var f in dir.Files) {
        if (f.StreamingSize == null || f.StreamOpener == null) continue;
        if (f.StreamingSize.Value <= 0) continue;
        var clusterOffset = dataAreaOffset + (long)(f.StartCluster - 2) * actualClusterBytes;
        output.Position = clusterOffset;
        using var src = f.StreamOpener();
        long copied = 0;
        var buf = new byte[64 * 1024];
        var remaining = f.StreamingSize.Value;
        while (copied < remaining) {
          var want = (int)Math.Min(buf.Length, remaining - copied);
          var n = src.Read(buf, 0, want);
          if (n <= 0) break;
          output.Write(buf, 0, n);
          copied += n;
        }
        // Source ended at or before remaining; remaining cluster tail
        // stays zero (sparse). The bound on src guarantees `n` per call
        // never produced past the entry's logical size — slack-byte
        // leakage is physically impossible.
      }
      foreach (var d in dir.Dirs) EmitStreamingFiles(d);
    }
    EmitStreamingFiles(streamingTree);
    output.Flush();
  }

  /// <summary>BPB geometry tuple snapshotted from a freshly-written FAT image
  /// to drive the streaming write phase.</summary>
  private readonly record struct StreamingBpb(int BytesPerSector, int SectorsPerCluster,
    int ReservedSectors, int FatCount, int FatSize, int RootEntryCount,
    int FirstDataSector, int FatType);

  /// <summary>Reads the BPB written by <see cref="BuildTo"/> back from
  /// <paramref name="output"/> so streaming-file writes can seek to the
  /// correct cluster offsets without depending on writer internals.</summary>
  private static StreamingBpb ReadBpbForStreaming(Stream output) {
    var bpb = new byte[512];
    output.Position = 0;
    output.ReadExactly(bpb);
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;
    var spc = bpb[13];
    if (spc == 0) spc = 1;
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(14));
    var fatCount = bpb[16];
    if (fatCount == 0) fatCount = 2;
    var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(17));
    var fatSz16 = BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(22));
    var fatSize = fatSz16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(bpb.AsSpan(36))
      : fatSz16;
    var rootDirSectors = (rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reserved + fatCount * fatSize + rootDirSectors;
    // Calculate FAT type from total clusters (mirror FatReader logic).
    var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(bpb.AsSpan(19));
    var totalSectors = totalSectors16 == 0
      ? BinaryPrimitives.ReadInt32LittleEndian(bpb.AsSpan(32))
      : totalSectors16;
    var dataClusters = (totalSectors - firstDataSector) / spc;
    var fatType = fatSz16 == 0 ? 32
      : dataClusters < 4085 ? 12
      : dataClusters < 65525 ? 16
      : 32;
    return new StreamingBpb(bytesPerSector, spc, reserved, fatCount, fatSize,
      rootEntryCount, firstDataSector, fatType);
  }

  /// <summary>
  /// Builds the FAT image using the smallest sector count that fits all file data
  /// <em>and</em> directory entries. Automatically selects FAT32 (≥ 200 000 sectors)
  /// when the file count or total data would overflow the fixed root directory of a
  /// FAT12/FAT16 image. Prefer this over <see cref="Build"/> when the caller does
  /// not know the file count ahead of time (e.g. from a directory walk).
  /// </summary>
  public byte[] BuildAutoSized(int bytesPerSector = 512, int requestedClusterSize = 0,
    string? volumeLabel = null, int forcedFatType = 0, bool enableLfn = true, bool transactionFat = false,
    int requestedRootEntries = 0, bool forceLfn = false, bool minimal = false) {
    if (forceLfn) enableLfn = true; // force-LFN implies VFAT is on
    // Lay out the directory tree to size the image. Only the root's *direct*
    // children bound the fixed FAT12/16 root directory; files inside
    // subdirectories live in the subdir's own cluster chain, so they add data
    // clusters but never overflow the root.
    var tree = BuildTree();
    BuildSlots(tree, enableLfn, forceLfn);
    var rootDirentBytes = ContentLength(tree, isRoot: true);
    var fileSizes = new List<long>();
    var dirContentBytes = new List<long>();
    void CollectSizes(DirNode dir, bool isRoot) {
      if (!isRoot) dirContentBytes.Add(ContentLength(dir, isRoot: false));
      foreach (var f in dir.Files) fileSizes.Add(f.Data.Length);
      foreach (var sub in dir.Dirs) CollectSizes(sub, false);
    }
    CollectSizes(tree, true);

    // Pick the cluster size that minimises slack + FAT overhead without
    // escalating to a higher FAT variant than strictly necessary.
    var clusterBytes = requestedClusterSize > 0
      ? requestedClusterSize
      : SelectOptimalClusterSize(fileSizes, bytesPerSector, forcedFatType, requestedRootEntries);

    // Auto-fit means actually fit. Compute the cluster-aware data area
    // requirement (each file/dir rounded up to whole clusters, since FAT
    // can't sub-allocate clusters) plus a small headroom — 8 free clusters
    // for fragmentation/append room + 64 KB for boot+reserved+root+FATs.
    // The previous 1.44 MB floor meant "convert to FAT" always produced a
    // 1.44 MB image no matter how little data — that defeats auto-size.
    long RoundUpToCluster(long bytes) => bytes <= 0 ? 0 : ((bytes + clusterBytes - 1) / clusterBytes) * clusterBytes;
    var clusterAlignedFiles = fileSizes.Sum(s => RoundUpToCluster(s));
    // Each subdirectory occupies at least one cluster even if its dir entries fit in less.
    var clusterAlignedDirs = dirContentBytes.Sum(b => Math.Max((long)clusterBytes, RoundUpToCluster(b)));

    // ── Minimal-geometry path (compact --minimal) ──────────────────────────
    // Drop the generous "8 free clusters + 64 KB" headroom and the fixed-224
    // root directory: size the image to exactly hold the data plus the real
    // FAT12 metadata (1 reserved + two 9-sector FATs + a root directory sized
    // to the entries actually present). The result is the smallest valid FAT12
    // this writer emits — for a near-empty floppy that is a few KB instead of
    // the 1.44 MB original — but it is no longer a standard mountable floppy.
    // We only take this path when the payload stays within FAT12 (< 4085 data
    // clusters); larger sets fall through to the standard headroom below.
    var spcGuess = Math.Max(1, clusterBytes / bytesPerSector);
    var dataClusterGuess = (clusterAlignedFiles + clusterAlignedDirs) / clusterBytes;
    if (minimal && forcedFatType is 0 or 12 && dataClusterGuess < 4084) {
      const int fat12FatSectors = 9; // Build()'s fixed FAT12 FAT size
      var neededRootEntries = (int)((ContentLength(tree, isRoot: true) + 31) / 32);
      var minRoot = Math.Max(16, (neededRootEntries + 15) / 16 * 16);
      if (requestedRootEntries > minRoot) minRoot = requestedRootEntries;
      var minRootSectors = (minRoot * 32 + bytesPerSector - 1) / bytesPerSector;
      var minMetaSectors = 1 + 2 * fat12FatSectors + minRootSectors;
      var minDataSectors = (int)((clusterAlignedFiles + clusterAlignedDirs + bytesPerSector - 1) / bytesPerSector);
      var minimalTotal = minMetaSectors + minDataSectors + spcGuess; // +1 cluster safety headroom
      return Build(minimalTotal, bytesPerSector, clusterBytes, volumeLabel, forcedFatType,
                   enableLfn, transactionFat, minRoot, forceLfn);
    }

    var neededBytes = clusterAlignedFiles + clusterAlignedDirs + 8L * clusterBytes + 65536;
    var totalSectors = Math.Max(32, (int)((neededBytes + bytesPerSector - 1) / bytesPerSector));

    // FAT12/16 root directories are fixed at most 224 (FAT12) or 512 (FAT16) entries.
    // If our directory won't fit there — or the caller forces FAT32 — we must use
    // FAT32. FAT32 is only *recognised* as FAT32 when it has >= 65,525 data clusters
    // (readers pick the FAT variant purely from the cluster count), so we size to
    // exactly that minimum plus the data and a safety margin — NOT to an arbitrary
    // large constant. This is what keeps a small file set from ballooning into a
    // needlessly huge image just because long filenames forced FAT32.
    var maxRootEntries = requestedRootEntries > 0 ? requestedRootEntries : 224;
    var needFat32 = forcedFatType == 32
      || (forcedFatType == 0 && rootDirentBytes > maxRootEntries * 32);
    if (needFat32) {
      const long fat32MinClusters = 65525;
      const long margin = 2048;             // headroom so Build's own FAT-size recompute stays > the minimum
      const int reservedSectors = 32;       // FAT32 convention (boot + FSInfo + backup)
      var spc = Math.Max(1, clusterBytes / bytesPerSector);
      var dataClusters = fileSizes.Sum(s => s <= 0 ? 0L : (s + clusterBytes - 1) / clusterBytes)
        + dirContentBytes.Sum(b => Math.Max(1L, (b + clusterBytes - 1) / clusterBytes));
      var rootClusters = Math.Max(1L, (rootDirentBytes + clusterBytes - 1) / clusterBytes);
      var targetClusters = Math.Max(fat32MinClusters + margin, dataClusters + rootClusters + margin);
      var fatSectors = (targetClusters * 4 + bytesPerSector - 1) / bytesPerSector;
      var fat32Sectors = reservedSectors + 2 * fatSectors + targetClusters * spc;
      totalSectors = Math.Max(totalSectors, (int)fat32Sectors);
    }

    return Build(totalSectors, bytesPerSector, clusterBytes, volumeLabel, forcedFatType, enableLfn, transactionFat, requestedRootEntries, forceLfn);
  }

  /// <summary>
  /// Picks the cluster size (bytes) that minimises slack + FAT-table overhead
  /// without escalating to a higher FAT variant than strictly necessary.
  /// Delegates to <see cref="Compression.Core.Layout.FilesystemLayoutOptimizer"/>
  /// for the generic optimisation logic; FAT-specific tier and cost knowledge
  /// lives here.
  /// </summary>
  /// <summary>
  /// Picks the cluster size that minimises slack + FAT overhead for a <em>fixed</em>
  /// image size (e.g. a 1.44 MB floppy whose total size must not change). Only cluster
  /// sizes under which all files + metadata still fit inside <paramref name="totalSectors"/>
  /// are considered. Use when the user pinned the image size but left cluster size on Auto.
  /// Returns 0 if no candidate fits (caller should fall back to the writer's default).
  /// </summary>
  public int PickClusterForFixedImage(
      int totalSectors, int bytesPerSector, int forcedFatType, int requestedRootEntries, bool enableLfn) {
    var fileSizes = _files.Select(f => (long)f.Data.Length).ToList();

    int best = 0;
    long bestCost = long.MaxValue;
    int bestTier = int.MaxValue;
    foreach (var cb in Compression.Core.Layout.FilesystemLayoutOptimizer.StandardClusterSizes) {
      if (cb < bytesPerSector || cb % bytesPerSector != 0) continue;
      var spc = cb / bytesPerSector;
      var dataClusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, cb);
      var fatType = forcedFatType is 12 or 16 or 32 ? forcedFatType
        : dataClusters < 4085 ? 12 : dataClusters < 65525 ? 16 : 32;
      if (forcedFatType == 12 && dataClusters >= 4085) continue;
      if (forcedFatType == 16 && dataClusters >= 65525) continue;

      var rootEntries = fatType == 32 ? 0
        : requestedRootEntries > 0 ? requestedRootEntries
        : fatType == 16 ? 512 : 224;
      var rootSectors = (rootEntries * 32 + bytesPerSector - 1) / bytesPerSector;
      var bpe = fatType == 12 ? 12 : fatType == 16 ? 16 : 32;
      var fatSectors = (dataClusters + 2) * bpe / 8 / bytesPerSector + 1;
      var reserved = fatType == 32 ? 32L : 1L;

      // Feasibility: everything must fit inside the fixed image.
      var usedSectors = reserved + 2 * fatSectors + rootSectors + dataClusters * spc;
      if (usedSectors > totalSectors) continue;

      var overhead = (reserved + 2 * fatSectors + rootSectors) * bytesPerSector;
      var slack = Compression.Core.Layout.FilesystemLayoutOptimizer.Slack(fileSizes, cb);
      var cost = overhead + slack;

      // Prefer the lowest FAT tier, then lowest cost (least wasted space).
      if (fatType < bestTier || (fatType == bestTier && cost < bestCost)) {
        bestTier = fatType; bestCost = cost; best = cb;
      }
    }
    return best;
  }

  private static int SelectOptimalClusterSize(
      IReadOnlyList<long> fileSizes, int bytesPerSector, int forcedFatType, int requestedRootEntries) {
    // Candidate cluster sizes in bytes (all standard FAT power-of-two values).
    var candidates = Compression.Core.Layout.FilesystemLayoutOptimizer.StandardClusterSizes;

    return Compression.Core.Layout.FilesystemLayoutOptimizer.SelectClusterSizeTiered(
      candidates,
      tierFn: cb => {
        var dataClusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, cb);
        var fatType = forcedFatType is 12 or 16 or 32 ? forcedFatType
          : dataClusters < 4085 ? 12 : dataClusters < 65525 ? 16 : 32;
        if (forcedFatType == 12 && dataClusters >= 4085) return null; // constraint violated
        if (forcedFatType == 16 && dataClusters >= 65525) return null;
        return fatType; // tier = FAT variant (12 < 16 < 32)
      },
      costFn: cb => {
        var dataClusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, cb);
        var fatType = forcedFatType is 12 or 16 or 32 ? forcedFatType
          : dataClusters < 4085 ? 12 : dataClusters < 65525 ? 16 : 32;
        if (forcedFatType == 12 && dataClusters >= 4085) return null;
        if (forcedFatType == 16 && dataClusters >= 65525) return null;

        var rootEntries = fatType == 32 ? 0
          : requestedRootEntries > 0 ? requestedRootEntries
          : fatType == 16 ? 512 : 224;
        var rootSectors = (rootEntries * 32 + bytesPerSector - 1) / bytesPerSector;
        var bpe = fatType == 12 ? 12 : fatType == 16 ? 16 : 32;
        var fatSectors = (dataClusters + 2) * bpe / 8 / bytesPerSector + 1;
        var reserved = fatType == 32 ? 32L : 1L;
        var overhead = (reserved + 2 * fatSectors + rootSectors) * bytesPerSector;
        var slack = Compression.Core.Layout.FilesystemLayoutOptimizer.Slack(fileSizes, cb);
        return overhead + slack;
      });
  }

  /// <summary>
  /// Convenience: builds a FAT image from a list of files, auto-sizing to fit.
  /// Used by virtual-disk writers (QCOW2, VHD, VMDK, VDI) to embed a filesystem
  /// inside a disk container so that Create() produces a usable volume.
  /// </summary>
  public static byte[] BuildFromFiles(IEnumerable<(string name, byte[] data)> files) {
    var w = new FatWriter();
    var totalData = 0L;
    foreach (var (name, data) in files) {
      w.AddFile(ToShortName(name), data);
      totalData += data.Length;
    }
    // Auto-size: data + ~50% overhead, minimum 1.44 MB.
    var neededBytes = Math.Max(totalData * 3 / 2 + 32768, 1440 * 1024);
    var totalSectors = Math.Max(2880, (int)((neededBytes + 511) / 512));
    return w.Build(totalSectors);
  }

  private static string ToShortName(string name) {
    var leaf = Path.GetFileName(name);
    var dotIdx = leaf.LastIndexOf('.');
    var basePart = (dotIdx >= 0 ? leaf[..dotIdx] : leaf).ToUpperInvariant();
    var extPart = (dotIdx >= 0 ? leaf[(dotIdx + 1)..] : "").ToUpperInvariant();
    basePart = new string(basePart.Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
    extPart = new string(extPart.Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_').ToArray());
    if (basePart.Length == 0) basePart = "FILE";
    if (basePart.Length > 8) basePart = basePart[..8];
    if (extPart.Length > 3) extPart = extPart[..3];
    return extPart.Length > 0 ? $"{basePart}.{extPart}" : basePart;
  }

  /// <summary>
  /// Encodes a user-supplied volume label into the 11-byte BS_VolLab field. Per
  /// FATGEN103 §3.5, the label is ASCII, uppercase, space-padded to exactly 11
  /// bytes. Null / empty / whitespace-only input falls back to the historical
  /// "NO NAME    " sentinel that fsck readers expect for unlabeled volumes.
  /// Characters outside the allowed 8.3 set are replaced with underscore; the
  /// result is truncated to 11 bytes if longer.
  /// </summary>
  private static byte[] BuildVolumeLabelBytes(string? label) {
    if (string.IsNullOrWhiteSpace(label))
      return Encoding.ASCII.GetBytes("NO NAME    ");
    var upper = label.ToUpperInvariant();
    var sanitized = new char[11];
    var written = 0;
    foreach (var c in upper) {
      if (written >= 11) break;
      // Volume labels permit a slightly wider char set than 8.3 short names
      // (spaces are explicitly allowed inside the field), but DOS-era readers
      // reject anything outside ASCII printables anyway. Be conservative.
      if (c == ' ' || Is83Char(c)) sanitized[written++] = c;
      else sanitized[written++] = '_';
    }
    // Pad remaining slots with spaces.
    while (written < 11) sanitized[written++] = ' ';
    return Encoding.ASCII.GetBytes(sanitized);
  }

  private static void WriteFatEntry(byte[] disk, int fatOffset, int cluster, int value, int fatType) {
    if (fatType == 12) {
      var bytePos = fatOffset + cluster * 3 / 2;
      if (bytePos + 1 >= disk.Length) return;
      if ((cluster & 1) == 0) {
        disk[bytePos] = (byte)(value & 0xFF);
        disk[bytePos + 1] = (byte)((disk[bytePos + 1] & 0xF0) | ((value >> 8) & 0x0F));
      } else {
        disk[bytePos] = (byte)((disk[bytePos] & 0x0F) | ((value << 4) & 0xF0));
        disk[bytePos + 1] = (byte)((value >> 4) & 0xFF);
      }
    } else if (fatType == 16) {
      var pos = fatOffset + cluster * 2;
      if (pos + 2 <= disk.Length)
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(pos), (ushort)value);
    } else {
      var pos = fatOffset + cluster * 4;
      if (pos + 4 <= disk.Length)
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(pos), (uint)value & 0x0FFFFFFFu);
    }
  }
}
