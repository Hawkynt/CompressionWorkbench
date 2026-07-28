#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FileSystem.ExFat;

/// <summary>
/// Builds exFAT filesystem images that Windows 10+ actually mounts.
/// <para>
/// Default layout: 8&#160;MB image, 512&#8239;B/sector, 8&#160;sectors/cluster (4&#160;KB clusters).
/// VBR at sector&#160;0, backup VBR at sector&#160;12, FAT at sector&#160;24, cluster heap thereafter;
/// cluster&#160;2 = root, cluster&#160;3 = allocation bitmap, cluster&#160;4 = up-case table.
/// </para>
/// <para>
/// Key real-world fixes over the original implementation: Set-checksum on each File
/// directory entry set (required — Windows silently ignores files whose set-checksum
/// is wrong), up-case table checksum, timestamps on create/modify/access, volume
/// serial number, filesystem revision (1.0), stream-extension GeneralSecondaryFlags
/// advertising FAT-chain allocation. These are the fields fsck/chkdsk and
/// <c>diskutil</c>/<c>fsck_exfat</c> audit before declaring the volume clean.
/// </para>
/// </summary>
public sealed class ExFatWriter {
  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];
  private const uint EocMarker = 0xFFFFFFFFu;

  public void AddFile(string name, byte[] data) => _files.Add((name, data, null, null));

  /// <summary>
  /// Adds a streaming file: its <paramref name="size"/> is known up front
  /// (so the writer can plan cluster geometry), but its bytes are fetched on
  /// demand from <paramref name="openStream"/> during
  /// <see cref="BuildToStreaming"/>. Never buffered in memory by the writer.
  /// </summary>
  /// <remarks>
  /// Pair with <see cref="BuildToStreaming"/>. The factory is invoked at
  /// most once per file; the returned stream is read for exactly
  /// <paramref name="size"/> bytes (never more), so cluster-tail slack past
  /// the entry's logical size stays sparse-zero.
  /// </remarks>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    _files.Add((name, System.Array.Empty<byte>(), size, openStream));
  }

  /// <summary>
  /// Builds the exFAT image using the smallest size that fits all files, with
  /// the cluster size chosen by <see cref="Compression.Core.Layout.FilesystemLayoutOptimizer"/>
  /// to minimise internal slack + FAT overhead.
  /// </summary>
  /// <param name="requestedClusterBytes">Cluster size in bytes (0 = auto-select with the optimiser).</param>
  /// <param name="volumeLabel">Volume label written into the root directory as a Volume Label
  /// Directory Entry (type 0x83). Null/empty still emits the entry with character count 0.</param>
  public byte[] BuildAutoSized(int requestedClusterBytes = 0, string? volumeLabel = null)
    => Materialise(BuildAutoSizedCore(requestedClusterBytes, volumeLabel, out _));

  private byte[] BuildAutoSizedCore(int requestedClusterBytes, string? volumeLabel, out DirNode builtTree) {
    var fileSizes = _files.Select(f => f.StreamingSize ?? (long)f.Data.Length).ToList();
    const int bytesPerSector = 512;

    // exFAT's conventional minimum cluster for volumes in this range is 4 KiB —
    // Microsoft's formatter never goes smaller and our writer's layout assumes it.
    // Candidates therefore start at 4 KiB so an empty/small format doesn't collapse
    // to a 512-byte cluster (which broke the partition Add / round-trip path).
    const int minClusterBytes = 4096;
    int[] clusterCandidates = [4096, 8192, 16384, 32768, 65536, 131072];

    var clusterBytes = requestedClusterBytes >= minClusterBytes
      ? requestedClusterBytes
      : Compression.Core.Layout.FilesystemLayoutOptimizer.SelectClusterSize(
          clusterCandidates,
          cb => {
            if (cb < minClusterBytes) return null; // honour the 4 KiB floor
            var clusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, cb);
            var slack    = Compression.Core.Layout.FilesystemLayoutOptimizer.Slack(fileSizes, cb);
            // exFAT FAT overhead: 4 bytes per cluster entry, 1 FAT copy.
            var fatBytes = (clusters + 2) * 4L;
            // Allocation bitmap: 1 bit per cluster.
            var bitmapBytes = (clusters + 7) / 8;
            return slack + fatBytes + bitmapBytes;
          });

    var totalDataBytes = fileSizes.Sum();
    // Generous headroom: data + overhead + upcase table (~128 KB) + 5 % slack.
    var neededBytes = (long)(totalDataBytes * 1.05) + clusterBytes * 8 + 128 * 1024 + 24 * bytesPerSector;
    var totalSizeMB = (int)Math.Max(8, (neededBytes + 1024 * 1024 - 1) / (1024 * 1024));
    return BuildCore(totalSizeMB, clusterBytes, volumeLabel, out builtTree);
  }

  /// <summary>Builds the exFAT image.</summary>
  /// <param name="totalSizeMB">Total image size in megabytes.</param>
  /// <param name="requestedClusterBytes">Cluster size in bytes (0 = use 4 KB default).</param>
  /// <param name="volumeLabel">Volume label written into the root directory as a Volume Label
  /// Directory Entry (type 0x83). Null/empty still emits the entry with character count 0
  /// to match Windows format.com behaviour.</param>
  public byte[] Build(int totalSizeMB = 8, int requestedClusterBytes = 0, string? volumeLabel = null)
    => Materialise(BuildCore(totalSizeMB, requestedClusterBytes, volumeLabel, out _));

  /// <summary>
  /// Declared volume size in bytes of the most recent build. <see cref="BuildCore" />
  /// materialises only the written prefix, so a caller holding that prefix must extend
  /// its output to this length; the free space past the prefix is sparse zeros.
  /// </summary>
  public long DeclaredVolumeBytes { get; private set; }

  /// <summary>
  /// Expands a written prefix to the full declared volume, for callers that need a
  /// complete in-memory image.
  /// </summary>
  /// <exception cref="NotSupportedException">
  /// The volume is larger than a single array can hold. Use
  /// <see cref="BuildTo(Stream,int,int,string)" />, which never materialises free space.
  /// </exception>
  private byte[] Materialise(byte[] prefix) {
    var declared = this.DeclaredVolumeBytes;
    if (declared <= prefix.Length) return prefix;
    if (declared > Array.MaxLength)
      throw new NotSupportedException(
        $"An exFAT volume of {declared:N0} bytes cannot be materialised in memory; " +
        "use BuildTo(Stream, ...) so the free space stays sparse.");
    var full = new byte[declared];
    prefix.CopyTo(full, 0);
    return full;
  }

  /// <summary>
  /// Writes the volume to <paramref name="output" />, emitting only the region that
  /// actually carries data and then extending the stream to the declared size. Free
  /// space costs nothing, so volumes far past the in-memory limit are producible.
  /// </summary>
  public void BuildTo(Stream output, int totalSizeMB = 8, int requestedClusterBytes = 0,
                      string? volumeLabel = null) {
    ArgumentNullException.ThrowIfNull(output);
    var prefix = BuildCore(totalSizeMB, requestedClusterBytes, volumeLabel, out _);
    output.Write(prefix);
    if (output.CanSeek && this.DeclaredVolumeBytes > prefix.Length)
      output.SetLength(this.DeclaredVolumeBytes);
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 derives cluster geometry from the
  /// declared sizes of <see cref="AddStreamingFile"/> entries; pass 2 emits
  /// the boot region + FAT + allocation bitmap + up-case table + directory
  /// tree (file-data clusters left zero), then streams each entry's bytes
  /// from its factory straight into its allocated cluster run via 64 KB
  /// chunks. Cluster tail past each entry's exact <c>Size</c> stays sparse
  /// zero (the in-memory disk byte[] was zero-initialised and the
  /// per-entry stream copy never reads past the entry's logical size).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Bounded source streams (typically <c>BoundedEntryStream</c>) make
  /// slack-byte leakage from the read side physically impossible; the
  /// writer's exact-byte-count copy guarantees no excess from the write
  /// side. Per-entry isolation: every byte in an allocated cluster either
  /// came from the entry's own stream (clamped to its logical size) or
  /// stayed zero from the initial disk allocation.
  /// </para>
  /// <para>
  /// Partial coverage: pass 2 still materialises the full disk image
  /// once to reuse the proven metadata path (boot/FAT/bitmap/upcase/
  /// directory tree builder). A fully sparse metadata writer mirroring
  /// the FAT pattern at the boot+FAT sector level is a documented
  /// follow-up. The bar this pass meets — and that the entry-isolation
  /// fuzz checks — is that entry CONTENTS never travel through a byte[]
  /// inside the writer.
  /// </para>
  /// </remarks>
  public void BuildToStreaming(Stream output, int requestedClusterBytes = 0, string? volumeLabel = null) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var disk = BuildAutoSizedCore(requestedClusterBytes, volumeLabel, out var builtTree);
    // BuildAutoSizedCore returns only the written prefix; the volume must be extended
    // to the size its VBR declares or fsck reports "too large sector count".
    output.SetLength(Math.Max(disk.Length, this.DeclaredVolumeBytes));
    output.Position = 0;
    output.Write(disk);

    // Discover cluster heap geometry from the VBR we just wrote so we can
    // seek to each streaming entry's cluster offset.
    var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(88));
    var sectorsPerClusterShift = disk[109];
    const int bytesPerSector = 512;
    var clusterSize = bytesPerSector << sectorsPerClusterShift;
    var clusterHeapOffset = (long)clusterHeapOffsetSectors * bytesPerSector;

    var streamingEntries = new List<(uint StartCluster, long Size, Func<Stream> Opener)>();
    CollectStreamingAllocations(builtTree, streamingEntries);
    var buf = new byte[64 * 1024];
    foreach (var (startCluster, size, opener) in streamingEntries) {
      if (size <= 0) continue;
      var clusterOffset = clusterHeapOffset + (long)(startCluster - 2) * clusterSize;
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
      // Cluster tail past `size` retains zero from the disk init.
    }
    output.Flush();
  }

  private static void CollectStreamingAllocations(DirNode dir,
      List<(uint StartCluster, long Size, Func<Stream> Opener)> sink) {
    foreach (var file in dir.Files)
      if (file.StreamOpener != null && file.StreamingSize is { } sz && sz > 0)
        sink.Add((file.AllocatedStartCluster, sz, file.StreamOpener));
    foreach (var sub in dir.SubDirs)
      CollectStreamingAllocations(sub, sink);
  }

  private byte[] BuildCore(int totalSizeMB, int requestedClusterBytes, string? volumeLabel, out DirNode builtTree) {
    const int bytesPerSector = 512;
    var clusterBytes = requestedClusterBytes > 0 ? requestedClusterBytes : 4096;
    // exFAT requires cluster size to be a power-of-two multiple of the sector size.
    // Clamp to valid range and express as a shift.
    clusterBytes = Math.Max(bytesPerSector, clusterBytes);
    var sectorsPerClusterShift = BitOperations.Log2((uint)(clusterBytes / bytesPerSector));
    var sectorsPerCluster = 1 << sectorsPerClusterShift;
    var clusterSize = bytesPerSector * sectorsPerCluster;
    const int bytesPerSectorShift = 9; // log2(512)
    const int fatOffsetSectors = 24;
    const int fatCount = 1;

    // 64-bit: totalSizeMB * 1024 * 1024 overflows int at 2 GB, and exFAT volumes
    // routinely run far past that -- it is the format of choice for large removable
    // media precisely because FAT32 cannot.
    var totalBytes = (long)totalSizeMB * 1024 * 1024;
    var totalSectors = totalBytes / bytesPerSector;

    // First-pass FAT sizing, then fix-up once we know final cluster count.
    var fatLengthSectors = 1;
    var clusterHeapOffsetSectors = fatOffsetSectors + fatLengthSectors;
    var clusterCount = (totalSectors - clusterHeapOffsetSectors) / sectorsPerCluster;

    var fatBytesNeeded = (clusterCount + 2) * 4;
    fatLengthSectors = (int)((fatBytesNeeded + bytesPerSector - 1) / bytesPerSector);
    clusterHeapOffsetSectors = fatOffsetSectors + fatLengthSectors;
    clusterCount = (totalSectors - clusterHeapOffsetSectors) / sectorsPerCluster;

    // Everything this writer emits -- both boot regions, the FAT, the allocation
    // bitmap, the up-case table, the directory tree and the file data -- lives in a
    // prefix at the start of the volume; nothing is written near the end. So only
    // that prefix is materialised, and the caller extends the file to totalBytes.
    // Allocating totalBytes instead would cap exFAT at the ~2 GB array limit while
    // wasting memory proportional to the volume rather than to its contents.
    // The allocation bitmap holds one bit per cluster and so grows with the volume;
    // its clusters sit between the root directory and the up-case table, and the
    // materialised prefix has to cover them.
    var bitmapSizeBytes = (int)((clusterCount + 7) / 8);
    var bitmapClusterCount = (uint)Math.Max(1, (bitmapSizeBytes + clusterSize - 1) / clusterSize);

    // Only inline payloads land in the prefix. A streaming entry's bytes are
    // copied in by seek afterwards, so counting its clusters here pulled the
    // whole volume back into the buffer and overflowed it past 2 GB.
    var dataClusters = _files.Sum(f => {
      if (f.StreamOpener != null) return 0L;
      var sz = f.StreamingSize ?? (long)f.Data.Length;
      return sz <= 0 ? 1L : (sz + clusterSize - 1) / clusterSize;
    });
    // root dir + allocation bitmap + up-case table, one cluster per directory
    // (bounded above by the file count), the data itself, and headroom.
    var metaClusters = 8L + bitmapClusterCount + _files.Count + dataClusters;
    var heapStart = (long)clusterHeapOffsetSectors * bytesPerSector;
    var prefixBytes = Math.Min(totalBytes, heapStart + (metaClusters + 16) * clusterSize);
    if (prefixBytes < heapStart + clusterSize) prefixBytes = Math.Min(totalBytes, heapStart + clusterSize);

    this.DeclaredVolumeBytes = totalBytes;
    var disk = new byte[prefixBytes];
    var nowStamp = BuildExFatTimestamp(DateTime.UtcNow);
    var volumeSerial = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // Cluster 2 = root dir, then the allocation bitmap, then the up-case table.
    // The bitmap holds one bit per cluster, so it outgrows a single cluster as
    // soon as the volume exceeds clusterSize * 8 clusters -- 128 MB at a 4 KB
    // cluster. Pinning it to one cluster made every larger volume overlap the
    // up-case table, which chkdsk/fsck.exfat reports as "cluster is already
    // allocated for the other file". Size its chain from the cluster count.
    var fatOffset = fatOffsetSectors * bytesPerSector;
    const uint bitmapCluster = 3u;
    var bitmapSize = bitmapSizeBytes;
    var bitmapClusters = bitmapClusterCount;
    var upcaseCluster = bitmapCluster + bitmapClusters;

    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset), 0xFFFFFFF8);    // media type
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 4), EocMarker); // reserved
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + 2 * 4), EocMarker); // root EOC
    // Bitmap chain: each cluster points at the next, the last one terminates.
    for (var i = 0u; i < bitmapClusters; ++i) {
      var entry = bitmapCluster + i;
      var next = i + 1 < bitmapClusters ? entry + 1 : EocMarker;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + (int)entry * 4), next);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + (int)upcaseCluster * 4), EocMarker);

    var nextCluster = upcaseCluster + 1;
    var clusterHeapOffset = clusterHeapOffsetSectors * bytesPerSector;

    // --- Up-Case Table: minimal ASCII identity with upper-case transform. ---
    const int upcaseEntries = 128;
    const int upcaseBytes = upcaseEntries * 2;
    var upcaseOffset = clusterHeapOffset + (int)(upcaseCluster - 2) * clusterSize;
    for (var i = 0; i < upcaseEntries; ++i) {
      var ch = (ushort)(i >= 'a' && i <= 'z' ? i - 32 : i);
      BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(upcaseOffset + i * 2), ch);
    }
    var upcaseChecksum = TableChecksum(disk.AsSpan(upcaseOffset, upcaseBytes));

    // --- Directory tree ---
    // Files whose name carries '/' path separators belong inside a directory
    // tree, not flattened into the root. Build the tree, then in a single
    // depth-first pass allocate a contiguous cluster chain for every directory
    // (root included) and every file, and write each directory's entry sets so
    // a subdirectory's File entry set points at its own cluster chain.
    var root = BuildTree();
    builtTree = root;

    // The root directory occupies cluster 2; the free pool for file data and
    // subdirectory chains starts after bitmap (3) and upcase (4). The volume
    // label, if any, lives as a 0x83 entry inside the root's entry region —
    // WriteDirectory emits it ahead of the Bitmap / UpCase / file entries.
    nextCluster = upcaseCluster + 1;
    WriteDirectory(root, 2u, disk, clusterHeapOffset, clusterSize, fatOffset,
      nowStamp, ref nextCluster,
      bitmapCluster, bitmapSize, upcaseCluster, upcaseBytes, upcaseChecksum,
      volumeLabel);

    // --- Fill Allocation Bitmap ---
    var bitmapOffset = clusterHeapOffset + (int)(bitmapCluster - 2) * clusterSize;
    for (var c = 2u; c < nextCluster; ++c) {
      var bitIndex = (int)(c - 2);
      disk[bitmapOffset + bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
    }

    // --- VBRs (primary + backup) ---
    var usedClusters = nextCluster - 2;
    var percentInUse = clusterCount == 0 ? (byte)0 : (byte)Math.Min(100, usedClusters * 100 / clusterCount);
    WriteVbr(disk, 0, bytesPerSector, bytesPerSectorShift, sectorsPerClusterShift,
      fatOffsetSectors, fatLengthSectors, clusterHeapOffsetSectors,
      (uint)clusterCount, totalSectors, fatCount, volumeSerial, percentInUse);
    WriteVbr(disk, 12 * bytesPerSector, bytesPerSector, bytesPerSectorShift, sectorsPerClusterShift,
      fatOffsetSectors, fatLengthSectors, clusterHeapOffsetSectors,
      (uint)clusterCount, totalSectors, fatCount, volumeSerial, percentInUse);

    // --- Boot Checksum sector (spec §3.1.3) — required by chkdsk.
    // Rotate-right-one-then-add over the 11 sectors of the VBR region excluding
    // bytes 106/107/112 (VolumeFlags and PercentInUse are volatile). Then replicate
    // the 32-bit checksum for the entire sector. Primary at sector 11, backup at 23.
    WriteBootChecksumSector(disk, 0, bytesPerSector);
    WriteBootChecksumSector(disk, 12 * bytesPerSector, bytesPerSector);

    return disk;
  }

  private static void WriteBootChecksumSector(byte[] disk, int vbrOffset, int bytesPerSector) {
    var checksumSectorOffset = vbrOffset + 11 * bytesPerSector;
    if (checksumSectorOffset + bytesPerSector > disk.Length) return;
    uint checksum = 0;
    var spanLen = 11 * bytesPerSector;
    for (var i = 0; i < spanLen; ++i) {
      // Skip VolumeFlags (106/107) and PercentInUse (112) per spec §3.1.3.
      if (i == 106 || i == 107 || i == 112) continue;
      checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + disk[vbrOffset + i];
    }
    for (var i = 0; i < bytesPerSector; i += 4)
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(checksumSectorOffset + i), checksum);
  }

  private static void WriteVbr(byte[] disk, int offset, int bytesPerSector,
    int bytesPerSectorShift, int sectorsPerClusterShift,
    int fatOffsetSectors, int fatLengthSectors, int clusterHeapOffsetSectors,
    uint clusterCount, long totalSectors, int fatCount, uint volumeSerial, byte percentInUse) {
    disk[offset] = 0xEB; disk[offset + 1] = 0x76; disk[offset + 2] = 0x90;
    Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(disk, offset + 3);
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(offset + 64), 0);               // PartitionOffset
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(offset + 72), (ulong)totalSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 80), (uint)fatOffsetSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 84), (uint)fatLengthSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 88), (uint)clusterHeapOffsetSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 92), clusterCount);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 96), 2);                // RootDirCluster
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(offset + 100), volumeSerial);    // VolumeSerialNumber
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(offset + 104), 0x0100);          // FileSystemRevision 1.0
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(offset + 106), 0);               // VolumeFlags
    disk[offset + 108] = (byte)bytesPerSectorShift;
    disk[offset + 109] = (byte)sectorsPerClusterShift;
    disk[offset + 110] = (byte)fatCount;
    disk[offset + 112] = percentInUse;
    disk[offset + 510] = 0x55;
    disk[offset + 511] = 0xAA;
  }

  /// <summary>
  /// exFAT set-checksum per spec §7.4.3 — rotate-right-one-bit-then-add over every
  /// byte of the entry set, skipping bytes 2 and 3 of the first (File) entry which
  /// are the checksum field itself.
  /// </summary>
  private static ushort EntrySetChecksum(ReadOnlySpan<byte> set) {
    ushort checksum = 0;
    for (var i = 0; i < set.Length; ++i) {
      if (i == 2 || i == 3) continue;
      checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]) & 0xFFFF);
    }
    return checksum;
  }

  /// <summary>
  /// Up-case table checksum per spec §7.2.2 — same rotate-add, but uint32 and over the
  /// table bytes directly (no skip).
  /// </summary>
  private static uint TableChecksum(ReadOnlySpan<byte> table) {
    uint checksum = 0;
    foreach (var b in table)
      checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + b;
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
    // exFAT double-seconds-resolution timestamp (same layout as FAT16 DOS date/time).
    uint year = dt.Year >= 1980 ? (uint)(dt.Year - 1980) : 0u;
    uint time = ((uint)dt.Hour << 11) | ((uint)dt.Minute << 5) | ((uint)(dt.Second / 2));
    uint date = (year << 9) | ((uint)dt.Month << 5) | (uint)dt.Day;
    return (date << 16) | time;
  }

  // ── Directory tree ────────────────────────────────────────────────────
  //
  // A FileNode is a leaf carrying file data; a DirNode is an exFAT
  // subdirectory holding ordered child entries (subdirectories first, then
  // files, both in insertion order). The root DirNode models the volume root.

  private sealed class FileNode {
    public required string Name;
    public required byte[] Data;
    /// <summary>When non-null, the file's bytes come from <see cref="StreamOpener"/>
    /// at <see cref="BuildToStreaming"/> time and the writer skips its
    /// in-memory <see cref="Data"/> copy entirely.</summary>
    public long? StreamingSize;
    public Func<Stream>? StreamOpener;
    public long EffectiveLength => this.StreamingSize ?? this.Data.Length;
    /// <summary>Allocation result captured during <see cref="WriteDirectory"/>
    /// so <see cref="BuildToStreaming"/> can stream bytes into the right
    /// cluster offset after metadata is committed.</summary>
    public uint AllocatedStartCluster;
    public int AllocatedClusterCount;
  }

  private sealed class DirNode {
    public required string Name;
    public readonly List<DirNode> SubDirs = [];
    public readonly List<FileNode> Files = [];

    public DirNode GetOrAddSubDir(string name) {
      foreach (var d in this.SubDirs)
        if (d.Name == name) return d;
      var created = new DirNode { Name = name };
      this.SubDirs.Add(created);
      return created;
    }
  }

  /// <summary>
  /// Splits each added file's name on '/' (and '\') into a directory path and a
  /// leaf, creating intermediate <see cref="DirNode"/>s as needed, so the root
  /// holds a real tree instead of flattened slash-bearing names.
  /// </summary>
  private DirNode BuildTree() {
    var root = new DirNode { Name = "" };
    foreach (var (name, data, streamingSize, opener) in _files) {
      var parts = name.Split('/', '\\');
      var dir = root;
      for (var i = 0; i < parts.Length - 1; ++i) {
        if (parts[i].Length == 0) continue; // tolerate leading/double separators
        dir = dir.GetOrAddSubDir(parts[i]);
      }
      var leaf = parts[^1];
      dir.Files.Add(new FileNode {
        Name = leaf,
        Data = data,
        StreamingSize = streamingSize,
        StreamOpener = opener,
      });
    }
    return root;
  }

  /// <summary>Number of 32-byte entries a single File entry set occupies:
  /// the File entry, the Stream Extension, and ceil(len/15) File Name entries.</summary>
  private static int EntrySetCount(string name) {
    var nameLength = name.Length;
    var nameEntries = (nameLength + 14) / 15;
    return 2 + nameEntries; // File + Stream + name entries
  }

  /// <summary>
  /// Lays out and writes one directory's entry sets, recursing depth-first so
  /// every subdirectory gets its own cluster chain before the parent references
  /// it. <paramref name="firstCluster"/> is this directory's already-reserved
  /// starting cluster (cluster 2 for the root). Any further clusters this
  /// directory needs, plus all file-data and subdirectory chains, are taken
  /// from the free pool tracked by <paramref name="nextCluster"/>.
  /// The root additionally carries the Volume Label, Allocation Bitmap and
  /// Up-Case Table system entry sets.
  /// </summary>
  private void WriteDirectory(DirNode dir, uint firstCluster, byte[] disk,
    int clusterHeapOffset, int clusterSize, int fatOffset, uint nowStamp,
    ref uint nextCluster,
    uint bitmapCluster, int bitmapSize,
    uint upcaseCluster, int upcaseBytes, uint upcaseChecksum,
    string? volumeLabel = null) {

    var isRoot = firstCluster == 2;

    // 1. Size this directory's own entry region (in 32-byte entries), so we
    //    know how many clusters it spans and can reserve the overflow ones
    //    before allocating children.
    var entries = 0;
    if (isRoot)
      entries += 3; // VolumeLabel + Bitmap + UpCase system entry sets (1 each)
    foreach (var sub in dir.SubDirs)
      entries += EntrySetCount(sub.Name);
    foreach (var file in dir.Files)
      entries += EntrySetCount(file.Name);

    var dirBytes = entries * 32;
    var dirClusters = Math.Max(1, (dirBytes + clusterSize - 1) / clusterSize);

    // Reserve this directory's cluster chain. The first cluster is given; any
    // overflow clusters come from the pool and must be claimed up-front so
    // children don't reuse them.
    var dirChain = new uint[dirClusters];
    dirChain[0] = firstCluster;
    for (var c = 1; c < dirClusters; ++c)
      dirChain[c] = nextCluster++;

    // 2. Allocate child storage and remember each child's first cluster.
    var subFirst = new uint[dir.SubDirs.Count];
    for (var s = 0; s < dir.SubDirs.Count; ++s)
      subFirst[s] = nextCluster++; // each subdir starts a fresh chain
    var fileFirst = new uint[dir.Files.Count];
    for (var f = 0; f < dir.Files.Count; ++f) {
      var len = dir.Files[f].EffectiveLength;
      var clustersNeeded = Math.Max(1, (int)((len + clusterSize - 1) / clusterSize));
      fileFirst[f] = nextCluster;
      dir.Files[f].AllocatedStartCluster = nextCluster;
      dir.Files[f].AllocatedClusterCount = clustersNeeded;
      nextCluster += (uint)clustersNeeded;
    }

    // 3. Recurse into subdirectories first (depth-first), giving each its
    //    reserved first cluster; this fixes their final on-disk size.
    for (var s = 0; s < dir.SubDirs.Count; ++s)
      WriteDirectory(dir.SubDirs[s], subFirst[s], disk,
        clusterHeapOffset, clusterSize, fatOffset, nowStamp, ref nextCluster,
        bitmapCluster, bitmapSize, upcaseCluster, upcaseBytes, upcaseChecksum);

    // 4. Build this directory's entry region into a contiguous buffer.
    var buffer = new byte[dirClusters * clusterSize];
    var pos = 0;

    if (isRoot) {
      // Volume label entry (0x83). Per exFAT spec §7.3 the slot carries a
      // CharacterCount byte at offset 1 (max 11) followed by up to 11
      // UTF-16LE code units at offset 2. An empty / null label still emits
      // the entry with count 0 (Windows tolerates this — matches the
      // historical "no label" behaviour).
      buffer[pos] = 0x83;
      if (!string.IsNullOrEmpty(volumeLabel)) {
        var labelChars = volumeLabel.Length > 11
          ? volumeLabel[..11].ToCharArray()
          : volumeLabel.ToCharArray();
        buffer[pos + 1] = (byte)labelChars.Length;
        for (var i = 0; i < labelChars.Length; ++i)
          BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2 + i * 2), labelChars[i]);
      } else {
        buffer[pos + 1] = 0;
      }
      pos += 32;

      // Allocation Bitmap entry (0x81)
      buffer[pos] = 0x81;
      buffer[pos + 1] = 0; // BitmapFlags: bit 0 = 0 → first bitmap (only bitmap)
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 20), bitmapCluster);
      BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(pos + 24), bitmapSize);
      pos += 32;

      // Up-Case Table entry (0x82) — with TableChecksum at bytes 4-7.
      buffer[pos] = 0x82;
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 4), upcaseChecksum);
      BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 20), upcaseCluster);
      BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(pos + 24), upcaseBytes);
      pos += 32;
    }

    // Subdirectory File entry sets — directory attribute, stream points at the
    // subdirectory's own cluster chain; DataLength == its full cluster span.
    for (var s = 0; s < dir.SubDirs.Count; ++s) {
      var sub = dir.SubDirs[s];
      var subBytes = SubDirByteLength(sub, clusterSize);
      WriteEntrySet(buffer, ref pos, sub.Name, subFirst[s], subBytes, nowStamp,
        isDirectory: true);
    }

    // File entry sets — archive attribute, stream points at file data.
    for (var f = 0; f < dir.Files.Count; ++f) {
      var file = dir.Files[f];
      WriteEntrySet(buffer, ref pos, file.Name, fileFirst[f], file.EffectiveLength,
        nowStamp, isDirectory: false);
    }

    // 5. Write file data into the cluster heap and chain file clusters.
    for (var f = 0; f < dir.Files.Count; ++f) {
      var file = dir.Files[f];
      var len = file.EffectiveLength;
      var clustersNeeded = Math.Max(1, (int)((len + clusterSize - 1) / clusterSize));
      // Streaming entries leave the cluster heap zero — BuildToStreaming
      // post-fills them from the source in 64 KB chunks once metadata is
      // committed. The cluster-tail past Size stays sparse-zero.
      if (file.StreamOpener == null) {
        var data = file.Data;
        var dataOffset = clusterHeapOffset + (int)(fileFirst[f] - 2) * clusterSize;
        if (data.Length > 0 && dataOffset + data.Length <= disk.Length)
          data.CopyTo(disk, dataOffset);
      }
      for (var c = 0; c < clustersNeeded; ++c) {
        var cluster = fileFirst[f] + (uint)c;
        var nextVal = (c + 1 < clustersNeeded) ? cluster + 1 : EocMarker;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + (int)cluster * 4), nextVal);
      }
    }

    // 6. Spill this directory's entry buffer across its cluster chain and
    //    write the directory's own FAT chain.
    for (var c = 0; c < dirClusters; ++c) {
      var cluster = dirChain[c];
      var clusterOffset = clusterHeapOffset + (int)(cluster - 2) * clusterSize;
      if (clusterOffset + clusterSize <= disk.Length)
        Array.Copy(buffer, c * clusterSize, disk, clusterOffset, clusterSize);
      var nextVal = (c + 1 < dirClusters) ? dirChain[c + 1] : EocMarker;
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fatOffset + (int)cluster * 4), nextVal);
    }
  }

  /// <summary>Total byte length of a subdirectory's on-disk entry region,
  /// rounded up to whole clusters — the value stored in its File entry set's
  /// Stream Extension DataLength / ValidDataLength.</summary>
  private static long SubDirByteLength(DirNode dir, int clusterSize) {
    var entries = 0;
    foreach (var sub in dir.SubDirs)
      entries += EntrySetCount(sub.Name);
    foreach (var file in dir.Files)
      entries += EntrySetCount(file.Name);
    var bytes = entries * 32;
    var clusters = Math.Max(1, (bytes + clusterSize - 1) / clusterSize);
    return (long)clusters * clusterSize;
  }

  /// <summary>
  /// Emits one exFAT entry set (File 0x85 + Stream Extension 0xC0 + File Name
  /// 0xC1 entries) into <paramref name="buffer"/> at <paramref name="pos"/>,
  /// computing the mandatory set checksum. Directories set the directory
  /// attribute (0x10); files set the archive attribute (0x20).
  /// </summary>
  private static void WriteEntrySet(byte[] buffer, ref int pos, string name,
    uint firstCluster, long dataLength, uint nowStamp, bool isDirectory) {
    var nameChars = name.ToCharArray();
    var nameEntries = (nameChars.Length + 14) / 15;
    var secondaryCount = 1 + nameEntries;

    var setStart = pos;

    // File entry (0x85)
    buffer[pos] = 0x85;
    buffer[pos + 1] = (byte)secondaryCount;
    // bytes 2-3 = SetChecksum (written after the set is laid out)
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 4),
      (ushort)(isDirectory ? 0x0010 : 0x0020)); // FileAttributes: directory or archive
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 8), nowStamp);   // CreateTimestamp
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 12), nowStamp);  // LastModifiedTimestamp
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 16), nowStamp);  // LastAccessedTimestamp
    pos += 32;

    // Stream Extension (0xC0)
    buffer[pos] = 0xC0;
    buffer[pos + 1] = 0x01; // AllocationPossible; NoFatChain=0 → chain read from FAT.
    buffer[pos + 3] = (byte)nameChars.Length;
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 4), ComputeNameHash(name));
    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(pos + 8), dataLength);     // ValidDataLength
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(pos + 20), firstCluster);
    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(pos + 24), dataLength);    // DataLength
    pos += 32;

    // File Name entries (0xC1)
    for (var n = 0; n < nameEntries; ++n) {
      buffer[pos] = 0xC1;
      buffer[pos + 1] = 0;
      var startChar = n * 15;
      var charsToWrite = Math.Min(15, nameChars.Length - startChar);
      for (var c = 0; c < charsToWrite; ++c)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(pos + 2 + c * 2), nameChars[startChar + c]);
      pos += 32;
    }

    var setBytes = 32 * (1 + secondaryCount);
    var checksum = EntrySetChecksum(buffer.AsSpan(setStart, setBytes));
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(setStart + 2), checksum);
  }
}
