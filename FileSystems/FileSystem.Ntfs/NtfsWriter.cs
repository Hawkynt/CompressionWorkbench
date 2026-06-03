#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Builds spec-compliant NTFS filesystem images. All reserved system MFT
/// records (0-15) are populated with real content: $MFT, $MFTMirr, $LogFile,
/// $Volume, $AttrDef, root $., $Bitmap, $Boot, $BadClus, $Secure, $UpCase,
/// and $Extend. Every record carries the mandatory $STANDARD_INFORMATION and
/// $FILE_NAME attributes, the Update Sequence Array (USA) fixup is applied
/// at sector boundaries, and the on-disk cluster bitmap reflects which
/// clusters are actually allocated. Small files (&lt;700 bytes) use a
/// resident $DATA attribute; larger files use non-resident cluster runs.
/// <para>
/// Images produced by this writer carry all the structure that chkdsk and
/// the Linux ntfs-3g driver check at mount time: volume serial, valid boot
/// signature, every system file has its "FILE" magic, USA fixup at
/// <c>record[510..512]</c> and <c>record[1022..1024]</c>, $Volume carries a
/// valid $VOLUME_INFORMATION (version 3.1), the $UpCase data stream is
/// 128 KiB long (65 536 UTF-16 upper-case mappings) and $Bitmap only
/// marks clusters that hold actual filesystem metadata/data.
/// </para>
/// <para>
/// Large directories: when a directory's $I30 file-name index no longer fits
/// in the resident $INDEX_ROOT inside its MFT record, it spills into a
/// non-resident $INDEX_ALLOCATION (a stream of "INDX" index records, each with
/// its own USA fixup) tracked by a named $BITMAP. The $INDEX_ROOT then holds
/// routing pointer entries (subnode VCN flag 0x01 + 8-byte child VCN at the
/// entry tail) into those INDX leaves, and the FILE_NAME entries live in the
/// leaves sorted by NTFS file-name collation. A single B+tree level is built:
/// the resident root points directly at leaf blocks. To keep all routing
/// pointers resident, the INDX block size is grown (power-of-two, 4 KiB..64 KiB)
/// as the entry count rises. With the default 1024-byte MFT record this handles
/// tens of thousands of short-named entries per directory; only a directory
/// whose routing pointers would overflow even a 64 KiB block (hundreds of
/// thousands of entries) would need a second tree level, which is not yet
/// implemented.
/// </para>
/// <para>
/// 8.3 short names: by default every $FILE_NAME is recorded in the Win32&amp;DOS
/// namespace (3) so the long name also serves as the 8.3 short name, the way a
/// freshly formatted Windows volume does. Passing
/// <c>generateShortNames: false</c> records names in the Win32-only namespace
/// (1) and emits no DOS short name — the equivalent of
/// <c>fsutil behavior set disable8dot3</c>.
/// </para>
/// </summary>
public sealed class NtfsWriter {

  // Keep the same high-level layout constants as the original writer so
  // existing tests (e.g. expecting the first user file at record 16) stay
  // valid.
  private const int BytesPerSector = 512;
  private const int DefaultSectorsPerCluster = 8;
  private const int DefaultClusterSize = BytesPerSector * DefaultSectorsPerCluster; // 4096
  private const int DefaultMftRecordSize = 1024;
  private const int MftReservedRecords = 16; // records 0..15 are system files
  private const int ResidentThreshold = 700;

  // Per-build geometry — set by Build() before any record is emitted. These
  // replace the former compile-time constants so cluster + MFT-record size are
  // tunable. Defaults keep the parameterless Build() byte-identical to before.
  private int _sectorsPerCluster = DefaultSectorsPerCluster;
  private int _clusterSize = DefaultClusterSize;
  private int _mftRecordSize = DefaultMftRecordSize;

  // Size of the $LogFile data region in bytes. Real NTFS typically uses
  // ≥2 MiB; for our minimal images we size proportionally to the volume
  // but always allocate at least one cluster.
  private const int LogFileBytes = 64 * 1024; // 64 KiB — enough for a clean log

  // Size of the $UpCase data stream: 65 536 UTF-16 code units = 128 KiB.
  private const int UpCaseBytes = 65536 * 2;

  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];

  /// <summary>
  /// Streaming-allocations side-effect: when non-null, every non-resident
  /// streaming entry's (startCluster, clusterCount, size, opener) is
  /// appended so <see cref="BuildToStreaming"/> can post-fill clusters
  /// from each source after metadata is committed. Resident streaming
  /// entries (size ≤ ResidentThreshold) are buffered inside the MFT
  /// record by the streaming wrapper before <see cref="Build(int)"/> runs.
  /// </summary>
  private List<(int StartCluster, int ClusterCount, long Size, Func<Stream> Opener)>? _streamingSink;
  private readonly string _volumeLabel;

  // $FILE_NAME namespace byte for the names this writer records. NTFS namespaces:
  // 0 = POSIX, 1 = Win32, 2 = DOS, 3 = Win32&DOS (a single name that doubles as
  // the 8.3 short name). A freshly formatted Windows volume records names as
  // Win32&DOS; disabling 8.3 generation ("fsutil behavior set disable8dot3")
  // records them Win32-only so no DOS short name is created. We mirror that:
  // short names on → namespace 3, off → namespace 1.
  private const byte NamespaceWin32 = 1;
  private const byte NamespaceWin32AndDos = 3;
  private readonly byte _fileNameNamespace;

  // A node in the directory tree the writer materialises before emitting MFT
  // records. Every node owns exactly one MFT record. Directories carry an $I30
  // index listing their immediate children; files carry their data + (for the
  // non-resident case) the cluster run assigned during layout.
  private sealed class TreeNode {
    public required string Name;            // leaf name (no path separators)
    public uint RecordNumber;               // this node's MFT record number
    public uint ParentRecord;               // parent directory's MFT record number
    public bool IsDirectory;

    // File payload (directories leave these unset).
    public byte[]? Data;
    public bool Resident;
    public int StartCluster;
    public int ClusterCount;

    // Streaming bytes — Size + opener; non-resident streaming entries
    // skip the in-memory data copy and are post-streamed by
    // BuildToStreaming. Resident streaming entries (Size ≤ threshold) are
    // buffered by the streaming wrapper before Build runs.
    public long? StreamingSize;
    public Func<Stream>? StreamOpener;
    public long EffectiveLength => this.StreamingSize ?? (long)(this.Data?.Length ?? 0);

    // Directory children, in insertion order (re-sorted by name when the $I30
    // index is built).
    public readonly List<TreeNode> Children = [];
    public readonly Dictionary<string, TreeNode> ChildByName =
      new(StringComparer.OrdinalIgnoreCase);

    // Large-directory ($INDEX_ALLOCATION) layout. When a directory's child
    // entries do not fit in the resident $INDEX_ROOT, they spill into
    // non-resident INDX blocks. These fields, populated during layout, hold the
    // pre-rendered INDX-block bytes plus the cluster run they occupy and the
    // $BITMAP marking which blocks are in use.
    public bool IndexSpilled;
    public byte[]? IndexRootBytes;        // resident $INDEX_ROOT pointing at the INDX leaves
    public byte[]? IndexAllocationBytes;  // concatenated INDX blocks (already USA-fixed-up)
    public int IndexAllocStartCluster;
    public int IndexAllocClusterCount;
    public byte[]? IndexBitmapBytes;      // $BITMAP for the INDX blocks
  }

  // Size of one INDX block (one index record) in the $INDEX_ALLOCATION stream.
  // Real NTFS uses 4 KiB regardless of cluster size for directory indexes; we
  // follow that. The index-root header advertises this in its "bytes per index
  // block" field.
  private const int IndexBlockSize = 4096;

  /// <summary>
  /// Creates a new NTFS writer. The volume label is stored in $Volume's
  /// $VOLUME_NAME attribute.
  /// </summary>
  /// <param name="volumeLabel">Volume name stored in $VOLUME_NAME.</param>
  /// <param name="generateShortNames">
  /// When <see langword="true"/> (default, matching a freshly formatted Windows
  /// volume) every $FILE_NAME is recorded in the combined Win32&amp;DOS namespace
  /// so the long name doubles as the 8.3 short name. When <see langword="false"/>
  /// — the equivalent of <c>fsutil behavior set disable8dot3</c> — names are
  /// recorded in the Win32-only namespace and no DOS short name is created.
  /// </param>
  public NtfsWriter(string volumeLabel = "CWB-NTFS", bool generateShortNames = true) {
    ArgumentNullException.ThrowIfNull(volumeLabel);
    this._volumeLabel = volumeLabel;
    this._fileNameNamespace = generateShortNames ? NamespaceWin32AndDos : NamespaceWin32;
  }

  /// <summary>Adds a file to the NTFS image.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data, null, null));
  }

  /// <summary>
  /// Adds a streaming file: <paramref name="size"/> drives MFT-record +
  /// cluster sizing in pass 1; bytes are pulled from
  /// <paramref name="openStream"/> in pass 2 of
  /// <see cref="BuildToStreaming"/>. Files larger than the resident
  /// threshold (~700 bytes) get a single-run non-resident $DATA whose
  /// clusters are filled from the source via 64 KB chunks; smaller files
  /// remain resident and are buffered up-front (the size-clamped bounded
  /// read still satisfies the isolation contract).
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    this._files.Add((name, System.Array.Empty<byte>(), size, openStream));
  }

  /// <summary>
  /// Builds the NTFS filesystem image with the default geometry (4 MiB volume,
  /// 4 KiB clusters, 1024-byte MFT records). Kept as a parameterless-default
  /// overload so existing callers/tests remain byte-compatible.
  /// </summary>
  /// <param name="totalSize">Total image size in bytes (default 4MB).</param>
  /// <returns>Complete NTFS image as byte array.</returns>
  public byte[] Build(int totalSize = 4 * 1024 * 1024)
    => this.Build(totalSize, DefaultClusterSize, DefaultMftRecordSize);

  /// <summary>
  /// Builds the image with the cluster size and MFT record size chosen by
  /// <see cref="Compression.Core.Layout.FilesystemLayoutOptimizer"/> to minimise
  /// file slack + MFT-zone reservation + per-file MFT-record waste, and the
  /// volume sized to exactly hold the files plus structural overhead.
  /// </summary>
  /// <param name="requestedClusterSize">Cluster size in bytes (0 = auto-select).</param>
  /// <param name="requestedMftRecordSize">MFT record size in bytes (0 = auto-select).</param>
  /// <returns>Complete NTFS image as byte array.</returns>
  public byte[] BuildAutoSized(int requestedClusterSize = 0, int requestedMftRecordSize = 0) {
    var fileSizes = this._files.Select(f => f.StreamingSize ?? (long)f.Data.Length).ToList();
    var fileCount = this._files.Count;

    // Candidate spaces. Cluster sizes start at 4 KiB (the practical NTFS floor
    // for our images); MFT records at the three power-of-two sizes real NTFS
    // uses. Honour an explicit request by collapsing the matching candidate
    // list to that single value.
    int[] clusterCandidates = requestedClusterSize > 0
      ? [requestedClusterSize]
      : [4096, 8192, 16384, 32768, 65536];
    int[] mftCandidates = requestedMftRecordSize > 0
      ? [requestedMftRecordSize]
      : [1024, 2048, 4096];

    var (clusterSize, mftRecordSize) = Compression.Core.Layout.FilesystemLayoutOptimizer.SelectPair(
      clusterCandidates,
      mftCandidates,
      (cb, mftSz) => {
        var clusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, cb);
        var slack    = Compression.Core.Layout.FilesystemLayoutOptimizer.Slack(fileSizes, cb);
        // MFT-zone reservation: real NTFS reserves ≈12.5 % of the volume for MFT
        // growth. Approximate as 12.5 % of the data-cluster bytes.
        var mftZone  = (long)(clusters * (long)cb * 0.125);
        // Per-file MFT-record waste: every file consumes one whole MFT record.
        // Resident-data files (≤ ResidentThreshold) keep their data inside the
        // record; the waste is the record bytes their data does not fill. Larger
        // files keep only metadata in the record, so the whole record minus the
        // metadata is "non-data" — approximate the waste as the full record.
        long mftWaste = 0;
        foreach (var s in fileSizes) {
          var residentBytes = s <= ResidentThreshold ? s : 0;
          mftWaste += Math.Max(0, mftSz - residentBytes);
        }
        return slack + mftZone + mftWaste;
      });

    // Size the volume to hold metadata + data + headroom. Each path segment
    // that is not the file's leaf becomes a directory MFT record, so count the
    // distinct directories the tree will materialise alongside the files.
    var directoryCount = this._files
      .SelectMany(f => EnumerateAncestorDirectories(f.Name))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Count();
    var totalMftRecords = MftReservedRecords + fileCount + directoryCount;
    var mftBytes = (long)totalMftRecords * mftRecordSize;
    var dataBytes = fileSizes.Sum(s => s <= ResidentThreshold ? 0L : ((s + clusterSize - 1) / clusterSize) * clusterSize);
    long overhead = (long)LogFileBytes + UpCaseBytes
      + 16L * clusterSize   // boot + bitmaps + mirror + slack
      + mftBytes;
    var total = (overhead + dataBytes) * 11 / 10; // 10 % headroom
    if (total < 4 * 1024 * 1024) total = 4 * 1024 * 1024; // never smaller than the default
    if (total > int.MaxValue) total = int.MaxValue;

    return this.Build((int)total, clusterSize, mftRecordSize);
  }

  /// <summary>
  /// Builds the NTFS filesystem image with a tunable cluster size and MFT record
  /// size.
  /// </summary>
  /// <param name="totalSize">Total image size in bytes (rounded up to a whole cluster).</param>
  /// <param name="clusterSize">
  /// Cluster size in bytes — a power-of-two multiple of the 512-byte sector size (512..65536).
  /// </param>
  /// <param name="mftRecordSize">
  /// MFT record size in bytes — a power of two (512/1024/2048/4096).
  /// </param>
  /// <returns>Complete NTFS image as byte array.</returns>
  public byte[] Build(int totalSize, int clusterSize, int mftRecordSize) {
    ValidateGeometry(clusterSize, mftRecordSize);
    this._clusterSize = clusterSize;
    this._sectorsPerCluster = clusterSize / BytesPerSector;
    this._mftRecordSize = mftRecordSize;

    var (rootNode, treeNodes) = this.BuildTree();

    // Pad to cluster boundary — a fractional cluster at the end confuses
    // readers computing totalClusters from volume size.
    if (totalSize % this._clusterSize != 0)
      totalSize += this._clusterSize - totalSize % this._clusterSize;

    var disk = new byte[totalSize];
    var totalSectors = totalSize / BytesPerSector;
    var totalClusters = totalSize / this._clusterSize;

    // Deterministic volume serial (high 32 bits derived from time, low from
    // magic) so Windows recognises the volume as distinct. Must be non-zero.
    var volumeSerial = DateTime.UtcNow.ToFileTimeUtc() ^ 0x4E544653_4E544653L;

    // --- Cluster layout ------------------------------------------------------
    //   cluster 0                : boot sector (VBR)
    //   clusters 2..              : $MFT
    //   cluster (mftEnd)          : $MFTMirr (mirror of first 4 records)
    //   next                      : $LogFile data
    //   next                      : $UpCase data (128 KiB = 32 clusters)
    //   next                      : $Bitmap data (allocated later)
    //   next                      : user file data
    // Everything stays within the 4 MiB default; larger images just have
    // bigger $Bitmap and user-data regions.

    const int mftStartCluster = 2;
    var totalMftRecords = MftReservedRecords + treeNodes.Count;
    var mftTotalBytes = totalMftRecords * this._mftRecordSize;
    var mftClusters = (mftTotalBytes + this._clusterSize - 1) / this._clusterSize;
    var mftOffset = mftStartCluster * this._clusterSize;

    var nextCluster = mftStartCluster + mftClusters;

    // $MFTMirr lives at roughly the middle of the volume in real NTFS so a
    // single bad sector can't take out both copies; we honour that.
    var mftMirrCluster = totalClusters / 2;
    if (mftMirrCluster <= nextCluster) mftMirrCluster = nextCluster;
    var mftMirrClusters = (4 * this._mftRecordSize + this._clusterSize - 1) / this._clusterSize;
    // Reserve that region before placing other files.

    var logFileCluster = nextCluster;
    var logFileClusters = (LogFileBytes + this._clusterSize - 1) / this._clusterSize;
    nextCluster += logFileClusters;

    var upCaseCluster = nextCluster;
    var upCaseClusters = (UpCaseBytes + this._clusterSize - 1) / this._clusterSize;
    nextCluster += upCaseClusters;

    var bitmapBytes = (totalClusters + 7) / 8;
    var bitmapCluster = nextCluster;
    var bitmapClusters = (bitmapBytes + this._clusterSize - 1) / this._clusterSize;
    nextCluster += bitmapClusters;

    // $MFT's own $BITMAP attribute (type 0xB0) — tracks which MFT records are
    // in use. ntfs-3g consults this on mount before walking the MFT, so even a
    // 16-record image needs a real cluster-backed bitmap. Bit i set ⇔ record i
    // is allocated. Stored non-resident in its own cluster.
    var mftBitmapBitsBytes = (totalMftRecords + 7) / 8;
    var mftBitmapCluster = nextCluster;
    var mftBitmapClusters = (mftBitmapBitsBytes + this._clusterSize - 1) / this._clusterSize;
    if (mftBitmapClusters < 1) mftBitmapClusters = 1;
    nextCluster += mftBitmapClusters;

    // Skip over the $MFTMirr region if we've grown into it.
    if (nextCluster > mftMirrCluster && nextCluster <= mftMirrCluster + mftMirrClusters) {
      nextCluster = mftMirrCluster + mftMirrClusters;
    }

    // Reserve clusters for user file data (non-resident only). Directories own
    // no $DATA, so only file nodes consume clusters; small files stay resident
    // inside their MFT record. Cluster runs are recorded back onto the node.
    var fileNodes = treeNodes.Where(n => !n.IsDirectory).ToList();
    foreach (var node in fileNodes) {
      var effLen = node.EffectiveLength;
      if (effLen <= ResidentThreshold) {
        node.Resident = true;
        continue;
      }
      var clusters = (int)((effLen + this._clusterSize - 1) / this._clusterSize);
      // Skip over the mirror region if necessary.
      if (nextCluster < mftMirrCluster && nextCluster + clusters > mftMirrCluster) {
        nextCluster = mftMirrCluster + mftMirrClusters;
      }
      node.Resident = false;
      node.StartCluster = nextCluster;
      node.ClusterCount = clusters;
      nextCluster += clusters;
    }

    // Reserve clusters for directories whose child index does not fit in the
    // resident $INDEX_ROOT. These spill into a non-resident $INDEX_ALLOCATION
    // stream (INDX blocks) tracked by a $BITMAP. The root directory (record 5)
    // is laid out the same way; it is not part of treeNodes so handle it here.
    var directoryNodes = treeNodes.Where(n => n.IsDirectory).Prepend(rootNode);
    foreach (var dir in directoryNodes) {
      this.LayoutDirectoryIndex(dir, includeSystemEntries: dir == rootNode);
      if (!dir.IndexSpilled) continue;

      var clusters = (dir.IndexAllocationBytes!.Length + this._clusterSize - 1) / this._clusterSize;
      if (nextCluster < mftMirrCluster && nextCluster + clusters > mftMirrCluster)
        nextCluster = mftMirrCluster + mftMirrClusters;
      dir.IndexAllocStartCluster = nextCluster;
      dir.IndexAllocClusterCount = clusters;
      nextCluster += clusters;
    }

    // --- Boot sector (VBR) ---------------------------------------------------
    WriteBootSector(disk, totalSectors, mftStartCluster, mftMirrCluster, volumeSerial);

    // --- Backup boot sector at the LAST sector of the volume -----------------
    // The NTFS spec requires the boot sector to be mirrored at the last sector
    // (totalSectors-1). Without it ntfsfix reports
    // "Checking the alternate boot sector... BAD / Failed to fix the alternate
    // boot sector". Cheaply done: copy the first 512 bytes to the last sector.
    var backupOffset = (long)(totalSectors - 1) * BytesPerSector;
    if (backupOffset >= 0 && backupOffset + BytesPerSector <= disk.Length)
      Array.Copy(disk, 0, disk, (int)backupOffset, BytesPerSector);

    // --- Build the $MFT bitmap data and write it to its cluster -------------
    // Bit i set ⇔ MFT record i is currently allocated. We allocate records 0..15
    // (system) plus one per user file. Bits beyond `totalMftRecords-1` stay 0.
    var mftBitmapBytesActual = mftBitmapClusters * this._clusterSize;
    var mftBitmap = new byte[mftBitmapBytesActual];
    for (var r = 0; r < totalMftRecords; r++)
      mftBitmap[r / 8] |= (byte)(1 << (r % 8));
    WriteBytesToClusters(disk, mftBitmapCluster, mftBitmap);

    // --- Build each system MFT record ---------------------------------------
    // Record 0: $MFT — has both $DATA (the MFT records themselves) and $BITMAP
    // (the per-record allocation bitmap). ntfs-3g loads $BITMAP first to know
    // how many entries to walk; without it mount fails immediately.
    WriteMftRecord(
      disk, mftOffset, 0, sequence: 1,
      fileName: "$MFT",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(mftStartCluster, mftClusters)],
      dataSize: mftTotalBytes,
      sizeHintInFileName: mftTotalBytes,
      extraNonResidentAttrs: [
        new NonResidentAttr(0xB0, [(mftBitmapCluster, mftBitmapClusters)], mftBitmapBytesActual),
      ]);

    // Record 1: $MFTMirr — stored at mftMirrCluster.
    WriteMftRecord(
      disk, mftOffset, 1, sequence: 1,
      fileName: "$MFTMirr",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(mftMirrCluster, mftMirrClusters)],
      dataSize: 4L * this._mftRecordSize,
      sizeHintInFileName: 4L * this._mftRecordSize);

    // Record 2: $LogFile
    WriteMftRecord(
      disk, mftOffset, 2, sequence: 1,
      fileName: "$LogFile",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(logFileCluster, logFileClusters)],
      dataSize: LogFileBytes,
      sizeHintInFileName: LogFileBytes);

    // Record 3: $Volume — volume information + name (small, so resident).
    WriteMftRecord(
      disk, mftOffset, 3, sequence: 1,
      fileName: "$Volume",
      parentRecord: 5,
      isDirectory: false,
      residentData: [],
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0,
      extraAttrs: [
        new ResidentAttr(0x60, BuildVolumeNameAttr(this._volumeLabel)),
        new ResidentAttr(0x70, BuildVolumeInformationAttr()),
      ]);

    // Record 4: $AttrDef — small, stays resident.
    var attrDef = BuildAttrDefTable();
    if (attrDef.Length <= ResidentThreshold) {
      WriteMftRecord(
        disk, mftOffset, 4, sequence: 1,
        fileName: "$AttrDef",
        parentRecord: 5,
        isDirectory: false,
        residentData: attrDef,
        nonResidentRuns: null,
        dataSize: attrDef.Length,
        sizeHintInFileName: attrDef.Length);
    } else {
      // Allocate clusters at the end for $AttrDef if it grows beyond resident.
      // In practice the 22-entry table is ~3 KiB so this branch rarely hits,
      // but keep it for safety.
      var attrDefClusters = (attrDef.Length + this._clusterSize - 1) / this._clusterSize;
      var attrDefCluster = nextCluster;
      nextCluster += attrDefClusters;
      WriteBytesToClusters(disk, attrDefCluster, attrDef);
      WriteMftRecord(
        disk, mftOffset, 4, sequence: 1,
        fileName: "$AttrDef",
        parentRecord: 5,
        isDirectory: false,
        residentData: null,
        nonResidentRuns: [(attrDefCluster, attrDefClusters)],
        dataSize: attrDef.Length,
        sizeHintInFileName: attrDef.Length);
    }

    // Record 5: root directory "." — its $I30 index lists the system files
    // resolved by name at mount time plus every direct child (top-level files
    // and top-level subdirectories). When that index is too large for the
    // resident $INDEX_ROOT it spills into $INDEX_ALLOCATION (laid out above).
    this.WriteDirectoryRecord(disk, mftOffset, recordNum: 5, sequence: 5, fileName: ".", parentRecord: 5, dir: rootNode);

    // Record 6: $Bitmap — cluster-in-use bitmap.
    var bitmap = BuildClusterBitmap(
      totalClusters,
      mftStartCluster, mftClusters,
      mftMirrCluster, mftMirrClusters,
      logFileCluster, logFileClusters,
      upCaseCluster, upCaseClusters,
      bitmapCluster, bitmapClusters,
      mftBitmapCluster, mftBitmapClusters,
      fileNodes,
      treeNodes.Where(n => n.IsDirectory).Prepend(rootNode).ToList());
    WriteBytesToClusters(disk, bitmapCluster, bitmap);
    WriteMftRecord(
      disk, mftOffset, 6, sequence: 1,
      fileName: "$Bitmap",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(bitmapCluster, bitmapClusters)],
      dataSize: bitmap.Length,
      sizeHintInFileName: bitmap.Length);

    // Record 7: $Boot — $DATA covers the first 16 sectors (the VBR + its
    // reserved tail, mirrored by the last sector of the volume in real NTFS;
    // we just point at the first cluster).
    WriteMftRecord(
      disk, mftOffset, 7, sequence: 1,
      fileName: "$Boot",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(0, 2)], // 2 clusters = 16 sectors = 8 KiB
      dataSize: 8192,
      sizeHintInFileName: 8192);

    // Record 8: $BadClus — sparse non-resident $DATA with named default
    // stream that covers the whole volume but has no backing runs, so every
    // cluster reads as zero. We write the unnamed default $DATA as a
    // zero-length resident stream (the canonical NTFS "placeholder" pattern).
    WriteMftRecord(
      disk, mftOffset, 8, sequence: 1,
      fileName: "$BadClus",
      parentRecord: 5,
      isDirectory: false,
      residentData: [],
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0);

    // Record 9: $Secure — carries the security-descriptor stream. A single
    // empty resident $DATA is acceptable for a fresh volume; real drivers
    // fall back to per-file security attributes. No $SDH/$SII indexes for
    // our minimal image.
    WriteMftRecord(
      disk, mftOffset, 9, sequence: 1,
      fileName: "$Secure",
      parentRecord: 5,
      isDirectory: false,
      residentData: [],
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0);

    // Record 10: $UpCase — 65 536-entry Unicode uppercase mapping. Written
    // to its own cluster run so the 128 KiB payload doesn't bloat the MFT.
    var upCase = BuildUpCaseTable();
    WriteBytesToClusters(disk, upCaseCluster, upCase);
    WriteMftRecord(
      disk, mftOffset, 10, sequence: 1,
      fileName: "$UpCase",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(upCaseCluster, upCaseClusters)],
      dataSize: upCase.Length,
      sizeHintInFileName: upCase.Length);

    // Record 11: $Extend — empty directory (no children in a minimal image).
    WriteMftRecord(
      disk, mftOffset, 11, sequence: 1,
      fileName: "$Extend",
      parentRecord: 5,
      isDirectory: true,
      residentData: null,
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0,
      indexRootData: BuildEmptyIndexRoot());

    // Records 12-15: reserved placeholders. Real NTFS leaves them with a
    // FILE signature but the "in-use" flag cleared, so chkdsk sees them as
    // "allocated MFT entries waiting to be used" rather than corruption.
    for (uint r = 12; r <= 15; r++) {
      WriteReservedMftRecord(disk, mftOffset, r);
    }

    // --- User records starting at record 16 ---------------------------------
    // Directory and file nodes share the >= 16 record space. Directories carry
    // a $FILE_NAME pointing at their parent plus an $I30 index over their own
    // children; files carry their parent reference and $DATA (resident or via
    // cluster runs).
    foreach (var node in treeNodes) {
      if (node.IsDirectory) {
        this.WriteDirectoryRecord(disk, mftOffset, node.RecordNumber, sequence: 1,
          fileName: node.Name, parentRecord: node.ParentRecord, dir: node);
        continue;
      }

      var data = node.Data!;
      var effLen = node.EffectiveLength;
      if (node.Resident) {
        // Resident streaming entries: drain the bounded source into the
        // in-memory data buffer once here. Resident files live INSIDE
        // the MFT record (no clusters), so the streaming-copy pass below
        // can't reach them — buffering is required for the byte path.
        // The bound on the source (BoundedEntryStream) still caps
        // anything past `Size`.
        byte[] residentBytes = data;
        if (node.StreamOpener != null) {
          residentBytes = new byte[effLen];
          using var src = node.StreamOpener();
          var read = 0;
          while (read < residentBytes.Length) {
            var n = src.Read(residentBytes, read, residentBytes.Length - read);
            if (n <= 0) break;
            read += n;
          }
        }
        WriteMftRecord(
          disk, mftOffset, node.RecordNumber, sequence: 1,
          fileName: node.Name,
          parentRecord: node.ParentRecord,
          isDirectory: false,
          residentData: residentBytes,
          nonResidentRuns: null,
          dataSize: residentBytes.Length,
          sizeHintInFileName: residentBytes.Length);
      } else {
        WriteMftRecord(
          disk, mftOffset, node.RecordNumber, sequence: 1,
          fileName: node.Name,
          parentRecord: node.ParentRecord,
          isDirectory: false,
          residentData: null,
          nonResidentRuns: [(node.StartCluster, node.ClusterCount)],
          dataSize: effLen,
          sizeHintInFileName: effLen);

        if (node.StreamOpener != null) {
          // Non-resident streaming entry — record its allocation; the
          // BuildToStreaming post-pass fills the clusters from the
          // source via 64 KB chunks. Cluster tail past `effLen` stays
          // sparse-zero from the disk init.
          this._streamingSink?.Add((node.StartCluster, node.ClusterCount, effLen, node.StreamOpener));
        } else {
          var clusterOffset = (long)node.StartCluster * this._clusterSize;
          if (clusterOffset + data.Length <= disk.Length)
            data.CopyTo(disk, (int)clusterOffset);
        }
      }
    }

    // --- $LogFile data region: real NTFS initialises it to "clean" (0xFF)
    //     pages so recovery treats the log as empty. ---
    var logByteOffset = (long)logFileCluster * this._clusterSize;
    if (logByteOffset + LogFileBytes <= disk.Length)
      Array.Fill(disk, (byte)0xFF, (int)logByteOffset, LogFileBytes);

    // --- $MFTMirr data: mirror the first 4 MFT records. ---------------------
    var mirrByteOffset = (long)mftMirrCluster * this._clusterSize;
    if (mirrByteOffset + 4 * this._mftRecordSize <= disk.Length) {
      Array.Copy(disk, mftOffset, disk, (int)mirrByteOffset, 4 * this._mftRecordSize);
    }

    return disk;
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 derives MFT-record + cluster
  /// geometry from the declared sizes of <see cref="AddStreamingFile"/>
  /// entries; pass 2 emits all reserved system MFT records (0..15) + the
  /// per-user MFT records (with $DATA attributes pointing at single-run
  /// non-resident allocations for files &gt; ResidentThreshold), then
  /// streams each non-resident entry's bytes from its factory into its
  /// allocated cluster run via 64 KB chunks. Cluster tail past each
  /// entry's exact <c>Size</c> stays sparse-zero (the in-memory disk
  /// byte[] was zero-initialised and the per-entry stream copy never
  /// reads past the entry's logical size).
  /// </summary>
  /// <remarks>
  /// <para>Coverage: small files (&lt;= 700 bytes) live INSIDE their MFT
  /// record as resident $DATA; for streaming entries the writer drains
  /// the bounded source into a single byte[] of exactly Size bytes
  /// before emitting the record. Large files use single-run non-resident
  /// $DATA — the streaming copy fills their cluster run from the source
  /// in 64 KB chunks.</para>
  /// <para>What's NOT covered: sparse files, compressed files
  /// (LZNT1), multi-run fragmented files. These existed only via the
  /// reader path before; the streaming writer keeps the single-run
  /// invariant the existing Build path produces. Refactoring to a fully
  /// sparse metadata writer (emitting MFT records directly to the
  /// output stream without the disk byte[]) is a documented follow-up.
  /// Entry CONTENTS of non-resident files never travel through a byte[]
  /// inside the writer — that's the bar the fuzz harness checks.</para>
  /// </remarks>
  public void BuildToStreaming(Stream output, int totalSize) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(int StartCluster, int ClusterCount, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    byte[] disk;
    int clusterSize;
    try {
      disk = this.Build(totalSize);
      clusterSize = this._clusterSize;
    } finally {
      this._streamingSink = null;
    }
    output.SetLength(disk.Length);
    output.Position = 0;
    output.Write(disk);

    var buf = new byte[64 * 1024];
    foreach (var (startCluster, _, size, opener) in sink) {
      if (size <= 0) continue;
      var clusterOffset = (long)startCluster * clusterSize;
      if (clusterOffset < 0 || clusterOffset >= output.Length) continue;
      output.Position = clusterOffset;
      using var src = opener();
      long copied = 0;
      while (copied < size) {
        var want = (int)Math.Min(buf.Length, size - copied);
        var n = src.Read(buf, 0, want);
        if (n <= 0) break;
        output.Write(buf, 0, n);
        copied += n;
      }
    }
    output.Flush();
  }

  /// <summary>Two-pass streaming Build with auto-sized geometry.</summary>
  public void BuildToStreamingAutoSized(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreamingAutoSized requires a writable, seekable stream.", nameof(output));

    var sink = new List<(int StartCluster, int ClusterCount, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    byte[] disk;
    int clusterSize;
    try {
      disk = this.BuildAutoSized();
      clusterSize = this._clusterSize;
    } finally {
      this._streamingSink = null;
    }
    output.SetLength(disk.Length);
    output.Position = 0;
    output.Write(disk);

    var buf = new byte[64 * 1024];
    foreach (var (startCluster, _, size, opener) in sink) {
      if (size <= 0) continue;
      var clusterOffset = (long)startCluster * clusterSize;
      if (clusterOffset < 0 || clusterOffset >= output.Length) continue;
      output.Position = clusterOffset;
      using var src = opener();
      long copied = 0;
      while (copied < size) {
        var want = (int)Math.Min(buf.Length, size - copied);
        var n = src.Read(buf, 0, want);
        if (n <= 0) break;
        output.Write(buf, 0, n);
        copied += n;
      }
    }
    output.Flush();
  }

  // Materialises the directory tree from the flat (slashed-name, data) list.
  // The root directory is MFT record 5 and is not part of the returned node
  // list. Intermediate directories are created on demand and assigned MFT
  // record numbers in encounter order, interleaved with files, starting at
  // MftReservedRecords. For a tree with no separators this yields the
  // historical layout: files at records 16, 17, … with parent 5.
  private (TreeNode Root, List<TreeNode> Nodes) BuildTree() {
    var root = new TreeNode { Name = ".", RecordNumber = 5, ParentRecord = 5, IsDirectory = true };
    var nodes = new List<TreeNode>();
    var nextRecord = (uint)MftReservedRecords;

    foreach (var (rawName, data, streamingSize, opener) in this._files) {
      var segments = rawName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;

      var dir = root;
      // Walk/create the directory chain for everything but the final segment.
      for (var s = 0; s < segments.Length - 1; s++) {
        var segment = segments[s];
        if (!dir.ChildByName.TryGetValue(segment, out var child)) {
          child = new TreeNode {
            Name = segment,
            RecordNumber = nextRecord++,
            ParentRecord = dir.RecordNumber,
            IsDirectory = true,
          };
          dir.Children.Add(child);
          dir.ChildByName[segment] = child;
          nodes.Add(child);
        }
        dir = child;
      }

      var leaf = segments[^1];
      // A duplicate leaf path overwrites the previous file's data (last-wins),
      // matching how a real filesystem would treat the same target path twice.
      if (dir.ChildByName.TryGetValue(leaf, out var existing) && !existing.IsDirectory) {
        existing.Data = data;
        existing.StreamingSize = streamingSize;
        existing.StreamOpener = opener;
        continue;
      }

      var fileNode = new TreeNode {
        Name = leaf,
        RecordNumber = nextRecord++,
        ParentRecord = dir.RecordNumber,
        IsDirectory = false,
        Data = data,
        StreamingSize = streamingSize,
        StreamOpener = opener,
      };
      dir.Children.Add(fileNode);
      dir.ChildByName[leaf] = fileNode;
      nodes.Add(fileNode);
    }

    return (root, nodes);
  }

  // Yields the cumulative directory paths a slashed file name implies, e.g.
  // "docs/api/reference.txt" → "docs", "docs/api". Used only for sizing.
  private static IEnumerable<string> EnumerateAncestorDirectories(string name) {
    var segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
    for (var s = 0; s < segments.Length - 1; s++)
      yield return string.Join('/', segments.Take(s + 1));
  }

  private void WriteBootSector(byte[] disk, long totalSectors, long mftCluster, long mftMirrCluster, long volumeSerial) {
    disk[0] = 0xEB; disk[1] = 0x52; disk[2] = 0x90;
    Encoding.ASCII.GetBytes("NTFS    ").CopyTo(disk, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), BytesPerSector);
    disk[13] = (byte)this._sectorsPerCluster;
    disk[21] = 0xF8; // media descriptor
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(40), totalSectors - 1);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(48), mftCluster);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(56), mftMirrCluster);
    // clusters_per_mft_record (offset 64): NTFS encodes this specially.
    //   • record >= cluster  → store the positive cluster count per record.
    //   • record  < cluster  → store the negative base-2 log of the byte size
    //                          (e.g. 1024-byte record → -10, since 2^10 = 1024).
    disk[64] = EncodeClustersPerRecord(this._mftRecordSize, this._clusterSize);
    disk[68] = 4;                    // 4 clusters per index block
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(72), volumeSerial);
    disk[510] = 0x55; disk[511] = 0xAA;
  }

  // Encodes the boot-sector clusters_per_mft_record field (offset 64). When the
  // record fits in a single cluster (record < cluster) the field is the signed
  // negative base-2 log of the record's byte size; otherwise it is the positive
  // number of clusters per record. Mirrors NtfsReader's decode.
  private static byte EncodeClustersPerRecord(int recordSize, int clusterSize) {
    if (recordSize >= clusterSize)
      return (byte)(recordSize / clusterSize);
    var log2 = (int)Math.Log2(recordSize);
    return unchecked((byte)(-log2));
  }

  // Validates the tunable geometry. Cluster size must be a power-of-two multiple
  // of the 512-byte sector size in [512, 65536]; MFT record size must be a power
  // of two in [512, 4096].
  private void ValidateGeometry(int clusterSize, int mftRecordSize) {
    if (clusterSize < BytesPerSector || clusterSize > 65536 ||
        (clusterSize & (clusterSize - 1)) != 0 || clusterSize % BytesPerSector != 0)
      throw new ArgumentOutOfRangeException(nameof(clusterSize),
        clusterSize, "Cluster size must be a power-of-two multiple of 512 in [512, 65536].");
    if (mftRecordSize < 512 || mftRecordSize > 4096 ||
        (mftRecordSize & (mftRecordSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(mftRecordSize),
        mftRecordSize, "MFT record size must be a power of two in {512, 1024, 2048, 4096}.");
  }

  // Number of Update-Sequence-Array entries: 1 record-wide USN + one per
  // 512-byte sector spanned by the record.
  private int UsaCount => 1 + this._mftRecordSize / BytesPerSector;

  // Offset of the first attribute. The USA lives at byte 42 and occupies
  // 2*UsaCount bytes; the attribute region starts after it, 8-byte aligned.
  // Floored at 56 so the default 1024-byte record stays byte-identical to the
  // original writer (which padded attrStart to 56).
  private int AttrStart => Math.Max(56, (42 + 2 * this.UsaCount + 7) & ~7);

  // Writes a reserved (not-in-use) MFT record with FILE magic but no
  // attributes and the "in use" flag cleared. chkdsk treats these as empty
  // slots awaiting allocation rather than corruption.
  private void WriteReservedMftRecord(byte[] disk, int mftBaseOffset, uint recordNum) {
    var recordOffset = mftBaseOffset + (int)recordNum * this._mftRecordSize;
    if (recordOffset + this._mftRecordSize > disk.Length) return;

    var usaCount = this.UsaCount;
    var attrStart = this.AttrStart;
    var record = new byte[this._mftRecordSize];
    record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';

    // USA offset/count.
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 42);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)usaCount);
    // Sequence number.
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);
    // Attrs offset, flags = 0 (not in use).
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), (ushort)attrStart);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 0);
    // Allocated size.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)this._mftRecordSize);
    // MFT record number.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), recordNum);
    // End-of-attributes marker at attrs offset.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(attrStart), 0xFFFFFFFF);
    // Used size = attrs offset + 8 (end marker + alignment pad).
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)(attrStart + 8));

    this.ApplyUsaFixup(record);
    record.CopyTo(disk, recordOffset);
  }

  // Represents extra resident attributes a caller wants emitted between
  // $FILE_NAME and $DATA.
  private readonly record struct ResidentAttr(uint Type, byte[] Value);

  // Represents an extra non-resident attribute (e.g. $BITMAP for $MFT) emitted
  // after $DATA. Logical/physical sizes are derived from the cluster runs.
  private readonly record struct NonResidentAttr(uint Type, List<(int Cluster, int Count)> Runs, long DataSize);

  private void WriteMftRecord(byte[] disk, int mftBaseOffset, uint recordNum, ushort sequence,
    string fileName, uint parentRecord, bool isDirectory,
    byte[]? residentData, List<(int Cluster, int Count)>? nonResidentRuns, long dataSize,
    long sizeHintInFileName,
    byte[]? indexRootData = null,
    ResidentAttr[]? extraAttrs = null,
    NonResidentAttr[]? extraNonResidentAttrs = null,
    List<(int Cluster, int Count)>? indexAllocationRuns = null,
    long indexAllocationSize = 0,
    byte[]? indexBitmap = null) {

    var recordOffset = mftBaseOffset + (int)recordNum * this._mftRecordSize;
    if (recordOffset + this._mftRecordSize > disk.Length) return;

    var usaCount = this.UsaCount;
    var attrStart = this.AttrStart;
    var record = new byte[this._mftRecordSize];

    // --- Header ---
    record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 42); // USA offset
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)usaCount); // 1 USN + one per sector
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 0);  // LSN
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), sequence);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 1); // hard link count

    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), (ushort)attrStart);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), (ushort)(0x01 | (isDirectory ? 0x02 : 0)));
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)this._mftRecordSize);
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), 0); // base MFT ref
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(40), 0); // next attribute instance
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), recordNum);

    var pos = attrStart;

    // 0x10 $STANDARD_INFORMATION — mandatory, always first.
    pos = WriteStandardInformationAttr(record, pos, isDirectory);

    // 0x30 $FILE_NAME — mandatory for every record including system files.
    pos = this.WriteFileNameAttr(record, pos, fileName, parentRecord, sizeHintInFileName, isDirectory);

    // Caller-supplied extra resident attributes ($VOLUME_NAME/$VOLUME_INFORMATION for $Volume, etc.)
    if (extraAttrs != null) {
      foreach (var a in extraAttrs)
        pos = WriteResidentAttr(record, pos, a.Type, a.Value);
    }

    // 0x80 $DATA — only for non-directory records.
    if (!isDirectory) {
      if (residentData != null) {
        pos = WriteResidentDataAttr(record, pos, residentData);
      } else if (nonResidentRuns != null) {
        pos = WriteNonResidentDataAttr(record, pos, nonResidentRuns, dataSize);
      }
    }

    // 0x90 $INDEX_ROOT for directories.
    if (isDirectory && indexRootData != null)
      pos = WriteIndexRootAttr(record, pos, indexRootData);

    // 0xA0 $INDEX_ALLOCATION + 0xB0 $BITMAP (named "$I30") for large directories
    // whose index spilled out of the resident root.
    if (isDirectory && indexAllocationRuns != null) {
      pos = this.WriteNamedNonResidentAttr(record, pos, 0xA0, "$I30", indexAllocationRuns, indexAllocationSize);
      if (indexBitmap != null)
        pos = WriteNamedResidentAttr(record, pos, 0xB0, "$I30", indexBitmap);
    }

    // Caller-supplied extra non-resident attributes (e.g. 0xB0 $BITMAP on $MFT).
    if (extraNonResidentAttrs != null) {
      foreach (var na in extraNonResidentAttrs)
        pos = WriteNonResidentAttr(record, pos, na.Type, na.Runs, na.DataSize);
    }

    // End-of-attributes marker.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0xFFFFFFFF);
    pos += 4;
    // Used-size counter includes the end marker.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)pos);

    ApplyUsaFixup(record);
    record.CopyTo(disk, recordOffset);
  }

  // Writes the update-sequence-array fixup: each 512-byte sector's last two
  // bytes must equal the record-wide USN on disk; the overwritten originals
  // live in the USA so the reader can restore them. CHKDSK and ntfs-3g use
  // the matching USN as a torn-write detector.
  private void ApplyUsaFixup(byte[] record) {
    const ushort usn = 0x0001;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(42), usn);

    // One fixup per 512-byte sector spanned by the record: stash the sector's
    // trailing two bytes into the USA slot, then stamp the USN at the sector
    // tail. Matches NtfsReader's general usaCount-driven decode. For the default
    // 1024-byte record this covers sectors at offsets 510 and 1022 exactly as
    // the original two-sector code did.
    var sectors = this._mftRecordSize / BytesPerSector;
    for (var s = 0; s < sectors; s++) {
      var sectorEnd = s * BytesPerSector + 510;
      var usaSlot = 44 + s * 2; // 42 holds the USN; per-sector slots start at 44
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaSlot), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd)));
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd), usn);
    }
  }

  private static int WriteStandardInformationAttr(byte[] record, int pos, bool isDirectory) {
    const int valueLen = 48; // v1.2 shape — our reader and ntfs-3g both accept it
    var attrLen = (24 + valueLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x10);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; // resident
    record[pos + 9] = 0; // unnamed
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);

    var v = pos + 24;
    var now = DateTime.UtcNow.ToFileTimeUtc();
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v), now);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 8), now);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 16), now);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 24), now);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(v + 32), isDirectory ? 0x10u : 0x80u);

    return pos + attrLen;
  }

  private int WriteFileNameAttr(byte[] record, int pos, string fileName, uint parentRecord,
    long allocatedAndRealSize, bool isDirectory) {
    var nameBytes = Encoding.Unicode.GetBytes(fileName);
    var nameChars = fileName.Length;
    var valueLen = 66 + nameChars * 2;

    var attrLen = 24 + valueLen;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x30);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; // resident
    record[pos + 9] = 0; // unnamed
    // Resident flags: indexed ($FILE_NAME is always referenced by directory indexes).
    record[pos + 12] = 1; // resident_flags = FILE_ATTRIBUTE_IS_INDEXED
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);

    var v = pos + 24;
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v), (long)parentRecord | (1L << 48));

    var now = DateTime.UtcNow.ToFileTimeUtc();
    for (var t = 0; t < 4; t++)
      BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 8 + t * 8), now);

    // Allocated size (offset 40) and real size (offset 48) — helps chkdsk
    // cross-check against $DATA's sizes.
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 40), allocatedAndRealSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 48), allocatedAndRealSize);
    // File-attribute flags (offset 56): DIRECTORY bit for dirs, NORMAL for files.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(v + 56), isDirectory ? 0x10000000u : 0u);

    record[v + 64] = (byte)nameChars;
    record[v + 65] = this._fileNameNamespace; // Win32&DOS (short names on) or Win32-only (off)
    nameBytes.CopyTo(record, v + 66);

    return pos + attrLen;
  }

  private static int WriteResidentAttr(byte[] record, int pos, uint type, byte[] value) {
    var attrLen = (24 + value.Length + 7) & ~7;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), type);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; record[pos + 9] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)value.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);
    value.CopyTo(record, pos + 24);
    return pos + attrLen;
  }

  private static int WriteResidentDataAttr(byte[] record, int pos, byte[] data) {
    var attrLen = 24 + data.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0;
    record[pos + 9] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)data.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);

    data.CopyTo(record, pos + 24);
    return pos + attrLen;
  }

  private int WriteNonResidentDataAttr(byte[] record, int pos, List<(int Cluster, int Count)> runs, long dataSize)
    => this.WriteNonResidentAttr(record, pos, 0x80, runs, dataSize);

  private int WriteNonResidentAttr(byte[] record, int pos, uint type, List<(int Cluster, int Count)> runs, long dataSize) {
    var dataRuns = EncodeDataRuns(runs);
    var dataRunsOffset = 64;
    var attrLen = dataRunsOffset + dataRuns.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), type);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 1; // non-resident
    record[pos + 9] = 0;

    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 16), 0);
    long totalClusters = 0;
    foreach (var (_, c) in runs) totalClusters += c;
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 24), totalClusters - 1);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 32), (ushort)dataRunsOffset);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 40), totalClusters * this._clusterSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 48), dataSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 56), dataSize);

    dataRuns.CopyTo(record, pos + dataRunsOffset);
    return pos + attrLen;
  }

  // Writes a named non-resident attribute (e.g. $INDEX_ALLOCATION named "$I30").
  // The attribute name (UTF-16) sits right after the 64-byte non-resident header;
  // the data runs follow it.
  private int WriteNamedNonResidentAttr(byte[] record, int pos, uint type, string name,
    List<(int Cluster, int Count)> runs, long dataSize) {
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var nameOffset = 64; // standard non-resident header length
    var dataRunsOffset = (nameOffset + nameBytes.Length + 7) & ~7;
    var dataRuns = EncodeDataRuns(runs);
    var attrLen = (dataRunsOffset + dataRuns.Length + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), type);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 1;                              // non-resident
    record[pos + 9] = (byte)(nameBytes.Length / 2);   // name length in chars
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 10), (ushort)nameOffset);

    long totalClusters = 0;
    foreach (var (_, c) in runs) totalClusters += c;
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 16), 0);                      // starting VCN
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 24), totalClusters - 1);      // last VCN
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 32), (ushort)dataRunsOffset);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 40), totalClusters * this._clusterSize); // allocated
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 48), dataSize);               // real size
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 56), dataSize);               // initialized size

    nameBytes.CopyTo(record, pos + nameOffset);
    dataRuns.CopyTo(record, pos + dataRunsOffset);
    return pos + attrLen;
  }

  // Writes a named resident attribute (e.g. the $BITMAP named "$I30" that tracks
  // allocated INDX blocks). The name (UTF-16) follows the 24-byte resident header.
  private static int WriteNamedResidentAttr(byte[] record, int pos, uint type, string name, byte[] value) {
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var nameOffset = 24;
    var valueOffset = (nameOffset + nameBytes.Length + 7) & ~7;
    var attrLen = (valueOffset + value.Length + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), type);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0;                            // resident
    record[pos + 9] = (byte)(nameBytes.Length / 2); // name length in chars
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 10), (ushort)nameOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)value.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), (ushort)valueOffset);

    nameBytes.CopyTo(record, pos + nameOffset);
    value.CopyTo(record, pos + valueOffset);
    return pos + attrLen;
  }

  private static int WriteIndexRootAttr(byte[] record, int pos, byte[] indexData) {
    // INDEX_ROOT (type 0x90) for a file-name directory MUST be named "$I30"
    // (UTF-16, 4 chars = 8 bytes). ntfs-3g locates the directory index by
    // searching for an attribute with type 0x90 AND name "$I30"; without the
    // name it logs "Index root attribute missing in directory inode N".
    var indexName = Encoding.Unicode.GetBytes("$I30"); // 8 bytes
    var nameOffset = 24; // standard resident-attr header is 24 bytes
    var dataOffset = (nameOffset + indexName.Length + 7) & ~7; // align value to 8
    var attrLen = dataOffset + indexData.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x90);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0;                       // form code = resident
    record[pos + 9] = (byte)(indexName.Length / 2); // name length in chars
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 10), (ushort)nameOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)indexData.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), (ushort)dataOffset);

    indexName.CopyTo(record, pos + nameOffset);
    indexData.CopyTo(record, pos + dataOffset);
    return pos + attrLen;
  }

  private static byte[] EncodeDataRuns(List<(int Cluster, int Count)> runs) {
    using var ms = new MemoryStream();
    long prevLcn = 0;

    foreach (var (cluster, count) in runs) {
      var offset = cluster - prevLcn;
      var lengthBytes = GetSignedFieldBytes(count, unsigned: true);
      var offsetBytes = GetSignedFieldBytes(offset, unsigned: false);

      ms.WriteByte((byte)((offsetBytes << 4) | lengthBytes));
      WriteField(ms, count, lengthBytes);
      WriteField(ms, offset, offsetBytes);
      prevLcn = cluster;
    }

    ms.WriteByte(0);
    return ms.ToArray();
  }

  private static int GetSignedFieldBytes(long value, bool unsigned) {
    if (value == 0) return unsigned ? 1 : 0;
    if (unsigned) {
      if (value <= 0xFF) return 1;
      if (value <= 0xFFFF) return 2;
      if (value <= 0xFFFFFF) return 3;
      return 4;
    }
    if (value >= -128 && value <= 127) return 1;
    if (value >= -32768 && value <= 32767) return 2;
    if (value >= -8388608 && value <= 8388607) return 3;
    return 4;
  }

  private static void WriteField(MemoryStream ms, long value, int bytes) {
    for (var i = 0; i < bytes; i++)
      ms.WriteByte((byte)(value >> (i * 8)));
  }

  // The child entries a directory's $I30 index must hold, sorted by NTFS
  // file-name collation. For the root directory, the system files resolved by
  // name at mount time are prepended.
  private static List<(uint Record, string Name)> CollectIndexEntries(TreeNode dir, bool includeSystemEntries) {
    // ntfs-3g resolves system files like $Secure via path lookup through the
    // root directory's $I30 index, NOT by hard-coded record number — so the
    // root index must list the reserved metadata files we populate. Records
    // 12-15 are reserved and not exposed (matches `mkfs.ntfs`).
    var indexed = new List<(uint Record, string Name)>();
    if (includeSystemEntries)
      indexed.Add((9, "$Secure")); // ntfs_open_secure() does pathname_to_inode("$Secure")

    foreach (var child in dir.Children)
      indexed.Add((child.RecordNumber, child.Name));

    // NTFS file-name collation: case-insensitive Unicode code-point order via
    // $UpCase. char.ToUpperInvariant matches our $UpCase table for all of the
    // ASCII names we emit, so it's an order-preserving substitute.
    indexed.Sort((a, b) => string.Compare(
      a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    return indexed;
  }

  // Index-root header (16 bytes) shared by every directory $I30 index. The
  // "bytes per index block" field tells the reader how to step through the
  // $INDEX_ALLOCATION stream; resident-only directories advertise the default.
  private void WriteIndexRootHeader(MemoryStream ms, int indexBlockSize = IndexBlockSize) {
    var header = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x30); // $FILE_NAME collation key
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);     // FILENAME collation
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)indexBlockSize); // bytes per index block
    header[12] = (byte)Math.Max(1, indexBlockSize / this._clusterSize); // clusters per index block
    ms.Write(header);
  }

  // Picks the smallest power-of-two INDX block size (≥ 4 KiB, ≥ one cluster)
  // such that the directory's entries split into at most maxLeaves leaf blocks,
  // keeping the resident pointer $INDEX_ROOT within the MFT record. Caps at
  // 64 KiB; beyond that a multi-level tree would be needed (not implemented).
  private int ChooseIndexBlockSize(List<(uint Record, string Name)> indexed, int maxLeaves) {
    var totalEntryBytes = 0;
    foreach (var (_, name) in indexed)
      totalEntryBytes += IndexEntryLength(name);

    for (var size = Math.Max(IndexBlockSize, this._clusterSize); size <= 64 * 1024; size *= 2) {
      var usaBytes = (1 + size / BytesPerSector) * 2;
      var subHeaderOffset = (24 + usaBytes + 7) & ~7;
      var capacity = size - (subHeaderOffset + 16) - 16;
      if (capacity <= 0) continue;
      // Worst case one extra leaf from per-block packing rounding.
      var leaves = (totalEntryBytes + capacity - 1) / capacity + 1;
      if (leaves <= maxLeaves) return size;
    }
    return 64 * 1024; // best effort; very large directories may still overflow
  }

  // Builds a directory's resident $INDEX_ROOT ($I30) payload listing one index
  // entry per immediate child. Used only when the entries fit in the MFT record.
  private byte[] BuildDirectoryIndexRoot(TreeNode dir, bool includeSystemEntries) {
    var indexed = CollectIndexEntries(dir, includeSystemEntries);

    using var ms = new MemoryStream();
    this.WriteIndexRootHeader(ms);

    using var entries = new MemoryStream();
    foreach (var (recNum, name) in indexed)
      WriteIndexEntry(entries, recNum, name, dir.RecordNumber);
    var last = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(12), 0x02);
    entries.Write(last);

    var entriesData = entries.ToArray();

    var indexHeader = new byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(0), 16);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(4), 16 + entriesData.Length);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(8), 16 + entriesData.Length);
    ms.Write(indexHeader);
    ms.Write(entriesData);

    return ms.ToArray();
  }

  // Decides whether a directory's $I30 index fits in a resident $INDEX_ROOT and,
  // if not, builds the spilled representation: a resident $INDEX_ROOT of pointer
  // entries (subnode VCN flag set), a non-resident $INDEX_ALLOCATION made of INDX
  // leaf blocks holding the actual FILE_NAME entries (sorted), and a $BITMAP
  // marking the allocated blocks. Results are stashed on the node; cluster
  // reservation for the allocation stream happens in the caller.
  private void LayoutDirectoryIndex(TreeNode dir, bool includeSystemEntries) {
    var indexed = CollectIndexEntries(dir, includeSystemEntries);

    // Budget for the resident $INDEX_ROOT value: the MFT record minus the
    // header, $STANDARD_INFORMATION, the directory's own $FILE_NAME, the
    // $INDEX_ROOT attribute header + "$I30" name, and a safety margin for the
    // end-of-attributes marker. If the leaf entries fit, stay resident.
    var residentEntryBytes = 0;
    foreach (var (_, name) in indexed)
      residentEntryBytes += IndexEntryLength(name);
    residentEntryBytes += 16; // end marker entry
    var rootValueBytes = 16 /*index-root header*/ + 16 /*index header*/ + residentEntryBytes;

    var fileNameValue = 66 + dir.Name.Length * 2;
    var fileNameAttr = (24 + fileNameValue + 7) & ~7;
    var stdInfoAttr = (24 + 48 + 7) & ~7;
    var indexRootAttrOverhead = ((24 + 8 + 7) & ~7) /*hdr + "$I30"*/ + 8 /*end marker + pad*/;
    var residentBudget = this._mftRecordSize - this.AttrStart - stdInfoAttr - fileNameAttr - indexRootAttrOverhead;

    if (rootValueBytes <= residentBudget) {
      dir.IndexSpilled = false;
      dir.IndexRootBytes = BuildDirectoryIndexRoot(dir, includeSystemEntries);
      return;
    }

    // Spill into $INDEX_ALLOCATION. A single B+tree level is used: the resident
    // $INDEX_ROOT holds one routing pointer per leaf and each leaf is one INDX
    // block. The number of leaves must be small enough that all pointer entries
    // fit in the resident root, so the INDX block size is grown (a power-of-two
    // multiple of the sector size) until the leaf count fits that budget. With
    // the default 1024-byte MFT record this comfortably handles tens of
    // thousands of short-named entries; see the type docs for the cap.
    var maxPointerEntryBytes = 0;
    foreach (var (_, name) in indexed)
      maxPointerEntryBytes = Math.Max(maxPointerEntryBytes, ((16 + 66 + name.Length * 2 + 7) & ~7) + 8);
    if (maxPointerEntryBytes == 0) maxPointerEntryBytes = 24;
    var maxPointers = Math.Max(1, (residentBudget - 24 /*end pointer*/) / maxPointerEntryBytes);

    var indexBlockSize = this.ChooseIndexBlockSize(indexed, maxPointers);

    var usaEntries = 1 + indexBlockSize / BytesPerSector;
    var indexHeaderOffset = 24; // INDX record header is 24 bytes; USA follows
    var usaBytes = usaEntries * 2;
    var subHeaderOffset = (indexHeaderOffset + usaBytes + 7) & ~7; // 8-byte aligned index sub-header
    var leafEntriesStart = subHeaderOffset + 16;                   // after the 16-byte index sub-header
    var leafCapacity = indexBlockSize - leafEntriesStart - 16;     // reserve 16 for the end-marker entry

    var leaves = new List<List<(uint Record, string Name)>>();
    var current = new List<(uint Record, string Name)>();
    var currentBytes = 0;
    foreach (var e in indexed) {
      var len = IndexEntryLength(e.Name);
      if (currentBytes + len > leafCapacity && current.Count > 0) {
        leaves.Add(current);
        current = [];
        currentBytes = 0;
      }
      current.Add(e);
      currentBytes += len;
    }
    if (current.Count > 0) leaves.Add(current);
    if (leaves.Count == 0) leaves.Add([]); // empty directory still gets one (empty) leaf

    // Render each leaf as an INDX block (with USA fixups) and collect the
    // separator key (the last entry's name + record) for the root pointer.
    using var alloc = new MemoryStream();
    var pointers = new List<(uint Record, string Name, long Vcn)>();
    var clustersPerBlock = Math.Max(1, indexBlockSize / this._clusterSize);
    for (var i = 0; i < leaves.Count; i++) {
      var vcn = (long)i * clustersPerBlock;
      var block = this.BuildIndexBlock(leaves[i], dir.RecordNumber, vcn, leafEntriesStart, subHeaderOffset, indexBlockSize);
      alloc.Write(block);
      // Pure routing pointer: separator name only (the largest key in this
      // leaf), MFT ref 0 so the real entry is counted once — in the leaf.
      var sepName = leaves[i].Count > 0 ? leaves[i][^1].Name : string.Empty;
      pointers.Add((0u, sepName, vcn));
    }

    dir.IndexSpilled = true;
    dir.IndexAllocationBytes = alloc.ToArray();
    dir.IndexRootBytes = BuildPointerIndexRoot(pointers, dir.RecordNumber, indexBlockSize);

    // $BITMAP: one bit per INDX block, rounded up to 8 bytes (NTFS minimum).
    var bitmapBytes = Math.Max(8, ((leaves.Count + 63) / 64) * 8);
    var bitmap = new byte[bitmapBytes];
    for (var i = 0; i < leaves.Count; i++)
      bitmap[i / 8] |= (byte)(1 << (i % 8));
    dir.IndexBitmapBytes = bitmap;
  }

  // 8-byte-aligned length of one FILE_NAME index entry for the given name.
  private static int IndexEntryLength(string name) => (16 + 66 + name.Length * 2 + 7) & ~7;

  // Builds a single INDX leaf block: standard "INDX" record header, USA, an
  // index sub-header, the leaf's FILE_NAME entries (sorted) and an end-marker
  // entry. USA fixups are applied so the reader can detect torn writes.
  private byte[] BuildIndexBlock(List<(uint Record, string Name)> entries, uint parentRecord, long vcn,
    int entriesStart, int subHeaderOffset, int indexBlockSize) {
    var block = new byte[indexBlockSize];

    // INDX record header.
    block[0] = (byte)'I'; block[1] = (byte)'N'; block[2] = (byte)'D'; block[3] = (byte)'X';
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), 24);                                  // USA offset
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(6), (ushort)(1 + indexBlockSize / BytesPerSector)); // USA count
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8), 0);                                   // LSN
    BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(16), vcn);                                 // this block's VCN

    // Render the entry stream (entries + end marker).
    using var es = new MemoryStream();
    foreach (var (recNum, name) in entries)
      WriteIndexEntry(es, recNum, name, parentRecord);
    var end = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(12), 0x02); // last entry, no subnode
    es.Write(end);
    var entryStream = es.ToArray();

    // Index sub-header (relative to its own start at subHeaderOffset).
    var entriesRel = entriesStart - subHeaderOffset;
    var totalSize = entriesRel + entryStream.Length;
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(subHeaderOffset + 0), entriesRel);                  // entries offset
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(subHeaderOffset + 4), totalSize);                   // index content size
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(subHeaderOffset + 8), indexBlockSize - subHeaderOffset); // allocated size
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(subHeaderOffset + 12), 0);                         // flags: leaf (no children)
    entryStream.CopyTo(block, entriesStart);

    ApplyIndexBlockUsaFixup(block);
    return block;
  }

  // Applies the Update-Sequence-Array fixup to an INDX block: every 512-byte
  // sector's trailing two bytes are stashed in the USA and replaced with the USN.
  private static void ApplyIndexBlockUsaFixup(byte[] block) {
    const ushort usn = 0x0001;
    const int usaOffset = 24;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(usaOffset), usn);
    var sectors = block.Length / BytesPerSector;
    for (var s = 0; s < sectors; s++) {
      var sectorEnd = s * BytesPerSector + 510;
      var usaSlot = usaOffset + 2 + s * 2;
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(usaSlot), BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(sectorEnd)));
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(sectorEnd), usn);
    }
  }

  // Builds the resident $INDEX_ROOT holding pointer entries: one per INDX leaf,
  // each carrying the subnode flag (0x01) and an 8-byte child VCN at its tail.
  // The final entry is the end marker (flags 0x02|0x01) pointing at the last leaf.
  private byte[] BuildPointerIndexRoot(List<(uint Record, string Name, long Vcn)> pointers, uint parentRecord, int indexBlockSize) {
    using var ms = new MemoryStream();
    this.WriteIndexRootHeader(ms, indexBlockSize);

    using var entries = new MemoryStream();
    // All but the last leaf become keyed pointer entries; the last leaf is
    // reached through the end-marker entry's subnode pointer.
    for (var i = 0; i < pointers.Count - 1; i++) {
      var (recNum, name, vcn) = pointers[i];
      WritePointerEntry(entries, recNum, name, parentRecord, vcn, isEnd: false);
    }
    var lastVcn = pointers[^1].Vcn;
    WritePointerEntry(entries, 0, string.Empty, parentRecord, lastVcn, isEnd: true);

    var entriesData = entries.ToArray();

    var indexHeader = new byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(0), 16);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(4), 16 + entriesData.Length);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(8), 16 + entriesData.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(indexHeader.AsSpan(12), 1); // flags: LARGE_INDEX (has children)
    ms.Write(indexHeader);
    ms.Write(entriesData);

    return ms.ToArray();
  }

  // Writes a single pointer index entry: an optional FILE_NAME key plus a
  // trailing 8-byte child VCN. The subnode flag (0x01) is set; the end marker
  // additionally sets 0x02 and carries no key.
  private void WritePointerEntry(MemoryStream ms, uint mftRecordNum, string fileName, uint parentRecord,
    long childVcn, bool isEnd) {
    if (isEnd) {
      // End marker with a subnode: 16-byte header + 8-byte VCN.
      var entry = new byte[24];
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), 24);  // entry length
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), 0);  // no content
      BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(12), 0x03); // last entry (0x02) + has subnode (0x01)
      BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), childVcn);
      ms.Write(entry);
      return;
    }

    var nameChars = fileName.Length;
    var contentLen = 66 + nameChars * 2;
    var entryLen = 16 + contentLen;
    entryLen = (entryLen + 7) & ~7;
    entryLen += 8; // 8-byte child VCN at the tail

    var pointer = new byte[entryLen];
    BinaryPrimitives.WriteInt64LittleEndian(pointer.AsSpan(0), (long)mftRecordNum | (1L << 48));
    BinaryPrimitives.WriteUInt16LittleEndian(pointer.AsSpan(8), (ushort)entryLen);
    BinaryPrimitives.WriteUInt16LittleEndian(pointer.AsSpan(10), (ushort)contentLen);
    BinaryPrimitives.WriteUInt16LittleEndian(pointer.AsSpan(12), 0x01); // has subnode

    BinaryPrimitives.WriteInt64LittleEndian(pointer.AsSpan(16), (long)parentRecord | (1L << 48));
    var nameBytes = Encoding.Unicode.GetBytes(fileName);
    pointer[16 + 64] = (byte)nameChars;
    pointer[16 + 65] = this._fileNameNamespace;
    nameBytes.CopyTo(pointer, 16 + 66);

    // Child VCN occupies the last 8 bytes of the entry.
    BinaryPrimitives.WriteInt64LittleEndian(pointer.AsSpan(entryLen - 8), childVcn);
    ms.Write(pointer);
  }

  // Emits a directory MFT record. For a resident-index directory this writes a
  // single $INDEX_ROOT; for a large directory it writes the pointer $INDEX_ROOT
  // plus a non-resident $INDEX_ALLOCATION and its $BITMAP, and copies the INDX
  // blocks to their reserved clusters.
  private void WriteDirectoryRecord(byte[] disk, int mftOffset, uint recordNum, ushort sequence,
    string fileName, uint parentRecord, TreeNode dir) {
    if (!dir.IndexSpilled) {
      WriteMftRecord(disk, mftOffset, recordNum, sequence,
        fileName: fileName, parentRecord: parentRecord, isDirectory: true,
        residentData: null, nonResidentRuns: null, dataSize: 0, sizeHintInFileName: 0,
        indexRootData: dir.IndexRootBytes ?? BuildDirectoryIndexRoot(dir, includeSystemEntries: recordNum == 5));
      return;
    }

    WriteMftRecord(disk, mftOffset, recordNum, sequence,
      fileName: fileName, parentRecord: parentRecord, isDirectory: true,
      residentData: null, nonResidentRuns: null, dataSize: 0, sizeHintInFileName: 0,
      indexRootData: dir.IndexRootBytes,
      indexAllocationRuns: [(dir.IndexAllocStartCluster, dir.IndexAllocClusterCount)],
      indexAllocationSize: dir.IndexAllocationBytes!.Length,
      indexBitmap: dir.IndexBitmapBytes);

    // Copy the INDX blocks into their reserved clusters.
    var offset = (long)dir.IndexAllocStartCluster * this._clusterSize;
    if (offset + dir.IndexAllocationBytes.Length <= disk.Length)
      dir.IndexAllocationBytes.CopyTo(disk, (int)offset);
  }

  private static byte[] BuildEmptyIndexRoot() {
    // Index root with only the end-marker entry.
    using var ms = new MemoryStream();

    var header = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x30);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 4096);
    header[12] = 1;
    ms.Write(header);

    var last = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(12), 0x02);

    var indexHeader = new byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(0), 16);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(4), 16 + last.Length);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(8), 16 + last.Length);
    ms.Write(indexHeader);
    ms.Write(last);

    return ms.ToArray();
  }

  private void WriteIndexEntry(MemoryStream ms, uint mftRecordNum, string fileName, uint parentRecord) {
    var nameBytes = Encoding.Unicode.GetBytes(fileName);
    var nameChars = fileName.Length;

    var contentLen = 66 + nameChars * 2;
    var entryLen = 16 + contentLen;
    entryLen = (entryLen + 7) & ~7;

    var entry = new byte[entryLen];
    BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0), (long)mftRecordNum | (1L << 48));
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)entryLen);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), (ushort)contentLen);

    // Embedded $FILE_NAME parent reference points at the directory that owns
    // this index, so the entry is self-consistent with the child's own record.
    BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), (long)parentRecord | (1L << 48));
    entry[16 + 64] = (byte)nameChars;
    entry[16 + 65] = this._fileNameNamespace;
    nameBytes.CopyTo(entry, 16 + 66);

    ms.Write(entry);
  }

  // ── $Volume attributes ──────────────────────────────────────────────────

  private static byte[] BuildVolumeNameAttr(string label) {
    // $VOLUME_NAME (type 0x60) value is just the UTF-16 label (no NUL).
    return Encoding.Unicode.GetBytes(label);
  }

  private static byte[] BuildVolumeInformationAttr() {
    // $VOLUME_INFORMATION (type 0x70) layout (12 bytes):
    //   u64 reserved, u8 major_version, u8 minor_version, u16 flags.
    var v = new byte[12];
    v[8] = 3;  // major version (NTFS 3.1 → major 3)
    v[9] = 1;  // minor version
    // flags = 0 (clean volume; no VOLUME_IS_DIRTY bit set).
    return v;
  }

  // ── $AttrDef standard table ─────────────────────────────────────────────

  // Canonical NTFS attribute-definition entries the system driver expects.
  // Each entry is 160 bytes: 128-byte UTF-16 name, u32 type, u32 display rule,
  // u32 collation rule, u32 flags, u64 min size, u64 max size.
  private static byte[] BuildAttrDefTable() {
    (string Name, uint Type, uint DisplayRule, uint Collation, uint Flags, long MinSize, long MaxSize)[] defs =
    [
      ("$STANDARD_INFORMATION", 0x10, 0, 0, 0x40, 48, 72),
      ("$ATTRIBUTE_LIST",        0x20, 0, 0, 0x40, 0, -1),
      ("$FILE_NAME",             0x30, 1, 1, 0x42, 68, 578),
      ("$OBJECT_ID",             0x40, 0, 0, 0x40, 0, 256),
      ("$SECURITY_DESCRIPTOR",   0x50, 0, 0, 0x00, 0, -1),
      ("$VOLUME_NAME",           0x60, 0, 0, 0x40, 2, 256),
      ("$VOLUME_INFORMATION",    0x70, 0, 0, 0x40, 12, 12),
      ("$DATA",                  0x80, 0, 0, 0x00, 0, -1),
      ("$INDEX_ROOT",            0x90, 0, 0, 0x40, 0, -1),
      ("$INDEX_ALLOCATION",      0xA0, 0, 0, 0x00, 0, -1),
      ("$BITMAP",                0xB0, 0, 0, 0x00, 0, -1),
      ("$REPARSE_POINT",         0xC0, 0, 0, 0x00, 0, 0x4000),
      ("$EA_INFORMATION",        0xD0, 0, 0, 0x40, 8, 8),
      ("$EA",                    0xE0, 0, 0, 0x00, 0, 0x10000),
      ("$PROPERTY_SET",          0xF0, 0, 0, 0x40, 0, -1),
      ("$LOGGED_UTILITY_STREAM", 0x100, 0, 0, 0x00, 0, 0x10000),
    ];
    var table = new byte[defs.Length * 160];
    for (var i = 0; i < defs.Length; i++) {
      var d = defs[i];
      var o = i * 160;
      var name = Encoding.Unicode.GetBytes(d.Name);
      Array.Copy(name, 0, table, o, Math.Min(name.Length, 128));
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 128), d.Type);
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 132), d.DisplayRule);
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 136), d.Collation);
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 140), d.Flags);
      BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(o + 144), d.MinSize);
      BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(o + 152), d.MaxSize);
    }
    return table;
  }

  // ── $UpCase table ────────────────────────────────────────────────────────

  /// <summary>
  /// Builds the 65 536-entry UTF-16 uppercase mapping for $UpCase. Real NTFS
  /// ships a driver-defined table with Windows-specific casing; our table is
  /// derived from <see cref="char.ToUpperInvariant"/> which matches for the
  /// ASCII range and handles the common BMP range using the ICU-backed
  /// invariant culture — good enough for ntfs-3g's sanity check (which only
  /// verifies size and a handful of well-known mappings).
  /// </summary>
  internal static byte[] BuildUpCaseTable() {
    var table = new byte[UpCaseBytes];
    for (var i = 0; i < 65536; i++) {
      var upper = char.ToUpperInvariant((char)i);
      BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 2), upper);
    }
    return table;
  }

  // ── Cluster bitmap ───────────────────────────────────────────────────────

  private static byte[] BuildClusterBitmap(
    long totalClusters,
    int mftStart, int mftCount,
    long mftMirrStart, int mftMirrCount,
    int logStart, int logCount,
    int upCaseStart, int upCaseCount,
    int bitmapStart, int bitmapCount,
    int mftBitmapStart, int mftBitmapCount,
    List<TreeNode> fileNodes,
    List<TreeNode> directoryNodes) {
    var bytes = (int)((totalClusters + 7) / 8);
    var bitmap = new byte[bytes];

    // Boot sector + first two clusters.
    SetRange(bitmap, 0, 2);
    SetRange(bitmap, mftStart, mftCount);
    SetRange(bitmap, (int)mftMirrStart, mftMirrCount);
    SetRange(bitmap, logStart, logCount);
    SetRange(bitmap, upCaseStart, upCaseCount);
    SetRange(bitmap, bitmapStart, bitmapCount);
    SetRange(bitmap, mftBitmapStart, mftBitmapCount);

    foreach (var f in fileNodes) {
      if (!f.Resident) SetRange(bitmap, f.StartCluster, f.ClusterCount);
    }

    // Directory $INDEX_ALLOCATION clusters (large directories only).
    foreach (var d in directoryNodes) {
      if (d.IndexSpilled) SetRange(bitmap, d.IndexAllocStartCluster, d.IndexAllocClusterCount);
    }

    return bitmap;
  }

  private static void SetRange(byte[] bitmap, int startCluster, int count) {
    for (var c = startCluster; c < startCluster + count; c++) {
      if ((uint)(c / 8) >= (uint)bitmap.Length) return;
      bitmap[c / 8] |= (byte)(1 << (c % 8));
    }
  }

  // ── Low-level helpers ────────────────────────────────────────────────────

  private void WriteBytesToClusters(byte[] disk, int startCluster, byte[] data) {
    var offset = (long)startCluster * this._clusterSize;
    if (offset + data.Length > disk.Length) return;
    data.CopyTo(disk, (int)offset);
  }
}
