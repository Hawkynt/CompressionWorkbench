using System.Buffers.Binary;
using System.Text;

namespace FileSystem.HfsPlus;

/// <summary>
/// Creates minimal HFS+ filesystem images per Apple TN1150 ("HFS Plus Volume Format").
/// <para>
/// Produces a 4&#160;MB image with 4&#160;KB block size by default. Files are stored
/// uncompressed in the data fork using single-extent allocation. The catalog file
/// record is the full 248-byte <c>HFSPlusCatalogFile</c> layout with the data fork
/// <c>HFSPlusForkData</c> struct at offset 88 and the resource fork <c>HFSPlusForkData</c>
/// at offset 168, matching TN1150.
/// </para>
/// </summary>
public sealed class HfsPlusWriter {
  private const uint DefaultBlockSize = 4096;
  private const int DefaultImageBlocks = 1024; // 4 MB = 1024 * 4096
  private const int VolumeHeaderOffset = 1024;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsxSignature = 0x4858;    // "HX" — case-sensitive HFSX variant.
  private const ushort HfsPlusVersion = 4;
  private const ushort HfsxVersion = 5;
  private const uint RootFolderCnid = 2;
  private const uint FirstUserCnid = 16;

  // HFS+ B-tree node size. This is fixed independent of the allocation block
  // size (real HFS+ stores it in the B-tree header), so the catalog always
  // holds 4096-byte nodes regardless of how big an allocation block is.
  internal const ushort CatalogNodeSize = 4096;

  // ── Construction-time options (case-sensitive HFSX, journal, name) ───────
  private readonly bool _caseSensitive;
  private readonly bool _journalEnabled;
  private readonly int _journalSize;
  private readonly string _volumeName;

  /// <summary>
  /// Creates a new HFS+ writer.
  /// </summary>
  /// <param name="caseSensitive">When <c>true</c>, emit the HFSX (<c>HX</c>) signature and
  /// binary key comparator so filenames compare case-sensitively. Default <c>false</c> (classic <c>H+</c>).</param>
  /// <param name="journalEnabled">Reserved for forward compatibility. A real journal
  /// (journal info block + journal file) is not emitted yet, so the volume is written
  /// as a non-journaled HFS+ volume regardless — exactly what <c>mkfs.hfsplus</c>
  /// produces without <c>-J</c>, and what <c>fsck.hfsplus</c> accepts as clean.
  /// Setting <c>kHFSVolumeJournaledBit</c> without a valid <c>journalInfoBlock</c> would
  /// make fsck report "Volume header needs minor repair", so the bit stays clear until
  /// journaling is genuinely implemented. Default <c>false</c>.</param>
  /// <param name="journalSize">Journal size in bytes. Reserved for forward compatibility.</param>
  /// <param name="volumeName">Volume name used as the root directory key. Default "Untitled".</param>
  public HfsPlusWriter(bool caseSensitive = false, bool journalEnabled = false,
      int journalSize = 8 * 1024 * 1024, string volumeName = "Untitled") {
    ArgumentNullException.ThrowIfNull(volumeName);
    this._caseSensitive = caseSensitive;
    this._journalEnabled = journalEnabled;
    this._journalSize = journalSize;
    this._volumeName = volumeName;
  }

  // TN1150 HFSPlusCatalogFile layout.
  internal const int CatalogFileRecordSize = 248;
  internal const int CatalogForkDataSize = 80;
  internal const int DataForkOffset = 88;
  internal const int ResourceForkOffset = 168;

  // HFS+ epoch: 1904-01-01T00:00:00Z.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];

  /// <summary>
  /// Streaming-allocations side-effect: when non-null, every streaming
  /// entry's (startBlock, blockCount, size, opener) is appended so
  /// <see cref="BuildToStreaming"/> can post-fill blocks from each source
  /// after metadata is committed.
  /// </summary>
  private List<(uint StartBlock, uint BlockCount, long Size, Func<Stream> Opener)>? _streamingSink;

  /// <summary>Declared size of the volume the last build laid out.</summary>
  private long DeclaredImageBytes { get; set; }

  /// <summary>Where the alternate volume header belongs, and its bytes, on a streaming build.</summary>
  private (long Offset, byte[] Bytes)? AlternateVolumeHeader { get; set; }

  /// <summary>
  /// Adds a file to be included in the volume image.
  /// </summary>
  /// <param name="name">The filename (stored in the root directory).</param>
  /// <param name="data">The file content.</param>
  public void AddFile(string name, byte[] data) => this._files.Add((name, data, null, null));

  /// <summary>
  /// Adds a streaming file: <paramref name="size"/> drives catalog +
  /// extent allocation in pass 1; bytes are pulled from
  /// <paramref name="openStream"/> in pass 2 of
  /// <see cref="BuildToStreaming"/>. Never buffered as <c>byte[]</c>.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    this._files.Add((name, System.Array.Empty<byte>(), size, openStream));
  }

  /// <summary>
  /// Picks the allocation block size that minimises slack + structural overhead
  /// via <see cref="Compression.Core.Layout.FilesystemLayoutOptimizer"/>, then
  /// builds the image. The candidate set is [4&#160;KB … 64&#160;KB]; most HFS+
  /// images stay at 4&#160;KB — the optimizer only bumps the block size for
  /// large-file sets where the bigger allocation unit cuts table overhead.
  /// </summary>
  /// <param name="requestedBlockSize">
  /// Explicit allocation block size in bytes (0 = auto-select). Must be a power
  /// of two &gt;= 512 when non-zero.
  /// </param>
  /// <returns>A byte array containing the HFS+ filesystem image.</returns>
  public byte[] BuildAutoSized(int requestedBlockSize = 0) {
    var fileSizes = this._files.Select(f => f.StreamingSize ?? (long)f.Data.Length).ToList();

    // HFS+ allocation block size is a power of two >= 512. The newfs_hfs default
    // for typical volumes is 4 KB; we offer 4 KB … 64 KB. The B-tree node size
    // (CatalogNodeSize) is fixed independent of the allocation block size, so
    // every candidate produces a reader-parseable layout.
    int[] candidates = [4096, 8192, 16384, 32768, 65536];
    var blockSize = requestedBlockSize > 0
      ? (uint)requestedBlockSize
      : (uint)Compression.Core.Layout.FilesystemLayoutOptimizer.SelectClusterSize(
          candidates,
          bs => {
            var clusters = Compression.Core.Layout.FilesystemLayoutOptimizer.DataClusters(fileSizes, bs);
            var slack    = Compression.Core.Layout.FilesystemLayoutOptimizer.Slack(fileSizes, bs);
            // System overhead at this block size: the allocation bitmap covers
            // every cluster (1 bit each, rounded up to whole bytes), and the
            // catalog + extents B-trees occupy whole allocation blocks (so a
            // larger block enlarges their fixed footprint).
            var totalClusters = clusters + DefaultImageBlocks; // bitmap sizes the whole volume
            var bitmapBytes   = (totalClusters + 7) / 8;
            // Catalog (2 nodes) + extents (1 node) rounded up to blocks.
            var catalogBytes  = (long)CatalogBlocks((uint)bs) * bs;
            var extentsBytes  = (long)bs; // 1 block for the empty extents B-tree
            return slack + bitmapBytes + catalogBytes + extentsBytes;
          });

    return Build(blockSize);
  }

  /// <summary>
  /// Builds and returns the complete HFS+ volume image with the given allocation
  /// block size.
  /// </summary>
  /// <param name="blockSize">
  /// HFS+ allocation block size in bytes. Must be a power of two &gt;= 512.
  /// Defaults to <see cref="DefaultBlockSize"/> (4&#160;KB).
  /// </param>
  /// <returns>A byte array containing the HFS+ filesystem image.</returns>
  public byte[] Build(uint blockSize = DefaultBlockSize) {
    if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(blockSize),
        blockSize, "HFS+ allocation block size must be a power of two >= 512.");

    var dataBlocksNeeded = 0;
    foreach (var entry in this._files) {
      var eff = entry.StreamingSize ?? (long)entry.Data.Length;
      dataBlocksNeeded += (int)((eff + blockSize - 1) / blockSize);
    }

    // Layout per TN1150:
    //   block 0:        boot blocks (sectors 0,1) + primary VHB (sector 2)
    //   block 1:        allocation bitmap (1 block fits up to 32768 alloc blocks)
    //   block 2:        extents-overflow B-tree (1 block, header only — empty)
    //   blocks 3..:     catalog B-tree (header + index + leaf nodes)
    //   blocks ..N:     user file data
    //   block totalBlocks-1: alternate VHB at sector (totalSectors-2)
    const uint AllocBlock = 1;
    const uint ExtentsBlock = 2;
    const uint CatalogStartBlock = 3;
    const ushort nodeSize = CatalogNodeSize;

    // ── Build the catalog leaf records up front ───────────────────────────
    // The number of B-tree nodes (and therefore the catalog fork size and the
    // image geometry) depends on how many records there are and how they pack
    // into leaf nodes, so the tree is planned before any blocks are allocated.
    var catalog = BuildCatalogTree(blockSize, CatalogStartBlock, out var catalogBlockCount,
        out var userDataStartBlock, out var nextBlockAfterData, out var nextCnid,
        out var folderCount);

    var minBlocks = CatalogStartBlock + catalogBlockCount + (uint)dataBlocksNeeded + 1u; // boot+alloc+ext+catalog+data+altVHB
    var totalBlocks = Math.Max((uint)DefaultImageBlocks, minBlocks);
    var imageSize = (long)totalBlocks * blockSize;
    this.DeclaredImageBytes = imageSize;

    // Building for a stream materialises only what is not user file data: every
    // payload sits at or past userDataStartBlock and is placed by seek. The
    // alternate volume header at the tail is written separately.
    var bufferBytes = this._streamingSink != null
      ? Math.Min(imageSize, (long)userDataStartBlock * blockSize)
      : imageSize;
    if (bufferBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"HFS+: a {imageSize:N0}-byte volume exceeds the array limit; write it to a seekable stream instead.");
    var disk = new byte[bufferBytes];

    // HFS+ requires the volume header at sector 2 (offset 1024) AND a byte-
    // identical alternate volume header at sector (totalSectors-2). For an
    // image with 512-byte sectors that's at byte offset (imageSize - 1024).
    // Both copies must carry the H+/HX signature and matching contents — fsck
    // refuses the volume otherwise.
    var alternateVhOffset = imageSize - 1024;

    // ── Volume Header at offset 1024 ──────────────────────────────────────
    var vh = disk.AsSpan(VolumeHeaderOffset);
    // HFSX (case-sensitive) uses signature 'HX' (0x4858) and version 5; the
    // catalog B-tree's keyCompareType then becomes binary (0xBC) instead of
    // case-insensitive (0xCF).
    var sig = this._caseSensitive ? HfsxSignature : HfsPlusSignature;
    var ver = this._caseSensitive ? HfsxVersion : HfsPlusVersion;
    BinaryPrimitives.WriteUInt16BigEndian(vh, sig);
    BinaryPrimitives.WriteUInt16BigEndian(vh[2..], ver);
    // attributes: kHFSVolumeUnmountedBit (0x100) marks a cleanly-unmounted volume.
    // kHFSVolumeJournaledBit (0x2000) is intentionally NOT set: we don't emit a
    // journalInfoBlock + journal yet, and a journaled bit without a valid journal
    // makes fsck.hfsplus report "Volume header needs minor repair". A non-journaled
    // HFS+ volume is fully standard and passes fsck cleanly.
    var attrs = 0x00000100u;
    BinaryPrimitives.WriteUInt32BigEndian(vh[4..], attrs);
    // lastMountedVersion: ASCII "10.0" (the value mkfs.hfsplus writes).
    "10.0"u8.ToArray().CopyTo(vh[8..12]);
    var nowTs = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(vh[16..], nowTs);      // createDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[20..], nowTs);      // modifyDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[28..], nowTs);      // checkedDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[32..], (uint)this._files.Count); // fileCount (root excluded)
    // folderCount @ 36 (root excluded per TN1150) is filled after the catalog
    // tree is built and the subdirectory count is known.
    BinaryPrimitives.WriteUInt32BigEndian(vh[40..], blockSize);
    BinaryPrimitives.WriteUInt32BigEndian(vh[44..], totalBlocks);
    // rsrcClumpSize, dataClumpSize @ 56, 60: TN1150 recommends 64 KB.
    BinaryPrimitives.WriteUInt32BigEndian(vh[56..], 0x10000);    // rsrcClumpSize = 64K
    BinaryPrimitives.WriteUInt32BigEndian(vh[60..], 0x10000);    // dataClumpSize = 64K
    // encodingsBitmap @ 72: bit 0 = MacRoman (the mandatory legacy encoding).
    BinaryPrimitives.WriteUInt64BigEndian(vh[72..], 1UL);

    var catalogStartBlock = CatalogStartBlock;
    var nextBlock = nextBlockAfterData;

    // ── Special-file ForkData descriptors per TN1150 §3.2 ─────────────────
    // VolumeHeader special-file ForkData offsets:
    //   allocationFile @ 112, extentsFile @ 192, catalogFile @ 272,
    //   attributesFile @ 352, startupFile @ 432.
    // Each is a 80-byte HFSPlusForkData:
    //   +0  logicalSize (u64 BE)
    //   +8  clumpSize   (u32 BE)
    //   +12 totalBlocks (u32 BE)
    //   +16 extents[8] (u32 startBlock + u32 blockCount each).
    //
    // Allocation bitmap: 1 block at AllocBlock covers the volume.
    WriteForkData(vh.Slice(112, CatalogForkDataSize),
        (long)blockSize, blockSize, AllocBlock, 1u);

    // Extents-overflow B-tree: 1 block at ExtentsBlock. We don't have any
    // overflow records but the B-tree must exist (header node only). fsck
    // requires this — an "all-zero" extents fork descriptor fails verification.
    WriteForkData(vh.Slice(192, CatalogForkDataSize),
        (long)blockSize, blockSize, ExtentsBlock, 1u);

    // Catalog file: 2 blocks at CatalogStartBlock.
    WriteForkData(vh.Slice(272, CatalogForkDataSize),
        (long)catalogBlockCount * blockSize, blockSize, catalogStartBlock, catalogBlockCount);

    // Attributes B-tree file: not allocated (HFS+ allows empty attributes file).
    WriteForkData(vh.Slice(352, CatalogForkDataSize), 0L, 0u, 0u, 0u);

    // Startup file: empty (only used for special boot scenarios).
    WriteForkData(vh.Slice(432, CatalogForkDataSize), 0L, 0u, 0u, 0u);

    // ── Build extents-overflow B-tree (empty) ─────────────────────────────
    // Required even when empty: fsck.hfsplus refuses a volume whose
    // extentsFile fork descriptor in the volume header has totalBlocks=0.
    // We allocate 1 block (=1 node) at ExtentsBlock containing only a
    // header node with no leaf records.
    {
      var extBase = (int)(ExtentsBlock * blockSize);
      var extHeader = disk.AsSpan(extBase, nodeSize);
      extHeader[8] = 1; // kind = kBTHeaderNode
      extHeader[9] = 0; // height
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[10..], 3); // numRecords = 3

      var ehdr = extHeader[14..];
      BinaryPrimitives.WriteUInt16BigEndian(ehdr, 0);            // treeDepth = 0 (no leaf nodes)
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[2..], 0);       // rootNode = 0 (empty)
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[6..], 0);       // leafRecords = 0
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[10..], 0);      // firstLeafNode
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[14..], 0);      // lastLeafNode
      BinaryPrimitives.WriteUInt16BigEndian(ehdr[18..], nodeSize);
      BinaryPrimitives.WriteUInt16BigEndian(ehdr[20..], 10);     // maxKeyLength = HFSPlusExtentKey size = 10
      // totalNodes must equal the whole fork's capacity (forkBytes / nodeSize),
      // not just the single header node — fsck recomputes it as forkBytes/
      // nodeSize and rejects the B-tree header ("Invalid B-tree header") on a
      // mismatch. The extents fork is 1 allocation block; when the block size
      // exceeds the 4 KB node size that block holds several nodes, all free
      // except the header node.
      var extTotalNodes = (uint)(blockSize / nodeSize);
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[22..], extTotalNodes);     // totalNodes
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[26..], extTotalNodes - 1); // freeNodes (all but header)
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[32..], blockSize); // clumpSize
      ehdr[36] = 0; ehdr[37] = 0;                                 // btreeType, keyCompareType (binary)
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[38..], 2);       // attributes = kBTBigKeysMask only

      // Offset table.
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 2)..], 14);  // BTHeaderRec
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 4)..], 120); // UserDataRec
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 6)..], 248); // BTMapRec
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 8)..], (ushort)(nodeSize - 8));

      // Map: only this header node (node 0) used; the rest of the fork's nodes
      // stay free (their bits are already zero).
      extHeader[248] = 0x80;
    }

    // ── Write catalog B-tree nodes ────────────────────────────────────────
    // The catalog tree (header + index + leaf nodes) was fully planned by
    // BuildCatalogTree; here we just blit each node image into its block.
    var catalogBase = (int)(catalogStartBlock * blockSize);
    for (var n = 0; n < catalog.Nodes.Count; n++)
      catalog.Nodes[n].CopyTo(disk.AsSpan(catalogBase + n * nodeSize, nodeSize));

    // ── Write user file data into its allocation blocks ───────────────────
    foreach (var (startBlock, data) in catalog.FileData) {
      if (data.Length == 0) continue;
      var destOffset = (long)startBlock * blockSize;
      if (this._streamingSink != null) {
        // Inline payloads go through the same post-pass when building for a
        // stream: they sit past the materialised prefix.
        var payload = data;
        this._streamingSink.Add(((uint)startBlock, 0u, payload.LongLength,
          () => new MemoryStream(payload, writable: false)));
        continue;
      }
      if (destOffset + data.Length <= disk.Length)
        data.CopyTo(disk, (int)destOffset);
    }

    // ── Allocation bitmap at AllocBlock ──────────────────────────────────
    // Mark used blocks: 0 (boot+VHB), 1 (alloc), 2 (extents),
    // catalogStartBlock..catalogStartBlock+catalogBlockCount-1 (catalog),
    // userDataStartBlock..nextBlock-1 (user data), and totalBlocks-1
    // (alternate VHB sector resides in the last block).
    var allocBase = (int)(AllocBlock * blockSize);
    void MarkUsed(uint blk) {
      if (blk >= totalBlocks) return;
      var byteIndex = allocBase + (int)(blk / 8);
      var bitIndex = (int)(7 - (blk % 8));
      if (byteIndex < disk.Length)
        disk[byteIndex] |= (byte)(1 << bitIndex);
    }
    // System-reserved blocks.
    MarkUsed(0);
    MarkUsed(AllocBlock);
    MarkUsed(ExtentsBlock);
    for (var b = catalogStartBlock; b < catalogStartBlock + catalogBlockCount; b++) MarkUsed(b);
    // User data blocks.
    for (var b = userDataStartBlock; b < nextBlock; b++) MarkUsed(b);
    // Alt VHB lives in the last allocation block.
    MarkUsed(totalBlocks - 1);

    var usedBlocks = CatalogStartBlock + catalogBlockCount + (uint)dataBlocksNeeded + 1u; // boot+alloc+ext+catalog+data+altVH
    BinaryPrimitives.WriteUInt32BigEndian(vh[48..], totalBlocks - usedBlocks); // freeBlocks
    BinaryPrimitives.WriteUInt32BigEndian(vh[52..], nextBlock); // nextAllocation
    BinaryPrimitives.WriteUInt32BigEndian(vh[36..], folderCount); // folderCount (subdirectories; root excluded)
    BinaryPrimitives.WriteUInt32BigEndian(vh[64..], nextCnid);  // nextCatalogID

    // ── Alternate Volume Header — byte-identical mirror of primary ───────
    // 512-byte block at (imageSize - 1024). Must match the primary so fsck's
    // cross-check passes. We copy the entire 512-byte sector starting at the
    // primary VHB offset (1024..1535).
    // The alternate header lands past the materialised prefix on a streaming
    // build, so it travels with the deferred writes instead.
    if (this._streamingSink != null) {
      var mirror = disk.AsSpan(VolumeHeaderOffset, 512).ToArray();
      this.AlternateVolumeHeader = (alternateVhOffset, mirror);
    } else {
      disk.AsSpan(VolumeHeaderOffset, 512).CopyTo(disk.AsSpan((int)alternateVhOffset, 512));
    }

    return disk;
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 derives allocation-block geometry +
  /// catalog B-tree size from the declared sizes of
  /// <see cref="AddStreamingFile"/> entries; pass 2 emits the volume
  /// header + allocation bitmap + extents B-tree + catalog B-tree
  /// (with file records carrying fork descriptors pointing at the
  /// single-extent runs) + the alternate volume header, then streams
  /// each entry's bytes from its factory into its allocated block run
  /// via 64 KB chunks. Block tail past each entry's exact <c>Size</c>
  /// stays sparse-zero.
  /// </summary>
  /// <remarks>
  /// What's NOT covered (partial): multi-extent fragmented files
  /// (streamed entries use a single contiguous extent — matching the
  /// existing single-pass writer's invariant), B-tree mutation against
  /// the output stream (pass 2 still materialises the full disk
  /// byte[] once to reuse the proven catalog builder). A fully sparse
  /// metadata writer is a documented follow-up. Entry CONTENTS never
  /// travel through a byte[] inside the writer.
  /// </remarks>
  public void BuildToStreaming(Stream output, uint blockSize = DefaultBlockSize) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(uint StartBlock, uint BlockCount, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    byte[] disk;
    uint actualBlockSize;
    try {
      disk = this.Build(blockSize);
      actualBlockSize = blockSize;
    } finally {
      this._streamingSink = null;
    }
    // The buffer is only the metadata prefix now; the volume's declared size is
    // what the stream has to be extended to, and the alternate volume header
    // lands past that prefix.
    output.Position = 0;
    output.Write(disk);
    output.SetLength(this.DeclaredImageBytes);
    if (this.AlternateVolumeHeader is { } alt) {
      output.Position = alt.Offset;
      output.Write(alt.Bytes, 0, alt.Bytes.Length);
    }

    var buf = new byte[64 * 1024];
    foreach (var (startBlock, _, size, opener) in sink) {
      if (size <= 0) continue;
      var byteOffset = (long)startBlock * actualBlockSize;
      if (byteOffset < 0 || byteOffset >= output.Length) continue;
      output.Position = byteOffset;
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

  /// <summary>Two-pass streaming Build with auto-sized allocation block.</summary>
  public void BuildToStreamingAutoSized(Stream output, int requestedBlockSize = 0) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreamingAutoSized requires a writable, seekable stream.", nameof(output));

    var sink = new List<(uint StartBlock, uint BlockCount, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    byte[] disk;
    uint actualBlockSize;
    try {
      disk = this.BuildAutoSized(requestedBlockSize);
      // Read back the block size committed by BuildAutoSized from the
      // volume header at offset 1024 + 40 (4 bytes BE, blockSize field).
      actualBlockSize = BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(VolumeHeaderOffset + 40));
      if (actualBlockSize == 0) actualBlockSize = DefaultBlockSize;
    } finally {
      this._streamingSink = null;
    }
    // The buffer is only the metadata prefix now; the volume's declared size is
    // what the stream has to be extended to, and the alternate volume header
    // lands past that prefix.
    output.Position = 0;
    output.Write(disk);
    output.SetLength(this.DeclaredImageBytes);
    if (this.AlternateVolumeHeader is { } alt) {
      output.Position = alt.Offset;
      output.Write(alt.Bytes, 0, alt.Bytes.Length);
    }

    var buf = new byte[64 * 1024];
    foreach (var (startBlock, _, size, opener) in sink) {
      if (size <= 0) continue;
      var byteOffset = (long)startBlock * actualBlockSize;
      if (byteOffset < 0 || byteOffset >= output.Length) continue;
      output.Position = byteOffset;
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

  // ── Catalog B-tree planning ──────────────────────────────────────────────

  /// <summary>
  /// The fully planned catalog B-tree: the byte image of every node (node 0 is
  /// the header node, followed by leaf nodes and index nodes) plus the user
  /// file-data placements so the caller can blit both into the image.
  /// </summary>
  private sealed class CatalogTree {
    public required List<byte[]> Nodes { get; init; }
    public required List<(uint StartBlock, byte[] Data)> FileData { get; init; }
  }

  /// <summary>
  /// Resolves the folder hierarchy, builds and sorts all catalog records, packs
  /// them into one or more leaf nodes (chained via fLink/bLink), builds the
  /// index level(s) above the leaves, and emits the B-tree header node with a
  /// node-allocation bitmap covering every node. A single leaf still collapses
  /// to the classic 2-node tree (header + leaf, treeDepth 1); larger catalogs
  /// grow extra leaves plus index nodes so a directory with thousands of entries
  /// round-trips.
  /// </summary>
  private CatalogTree BuildCatalogTree(uint blockSize, uint catalogStartBlock,
      out uint catalogBlockCount, out uint userDataStartBlock,
      out uint nextBlockAfterData, out uint nextCnid, out uint folderCount) {
    const ushort nodeSize = CatalogNodeSize;

    nextCnid = FirstUserCnid;

    // ── Resolve the folder hierarchy implied by the slash-separated names ──
    // Each file name may carry intermediate folders ("docs/api/reference.txt").
    // We materialise one folder record + folder thread record per distinct
    // directory and place every file under its real parent folder CNID, so the
    // reader rebuilds the nested path from the catalog rather than seeing a flat
    // root with embedded slashes.
    //
    // Folders are allocated CNIDs in discovery order, which guarantees a parent
    // folder always receives a smaller CNID than its children. Because catalog
    // leaf records are sorted by (parentCNID, name), this keeps every folder
    // record ahead of its descendants — the order the reader relies on to
    // resolve parent paths in a single forward pass over the leaf chain.
    var folderCnids = new Dictionary<string, uint> { [""] = RootFolderCnid };
    var folderValence = new Dictionary<uint, uint> { [RootFolderCnid] = 0 };
    var folderInfo = new Dictionary<uint, (uint Parent, string Name)>();
    var localNextCnid = nextCnid;

    uint EnsureFolder(string path) {
      if (folderCnids.TryGetValue(path, out var existing))
        return existing;

      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? "" : path[..slash];
      var leaf = slash < 0 ? path : path[(slash + 1)..];
      var parentCnid = EnsureFolder(parentPath);

      var cnid = localNextCnid++;
      folderCnids[path] = cnid;
      folderValence[cnid] = 0;
      folderInfo[cnid] = (parentCnid, leaf);
      folderValence[parentCnid]++;
      return cnid;
    }

    foreach (var entry in this._files) {
      var normalized = entry.Name.Replace('\\', '/').Trim('/');
      var slash = normalized.LastIndexOf('/');
      if (slash >= 0)
        EnsureFolder(normalized[..slash]);
    }

    // ── File data placement ───────────────────────────────────────────────
    // User data begins right after the catalog fork. The catalog fork size is
    // unknown until the node count is known, but node packing needs the file
    // start blocks. We resolve this in two passes: first compute how many leaf
    // and index nodes the records need, then assign data blocks.
    var fileData = new List<(uint StartBlock, byte[] Data)>();

    // Pass 1: build records to discover the node count. File records reference
    // start blocks, so we use placeholder start blocks here and rewrite the
    // fork descriptors once the real layout is known. To avoid a second record
    // build we instead compute the catalog node count from the record sizes and
    // only assign start blocks afterward — so we build records lazily below.
    var fileMeta = new List<(uint Cnid, uint Parent, string LeafName, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener, uint BlockCount, long EffectiveLength)>();
    foreach (var (rawName, data, streamingSize, opener) in this._files) {
      var normalized = rawName.Replace('\\', '/').Trim('/');
      var slash = normalized.LastIndexOf('/');
      var parentPath = slash < 0 ? "" : normalized[..slash];
      var leafName = slash < 0 ? normalized : normalized[(slash + 1)..];
      var parentCnid = folderCnids[parentPath];
      var effLen = streamingSize ?? (long)data.Length;
      var blockCount = (uint)((effLen + blockSize - 1) / blockSize);
      var fileCnid = localNextCnid++;
      folderValence[parentCnid]++;
      fileMeta.Add((fileCnid, parentCnid, leafName, data, streamingSize, opener, blockCount, effLen));
    }

    nextCnid = localNextCnid;

    // ── Compute the catalog fork size (node count) ─────────────────────────
    // Catalog node count = 1 header + leaves + index nodes. To size the fork we
    // need the leaf count, which depends on record sizes — and the record sizes
    // for files do not depend on the (yet unknown) start block. So we build the
    // records with the correct sizes using a provisional start block of 0, pack
    // them to count nodes, derive the fork size, assign real start blocks, then
    // rewrite only the fork-descriptor bytes inside the already-packed records.

    // Build all records (sorted) with provisional file start blocks = 0.
    // File records carry the fork-descriptor patch offset and their data so the
    // real start block can be written in once the catalog fork size is known;
    // non-file records leave ForkPatchOffset = -1 and Data = null.
    var VolumeName = string.IsNullOrEmpty(this._volumeName) ? "untitled" : this._volumeName;
    var keyed = new List<(uint Parent, string Name, byte[] Bytes, int ForkPatchOffset, byte[]? Data, long? StreamingSize, Func<Stream>? StreamOpener, uint BlockCount)>();

    // Root folder + thread.
    keyed.Add((1u, VolumeName,
        BuildFolderRecord(RootFolderCnid, 1u, folderValence[RootFolderCnid], VolumeName), -1, null, null, null, 0u));
    keyed.Add((RootFolderCnid, "", BuildFolderThreadRecord(RootFolderCnid, 1, VolumeName), -1, null, null, null, 0u));

    folderCount = 0u;
    foreach (var (cnid, info) in folderInfo) {
      ++folderCount;
      keyed.Add((info.Parent, info.Name,
          BuildFolderRecord(cnid, info.Parent, folderValence[cnid], info.Name), -1, null, null, null, 0u));
      keyed.Add((cnid, "", BuildFolderThreadRecord(cnid, info.Parent, info.Name), -1, null, null, null, 0u));
    }

    foreach (var fm in fileMeta) {
      var rec = BuildFileRecord(fm.Cnid, fm.Parent, fm.LeafName, fm.EffectiveLength, 0u, fm.BlockCount);
      // Offset of extents[0].startBlock inside the record = keyLength prefix +
      // key body + DataForkOffset + 16 (logicalSize+clumpSize+totalBlocks).
      var keyLen = BinaryPrimitives.ReadUInt16BigEndian(rec) + 2;
      var patchOffset = keyLen + DataForkOffset + 16;
      keyed.Add((fm.Parent, fm.LeafName, rec, patchOffset, fm.Data, fm.StreamingSize, fm.StreamOpener, fm.BlockCount));
      // File thread record.
      keyed.Add((fm.Cnid, "", BuildFileThreadRecord(fm.Cnid, fm.Parent, fm.LeafName), -1, null, null, null, 0u));
    }

    // Sort by HFS+ catalog key: parentCNID first, then the name under the
    // volume's declared keyCompareType. The catalog header advertises
    // kHFSCaseFolding (0xCF), so the name order must follow TN1150's
    // case-folding FastUnicodeCompare — not a raw UTF-16 byte compare, which
    // would put 'Z' before 'a' and make fsck report "Keys out of order".
    keyed.Sort((a, b) => {
      if (a.Parent != b.Parent) return a.Parent.CompareTo(b.Parent);
      return HfsPlusCaseFold.Compare(a.Name, b.Name);
    });

    var records = new List<byte[]>(keyed.Count);
    foreach (var kr in keyed) records.Add(kr.Bytes);

    // ── Pack records into leaf nodes ───────────────────────────────────────
    // A node's usable record area is nodeSize - 14 (descriptor); each record
    // also costs a 2-byte offset-table slot, and one extra slot stores the
    // free-space marker. Record bodies are padded to a 2-byte boundary.
    var leafGroups = PackRecords(records, nodeSize);
    var leafCount = leafGroups.Count;

    // Node numbering: node 0 = header, nodes 1..leafCount = leaves, then index
    // nodes. We build index levels bottom-up; the first index level points to
    // the leaves, higher levels point to lower index nodes.
    var firstLeafNodeNumber = 1u;

    // ── Build the index level(s) ───────────────────────────────────────────
    // Each index record = full catalog key of the child's first record + a u32
    // child node number (2-byte aligned). One index node may not hold pointers
    // to every leaf, so index levels are built repeatedly until a single root
    // node remains.
    var nextNodeNumber = firstLeafNodeNumber + (uint)leafCount;

    // The first-record key of each leaf (key bytes WITHOUT the 2-byte length
    // prefix already include it; here we keep the full key incl. length prefix
    // for the index entry, matching kBTBigKeysMask layout).
    var leafFirstKeys = new List<byte[]>(leafCount);
    foreach (var group in leafGroups) {
      var first = group[0];
      var keyLen = BinaryPrimitives.ReadUInt16BigEndian(first) + 2;
      leafFirstKeys.Add(first[..keyLen]);
    }

    uint treeDepth;
    uint rootNode;
    var indexNodeImages = new List<(uint NodeNumber, byte[] Image)>();

    if (leafCount == 1) {
      // Single leaf: classic 2-node tree, the leaf is the root, depth 1.
      treeDepth = 1;
      rootNode = firstLeafNodeNumber;
    } else {
      // Build index levels over the children below.
      var childKeys = leafFirstKeys;
      var childNodes = new List<uint>();
      for (var i = 0; i < leafCount; i++) childNodes.Add(firstLeafNodeNumber + (uint)i);
      var level = 0;

      while (true) {
        // Pack (key, childNode) entries into index nodes.
        var entries = new List<(byte[] Key, uint Child)>(childKeys.Count);
        for (var i = 0; i < childKeys.Count; i++) entries.Add((childKeys[i], childNodes[i]));

        var groups = PackIndexEntries(entries, nodeSize);
        var thisLevelNodes = new List<uint>();
        var thisLevelKeys = new List<byte[]>();

        // Node numbers for this level are assigned contiguously so the
        // forward/backward sibling chain can be computed up front. Every node
        // at one B-tree level must be linked to its neighbours via fLink/bLink
        // (first node bLink = 0, last node fLink = 0); fsck rejects the volume
        // with "Invalid sibling link" when index nodes are left unlinked.
        var levelBaseNode = nextNodeNumber;
        for (var g = 0; g < groups.Count; g++) {
          var nodeNo = nextNodeNumber++;
          thisLevelNodes.Add(nodeNo);
          thisLevelKeys.Add(groups[g][0].Key);
          var fLink = g + 1 < groups.Count ? levelBaseNode + (uint)(g + 1) : 0u;
          var bLink = g > 0 ? levelBaseNode + (uint)(g - 1) : 0u;
          var img = BuildIndexNode(groups[g], nodeSize, (byte)(2 + level), fLink, bLink);
          indexNodeImages.Add((nodeNo, img));
        }

        if (groups.Count == 1) {
          rootNode = thisLevelNodes[0];
          treeDepth = (uint)(2 + level); // leaves are depth 1
          break;
        }

        childKeys = thisLevelKeys;
        childNodes = thisLevelNodes;
        ++level;
      }
    }

    var usedNodes = nextNodeNumber; // node numbers 0..nextNodeNumber-1 are used
    catalogBlockCount = (uint)(((long)usedNodes * nodeSize + blockSize - 1) / blockSize);
    // The catalog fork occupies whole allocation blocks. When the block size
    // exceeds the 4 KB node size, the fork's byte capacity holds more node
    // slots than we actually use. fsck.hfsplus computes the B-tree's node count
    // as forkBytes / nodeSize and rejects the header ("Invalid B-tree header")
    // unless totalNodes matches that capacity, with the surplus marked free.
    var nodeCapacity = (uint)((long)catalogBlockCount * blockSize / nodeSize);
    var totalNodes = Math.Max(usedNodes, nodeCapacity);

    // ── Assign user-data start blocks now that the fork size is known ──────
    // Walk the sorted records; every file record (ForkPatchOffset >= 0) gets the
    // next run of data blocks and its data-fork extent patched in place.
    userDataStartBlock = catalogStartBlock + catalogBlockCount;
    nextBlockAfterData = userDataStartBlock;
    for (var i = 0; i < keyed.Count; i++) {
      var entry = keyed[i];
      if (entry.ForkPatchOffset < 0) continue;
      var startBlock = nextBlockAfterData;
      var rec = records[i];
      BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(entry.ForkPatchOffset), startBlock);
      BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(entry.ForkPatchOffset + 4), entry.BlockCount);
      if (entry.StreamOpener != null && entry.StreamingSize is { } sz && sz > 0) {
        // Streaming entry: record the (startBlock, blockCount, size, opener)
        // tuple for BuildToStreaming's post-fill pass; leave the data area
        // zero so cluster tail past Size stays sparse-zero.
        this._streamingSink?.Add((startBlock, entry.BlockCount, sz, entry.StreamOpener));
      } else if (entry.Data is { Length: > 0 }) {
        fileData.Add((startBlock, entry.Data));
      }
      nextBlockAfterData += entry.BlockCount;
    }

    // ── Emit leaf node images (now that start blocks are patched) ──────────
    var leafImages = new byte[leafCount][];
    for (var i = 0; i < leafCount; i++) {
      var fLink = i + 1 < leafCount ? firstLeafNodeNumber + (uint)(i + 1) : 0u;
      var bLink = i > 0 ? firstLeafNodeNumber + (uint)(i - 1) : 0u;
      leafImages[i] = BuildLeafNode(leafGroups[i], nodeSize, fLink, bLink);
    }

    // ── Assemble all node images in node-number order ──────────────────────
    // Only the used nodes get an image; surplus fork-capacity slots (when the
    // allocation block size exceeds the node size) stay zero on disk and are
    // flagged free in the header bitmap.
    var nodes = new byte[usedNodes][];
    nodes[0] = BuildCatalogHeaderNode(nodeSize, treeDepth, rootNode,
        (uint)records.Count, firstLeafNodeNumber,
        firstLeafNodeNumber + (uint)leafCount - 1, totalNodes, usedNodes);
    for (var i = 0; i < leafCount; i++)
      nodes[firstLeafNodeNumber + i] = leafImages[i];
    foreach (var (nodeNo, img) in indexNodeImages)
      nodes[nodeNo] = img;

    return new CatalogTree {
      Nodes = [.. nodes],
      FileData = fileData,
    };
  }

  /// <summary>
  /// Greedily packs leaf records into the smallest run of leaf nodes that fits.
  /// Each node reserves 14 bytes for the descriptor and 2 bytes per record (plus
  /// one free-space slot) for the trailing offset table; record bodies pad to a
  /// 2-byte boundary. Returns one list of records per leaf node, in order.
  /// </summary>
  private static List<List<byte[]>> PackRecords(List<byte[]> records, int nodeSize) {
    var groups = new List<List<byte[]>>();
    var current = new List<byte[]>();
    var used = 14; // node descriptor

    foreach (var rec in records) {
      var padded = rec.Length + (rec.Length & 1);
      // Cost of adding: record body (padded) + its offset slot, while still
      // leaving room for the free-space offset slot.
      var slots = 2 * (current.Count + 1) + 2; // existing+new offsets + free slot
      if (current.Count > 0 && used + padded + slots > nodeSize) {
        groups.Add(current);
        current = [];
        used = 14;
      }
      current.Add(rec);
      used += padded;
    }
    if (current.Count > 0) groups.Add(current);
    return groups;
  }

  /// <summary>
  /// Packs index entries (full catalog key + 4-byte child pointer) into index
  /// nodes the same way leaves are packed.
  /// </summary>
  private static List<List<(byte[] Key, uint Child)>> PackIndexEntries(
      List<(byte[] Key, uint Child)> entries, int nodeSize) {
    var groups = new List<List<(byte[] Key, uint Child)>>();
    var current = new List<(byte[] Key, uint Child)>();
    var used = 14;

    foreach (var e in entries) {
      var recLen = e.Key.Length + 4;            // key + child pointer
      var padded = recLen + (recLen & 1);
      var slots = 2 * (current.Count + 1) + 2;
      if (current.Count > 0 && used + padded + slots > nodeSize) {
        groups.Add(current);
        current = [];
        used = 14;
      }
      current.Add(e);
      used += padded;
    }
    if (current.Count > 0) groups.Add(current);
    return groups;
  }

  /// <summary>Lays out a leaf node image (kind = -1, height = 1).</summary>
  private static byte[] BuildLeafNode(List<byte[]> records, int nodeSize, uint fLink, uint bLink) {
    var node = new byte[nodeSize];
    BinaryPrimitives.WriteUInt32BigEndian(node, fLink);
    BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(4), bLink);
    node[8] = 0xFF; // kind = kBTLeafNode (-1)
    node[9] = 1;    // height
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10), (ushort)records.Count);

    var writePos = 14;
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      rec.CopyTo(node, writePos);
      BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 2 * (i + 1)), (ushort)writePos);
      writePos += rec.Length;
      if ((writePos & 1) != 0) writePos++;
    }
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 2 * (records.Count + 1)), (ushort)writePos);
    return node;
  }

  /// <summary>
  /// Lays out an index node image (kind = 0). Each record is a full catalog key
  /// (with its 2-byte length prefix) followed by a 4-byte child node number.
  /// <paramref name="fLink"/>/<paramref name="bLink"/> chain the node to its
  /// siblings at the same B-tree level (0 at the ends of the chain).
  /// </summary>
  private static byte[] BuildIndexNode(List<(byte[] Key, uint Child)> entries, int nodeSize, byte height, uint fLink, uint bLink) {
    var node = new byte[nodeSize];
    BinaryPrimitives.WriteUInt32BigEndian(node, fLink);
    BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(4), bLink);
    node[8] = 0;       // kind = kBTIndexNode (0)
    node[9] = height;  // height above the leaves + 1
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10), (ushort)entries.Count);

    var writePos = 14;
    for (var i = 0; i < entries.Count; i++) {
      var (key, child) = entries[i];
      key.CopyTo(node, writePos);
      BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(writePos + key.Length), child);
      BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 2 * (i + 1)), (ushort)writePos);
      writePos += key.Length + 4;
      if ((writePos & 1) != 0) writePos++;
    }
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 2 * (entries.Count + 1)), (ushort)writePos);
    return node;
  }

  /// <summary>
  /// Lays out the catalog B-tree header node (node 0) with the BTHeaderRec, a
  /// 128-byte UserDataRec, and a BTMapRec whose bits mark every allocated node
  /// (and map nodes if the bitmap overflows the header node). For the node
  /// counts this writer produces (catalogs of a few thousand nodes) the bitmap
  /// always fits the header node's map record.
  /// </summary>
  private static byte[] BuildCatalogHeaderNode(int nodeSize, uint treeDepth, uint rootNode,
      uint leafRecords, uint firstLeafNode, uint lastLeafNode, uint totalNodes, uint usedNodes) {
    var node = new byte[nodeSize];
    node[8] = 1; // kind = kBTHeaderNode
    node[9] = 0; // height
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(10), 3); // numRecords

    var hdr = node.AsSpan(14);
    BinaryPrimitives.WriteUInt16BigEndian(hdr, (ushort)treeDepth);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], rootNode);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], leafRecords);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], firstLeafNode);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], lastLeafNode);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[18..], (ushort)nodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[20..], 516); // maxKeyLength (HFS+ catalog)

    // The map record in the header node covers (nodeSize - 256) * 8 nodes; if
    // the catalog ever needed more nodes than that, dedicated map nodes would
    // follow. The catalogs produced here stay well under that ceiling, so every
    // node is recorded in the header map and there are no free nodes.
    var mapBytesInHeader = nodeSize - 256;
    var mapCapacity = (uint)(mapBytesInHeader * 8);
    if (totalNodes > mapCapacity)
      throw new NotSupportedException(
        $"HFS+ catalog requires {totalNodes} nodes, exceeding the {mapCapacity}-node "
        + "single-header-map limit; map nodes are not implemented.");

    BinaryPrimitives.WriteUInt32BigEndian(hdr[22..], totalNodes);              // totalNodes (fork capacity)
    BinaryPrimitives.WriteUInt32BigEndian(hdr[26..], totalNodes - usedNodes);  // freeNodes (surplus slots)
    hdr[36] = 0;    // btreeType = kHFSBTreeType
    hdr[37] = 0xCF; // keyCompareType = kHFSBinaryCompare
    // attributes: kBTBigKeysMask (2) | kBTVariableIndexKeysMask (4) = 6.
    BinaryPrimitives.WriteUInt32BigEndian(hdr[38..], 6);

    // Record offsets (reverse from end of node):
    //   #0 BTHeaderRec @ 14, #1 UserDataRec @ 120, #2 BTMapRec @ 248,
    //   free-space marker @ end of the map record.
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 2), 14);
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 4), 120);
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 6), 248);
    BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(nodeSize - 8), (ushort)(nodeSize - 8));

    // Map record at offset 248: one bit per node, MSB = lowest node. Mark only
    // the first `usedNodes` bits used; any surplus fork-capacity nodes stay free.
    for (var n = 0u; n < usedNodes; n++) {
      var byteIndex = 248 + (int)(n / 8);
      var bitIndex = 7 - (int)(n % 8);
      node[byteIndex] |= (byte)(1 << bitIndex);
    }

    return node;
  }

  // ── Record builders ─────────────────────────────────────────────────────

  private static byte[] BuildCatalogKey(uint parentCnid, string name) {
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    var keyLen = (ushort)(4 + 2 + nameBytes.Length);
    var key = new byte[2 + keyLen];
    BinaryPrimitives.WriteUInt16BigEndian(key, keyLen);
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(6), nameLen);
    nameBytes.CopyTo(key, 8);
    return key;
  }

  private static byte[] BuildFolderRecord(uint cnid, uint parentCnid, uint valence, string name) {
    // For the root folder, key = (parentID=1, volumeName). For other folders,
    // key = (parentID, folderName).
    var key = BuildCatalogKey(parentCnid, name);
    // TN1150 HFSPlusCatalogFolder = 88 bytes min (recordType + flags + valence +
    // folderID + dates + perms + userInfo + finderInfo + textEncoding + reserved).
    var recData = new byte[88];
    BinaryPrimitives.WriteInt16BigEndian(recData, 1);                 // recordType = kHFSPlusFolderRecord
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), valence);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(8), cnid);   // folderID
    var now = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(12), now);   // createDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(16), now);   // contentModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(20), now);   // attributeModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(24), now);   // accessDate

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  /// <summary>
  /// Builds a folder thread record (recordType=3). The key is (myCNID, "")
  /// and the body contains parentCnid + my name.
  /// </summary>
  private static byte[] BuildFolderThreadRecord(uint folderCnid, uint parentCnid, string name) {
    // Thread record key uses the FOLDER's own CNID.
    var key = BuildCatalogKey(folderCnid, "");
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    var recData = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt16BigEndian(recData, 3); // kHFSPlusFolderThreadRecord
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(8), nameLen);
    nameBytes.CopyTo(recData, 10);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  private static byte[] BuildFileThreadRecord(uint fileCnid, uint parentCnid, string name) {
    var key = BuildCatalogKey(fileCnid, "");
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    var recData = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt16BigEndian(recData, 4); // kHFSPlusFileThreadRecord
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(8), nameLen);
    nameBytes.CopyTo(recData, 10);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  /// <summary>
  /// Emits a full 248-byte TN1150 <c>HFSPlusCatalogFile</c> record with the data
  /// fork <c>HFSPlusForkData</c> at offset 88 (relative to the record body, i.e.
  /// after the catalog key) and the resource fork at offset 168.
  /// </summary>
  private static byte[] BuildFileRecord(uint fileCnid, uint parentCnid, string name,
      long logicalSize, uint startBlock, uint blockCount) {
    var key = BuildCatalogKey(parentCnid, name);
    var recData = new byte[CatalogFileRecordSize];

    // Header fields.
    BinaryPrimitives.WriteInt16BigEndian(recData, 2);                  // recordType = kHFSPlusFileRecord
    // flags = kHFSThreadExistsMask (0x0002) — required because we always
    // emit a paired file thread record. Without this, fsck reports
    // "Incorrect number of thread records" or "Invalid catalog record type".
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(2), 0x0002);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), 0);       // reserved1
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(8), fileCnid);// fileID
    var now = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(12), now);    // createDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(16), now);    // contentModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(20), now);    // attributeModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(24), now);    // accessDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(28), 0);      // backupDate
    // permissions[16] at offset 32 — zeros (owner=0, group=0, mode=0 → unspecified).
    // userInfo[16] at offset 48 (FileInfo) — zeros.
    // finderInfo[16] at offset 64 (ExtendedFileInfo) — zeros.
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(80), 0);      // textEncoding
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(84), 0);      // reserved2

    // HFSPlusForkData dataFork at offset 88 (80 bytes).
    WriteForkData(recData.AsSpan(DataForkOffset, CatalogForkDataSize), logicalSize, DefaultBlockSize, startBlock, blockCount);

    // HFSPlusForkData resourceFork at offset 168 (80 bytes). Empty for our writer.
    WriteForkData(recData.AsSpan(ResourceForkOffset, CatalogForkDataSize), 0, DefaultBlockSize, 0, 0);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  /// <summary>
  /// Writes an <c>HFSPlusForkData</c> struct (80 bytes):
  /// logicalSize (u64) + clumpSize (u32) + totalBlocks (u32) + 8 extents (u32 startBlock + u32 blockCount).
  /// </summary>
  private static void WriteForkData(Span<byte> dst, long logicalSize, uint clumpSize, uint startBlock, uint blockCount) {
    BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)logicalSize); // offset 0
    BinaryPrimitives.WriteUInt32BigEndian(dst[8..], clumpSize);     // offset 8
    BinaryPrimitives.WriteUInt32BigEndian(dst[12..], blockCount);   // offset 12 — totalBlocks
    // extents[0] at offset 16.
    BinaryPrimitives.WriteUInt32BigEndian(dst[16..], startBlock);
    BinaryPrimitives.WriteUInt32BigEndian(dst[20..], blockCount);
    // Remaining 7 extent descriptors are zero (no fragmentation in our writer).
  }

  // Number of allocation blocks the catalog fork occupies: enough to hold the
  // two fixed-size B-tree nodes (header + leaf). At 4 KB blocks → 2; at >= 8 KB
  // blocks both nodes fit in a single allocation block → 1.
  private static uint CatalogBlocks(uint blockSize)
    => (uint)((2 * CatalogNodeSize + blockSize - 1) / blockSize);

  private static uint HfsTimestamp(DateTime dt) {
    if (dt < HfsEpoch) return 0;
    var seconds = (dt - HfsEpoch).TotalSeconds;
    return seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }
}
