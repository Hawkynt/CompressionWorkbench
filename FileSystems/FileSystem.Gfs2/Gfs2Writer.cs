#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Gfs2;

/// <summary>
/// Clean-room GFS2 (Global File System 2) image writer producing a minimal,
/// empty, standalone (<c>lock_nolock</c>, single-journal) volume that real
/// <c>fsck.gfs2</c> (gfs2-utils) accepts without errors.
///
/// <para>The output mirrors the on-disk structures defined in the public Linux
/// kernel header <c>include/uapi/linux/gfs2_ondisk.h</c> and the layout produced
/// by <c>mkfs.gfs2</c>, reverse-validated byte-for-byte against a real reference
/// image. Big-endian throughout, 4096-byte blocks.</para>
///
/// <para>What we emit (everything <c>fsck.gfs2</c> requires for a clean volume):</para>
/// <list type="bullet">
///   <item><description>Superblock at byte 65536 (block 16).</description></item>
///   <item><description>A single resource group whose inline bitmap covers every
///   data block and correctly marks each used metadata block.</description></item>
///   <item><description>Master directory dinode and the system inodes hung off it:
///   <c>jindex</c>, <c>per_node</c>, <c>inum</c>, <c>statfs</c>, <c>rindex</c>,
///   <c>quota</c>.</description></item>
///   <item><description>A formatted 8&#160;MB journal (<c>journal0</c>) whose 2048
///   blocks each carry a clean unmount log header (correct <c>lh_hash</c> CRC32
///   and <c>lh_crc</c> CRC32C).</description></item>
///   <item><description><c>per_node</c> system inodes <c>inum_range0</c>,
///   <c>statfs_change0</c>, <c>quota_change0</c> (the latter a 1&#160;MB file of
///   empty quota-change blocks).</description></item>
///   <item><description>The root directory dinode with <c>.</c> and <c>..</c>.</description></item>
/// </list>
///
/// <para>Block-accounting fields (<c>rg_free</c>, <c>rg_dinodes</c>, the master
/// <c>statfs</c>, the <c>inum</c> next-formal-number) are all computed from the
/// real layout so <c>check_statfs</c> passes.</para>
/// </summary>
public sealed class Gfs2Writer {
  private const int BlockSize = 4096;
  private const int BlockShift = 12;

  private const uint MetaMagic = 0x01161970u;

  // gfs2_meta_header.mh_type
  private const uint MtSb = 1, MtRg = 2, MtRb = 3, MtDinode = 4, MtIndirect = 5,
                     MtLogHeader = 8, MtQuotaChange = 14;

  // gfs2_meta_header.mh_format (per-type format version)
  private const uint FmtSb = 100, FmtRg = 200, FmtRb = 300, FmtDinode = 400,
                     FmtIndirect = 500, FmtLogHeader = 800, FmtQuotaChange = 1400;

  // Superblock format constants.
  private const uint FormatFs = 1802, FormatMulti = 1900;

  // di_payload_format values for the data carried inline in a dinode.
  private const uint PfDirent = 1200, PfRindex = 1100, PfQuota = 1500;

  // di_flags bits.
  private const uint DifJData = 0x00000001;
  private const uint DifSystem = 0x00000200;

  // Directory entry de_type (matches DT_*) values.
  private const ushort DtDir = 4, DtRegular = 8;

  // Resource-group block states (2 bits/block, 4 blocks/byte, LSB-first).
  private const int BlkstUsed = 1, BlkstDinode = 3;

  // Mode bits.
  private const uint SIfDir = 0x4000, SIfReg = 0x8000;

  // Journal sizing: 8 MB == 2048 blocks (mkfs.gfs2 minimum journal).
  private const int JournalBlocks = 2048;

  // quota_change file: 1 MB == 256 blocks of empty quota-change metadata.
  private const int QuotaChangeBlocks = 256;

  private const int DinodeHeaderSize = 232; // sizeof(struct gfs2_dinode)
  private const int DirentSize = 40;        // sizeof(struct gfs2_dirent) on disk
  private const int IndPointerBase = 24;    // pointers begin right after meta header
  private const int PointersPerIndirect = (BlockSize - IndPointerBase) / 8; // 509

  private readonly SparseBlockImage _img;
  private readonly long _totalBlocks;
  private readonly byte[] _uuid;
  private readonly ulong _baseTime;

  // Two resource groups, matching the gfs2-utils convention: a small first RG
  // holding the journal + master + jindex, and a second RG covering the rest of
  // the device. fsck.gfs2 validates the rindex against this exact shape (the
  // first RG is always 2057 blocks: 1 header + 2056 data). With only one *data*
  // resource group, fsck's even-spacing check is trivially satisfied for any
  // device size; the second RG simply uses a multi-block bitmap when it is large.
  private const long Rg1Header = 17;
  private const long Rg1Data0 = 18;
  private const long Rg1Data = 2056;          // data blocks covered by RG1
  private const long Rg2Header = Rg1Data0 + Rg1Data; // 2074

  // Bytes of bitmap that fit in the rgrp header block and in each extra RB block.
  private const int RgrpBitmapBytes = BlockSize - 128;            // 3968
  private const int RbBitmapBytes = BlockSize - IndPointerBase;   // 4072

  // RG2 geometry, resolved in AssignLayout (depends on ri_length).
  private long _rg2Length;     // header block + RB blocks
  private long _rg2Data0;      // first data block of RG2
  private long _rg2Data;       // data blocks covered by RG2

  // Layout (assigned in Build).
  private long _sbBlock;       // 16
  private long _journalDinode;
  private long _journalData0;  // first journal data block
  private long _masterDinode;
  private long _jindexDinode;
  private long _perNodeDinode;
  private long _inumDinode;
  private long _statfsDinode;
  private long _rindexDinode;
  private long _quotaDinode;
  private long _inumRangeDinode;
  private long _statfsChangeDinode;
  private long _quotaChangeDinode;
  private long _quotaChangeData0;
  private long _rootDinode;

  // Formal inode numbers (no_formal_ino), assigned in dinode-creation order.
  private const ulong FiJournal = 1, FiMaster = 2, FiJindex = 3, FiPerNode = 4,
                      FiInumRange = 5, FiStatfsChange = 6, FiQuotaChange = 7,
                      FiInum = 8, FiStatfs = 9, FiRindex = 10, FiQuota = 11,
                      FiRoot = 12;
  private const ulong NextFreeFormalIno = 13;

  // Cluster lock-table name written to sb_locktable (read back as Gfs2Reader.LockTable).
  private readonly string _lockTable;

  // Set of blocks that are dinodes (bitmap state 3) vs plain used (state 1).
  private readonly SortedSet<long> _dinodeBlocks = [];
  private readonly SortedSet<long> _usedBlocks = [];

  // Caller files and the layout each one gets.
  private readonly List<(string Name, FilePayload Payload)> _files = [];
  private readonly List<FilePlan> _filePlans = [];
  private readonly DeferredPayloads _payloads = new();

  /// <summary>Data blocks one resource group covers. gfs2-utils keeps a group at or under 256 MB.</summary>
  private const long MaxRgData = 256L * 1024 * 1024 / BlockSize - 4;

  // Data resource groups past the fixed first one: (header, length, data0, data).
  private readonly List<(long Header, long Length, long Data0, long Data)> _dataRgs = [];

  /// <summary>Block pointers the dinode's own area holds, and one indirect block holds.</summary>
  private const int PointersPerDinode = (BlockSize - DinodeHeaderSize) / 8; // 483

  /// <summary>What one file gets: its dinode, the tree above its data, and its blocks.</summary>
  private sealed class FilePlan {
    public required string Name;
    public required FilePayload Payload;
    public required ulong FormalIno;
    public long Dinode;
    public ushort Height;
    public readonly List<long> DataBlocks = [];
    // Indirect blocks per level, innermost (closest to the data) first.
    public readonly List<List<long>> Levels = [];
  }

  /// <summary>
  /// Adds a regular file to the root directory. Bodies up to
  /// <c>BlockSize - 232</c> are stuffed in the dinode; longer ones get a
  /// metadata tree of indirect blocks.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, FilePayload.FromBytes(data)));
  }

  /// <summary>Adds a file whose bytes are pulled from <paramref name="openStream" /> as the volume is written.</summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(openStream);
    this._files.Add((name, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>
  /// Smallest volume that holds <paramref name="fileSizes" />: the fixed metadata
  /// layout, every file's dinode, its data blocks and the indirect blocks above
  /// them, plus room for the resource-group bitmaps. Rounded up to a megabyte.
  /// </summary>
  public static long EstimateSize(IEnumerable<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var blocks = (long)Rg2Header + QuotaChangeBlocks + 32; // fixed layout + system inodes
    foreach (var size in fileSizes) {
      ++blocks;                                            // the dinode
      if (size <= BlockSize - DinodeHeaderSize) continue;
      var data = (size + BlockSize - 1) / BlockSize;
      blocks += data;
      for (var count = data; count > PointersPerDinode;) {
        count = (count + PointersPerIndirect - 1) / PointersPerIndirect;
        blocks += count;
      }
    }
    // Bitmaps take a block per ~16 000 data blocks, and a twentieth again keeps
    // the last group from running right to its end.
    blocks += blocks / (RbBitmapBytes * 4) + 8;
    blocks += blocks / 20;
    var bytes = blocks * BlockSize;
    return Math.Max(32L * 1024 * 1024, (bytes + (1L << 20) - 1) & ~((1L << 20) - 1));
  }

  /// <summary>
  /// Creates a writer for an image of the given total size in bytes. The size is
  /// rounded down to a whole number of 4096-byte blocks; the minimum that yields
  /// a clean volume (journal + system inodes + slack) is 32&#160;MB.
  /// </summary>
  public Gfs2Writer(long sizeBytes = 32L * 1024 * 1024, byte[]? uuid = null,
                    DateTime? timestamp = null, string? lockTable = null) {
    this._lockTable = lockTable ?? "";
    this._totalBlocks = sizeBytes / BlockSize;
    if (this._totalBlocks < 4096)
      throw new ArgumentOutOfRangeException(nameof(sizeBytes),
        "GFS2 image must be at least 16 MB (4096 blocks) to hold the journal.");
    // gfs2-utils keeps each resource group at or below ~256 MB and splits a
    // larger device into several; the layout below does the same, so the volume
    // is bounded only by what the rindex can list.
    // The image is sparse: only blocks actually written are materialised, so a
    // multi-gigabyte volume costs its metadata rather than its full extent.
    this._img = new SparseBlockImage(BlockSize, this._totalBlocks * BlockSize);
    this._uuid = uuid ?? Guid.NewGuid().ToByteArray(bigEndian: true);
    if (this._uuid.Length != 16)
      throw new ArgumentException("UUID must be 16 bytes.", nameof(uuid));
    this._baseTime = (ulong)new DateTimeOffset(timestamp ?? DateTime.UtcNow)
      .ToUnixTimeSeconds();
  }

  /// <summary>Builds the image and returns the raw bytes.</summary>
  public byte[] Build() {
    var image = this.BuildCore();
    if (image.TotalBytes > Array.MaxLength)
      throw new IOException(
        $"GFS2: a {image.TotalBytes:N0}-byte volume exceeds the array limit; use Build(Stream).");
    var bytes = image.Materialise();
    using var buffer = new MemoryStream(bytes, writable: true);
    this._payloads.FlushTo(buffer);
    return bytes;
  }

  private SparseBlockImage BuildCore() {
    this.AssignLayout();
    this.WriteSuperblock();
    this.WriteJournal();
    this.WriteMasterTree();
    this.WriteRoot();
    // Block accounting (statfs + rgrp free/dinode counts) must reflect the
    // complete layout, so fill these once every dinode/used block is marked.
    this.WriteStatfsPayload();
    this.WriteResourceGroup();
    return this._img;
  }

  /// <summary>Builds the image and writes it to <paramref name="output"/>.</summary>
  public void Build(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (output.CanSeek) {
      var basePosition = output.Position;
      var image = this.BuildCore();
      image.WriteTo(output);
      this._payloads.FlushTo(output, basePosition);
      output.Position = basePosition + image.TotalBytes;
      output.Flush();
      return;
    }

    var bytes = this.Build();
    output.Write(bytes, 0, bytes.Length);
  }

  private void AssignLayout() {
    this._sbBlock = 16;

    // RG1 (blocks 18..2073): journal tree + master + jindex. This matches the
    // fixed first-resource-group shape gfs2-utils always emits.
    var b = Rg1Data0;
    this._journalDinode = b++;                 // 18
    var nInd = (JournalBlocks + PointersPerIndirect - 1) / PointersPerIndirect;
    b += nInd;                                 // 19..23 (indirect blocks)
    this._journalData0 = b;
    b += JournalBlocks;                        // 24..2071 (journal data)
    this._masterDinode = b++;                  // 2072
    this._jindexDinode = b++;                  // 2073

    if (b != Rg2Header)
      throw new InvalidOperationException(
        $"RG1 metadata does not fill the fixed first resource group (ended at {b}, expected {Rg2Header}).");

    // Resolve the data resource groups. Each one's 2-bits-per-block bitmap lives
    // in the rgrp header block plus as many RB blocks as needed
    // (ri_length = header + RBs), and covers at most MaxRgData data blocks —
    // gfs2-utils splits a larger device the same way. fsck.gfs2 requires ri_data
    // to be a multiple of GFS2_NBBY (4), so the bitmap occupies a whole number of
    // bytes with no partial trailing byte. ri_length depends on data and
    // vice-versa, so each group iterates to a fixed point.
    this._dataRgs.Clear();
    var header = Rg2Header;
    while (header < this._totalBlocks - 1) {
      var riLength = 1L;
      long data = 0;
      for (var iter = 0; iter < 8; iter++) {
        var data0 = header + riLength;
        var avail = (this._totalBlocks - 1) - data0; // last device block stays unused
        if (avail < 4) break;
        data = Math.Min(MaxRgData, avail);
        data -= data % 4;                            // round down to a multiple of 4
        var bitmapBytes = data / 4;
        var need = bitmapBytes <= RgrpBitmapBytes
          ? 1L
          : 1L + (bitmapBytes - RgrpBitmapBytes + RbBitmapBytes - 1) / RbBitmapBytes;
        if (need == riLength) break;
        riLength = need;
      }
      if (data < 4) break;
      this._dataRgs.Add((header, riLength, header + riLength, data));
      header = header + riLength + data;
    }

    if (this._dataRgs.Count == 0)
      throw new InvalidOperationException("Image too small for the GFS2 layout.");

    var first = this._dataRgs[0];
    this._rg2Length = first.Length;
    this._rg2Data0 = first.Data0;
    this._rg2Data = first.Data;

    // System inodes, the root, then the caller files — all from a cursor that
    // walks the data areas in order, stepping over each group's bitmap blocks.
    this._cursorRg = 0;
    this._cursorBlock = first.Data0;
    this._perNodeDinode = this.AllocBlock();
    this._inumRangeDinode = this.AllocBlock();
    this._statfsChangeDinode = this.AllocBlock();
    this._quotaChangeDinode = this.AllocBlock();
    this._quotaChangeData0 = this.AllocRun(QuotaChangeBlocks);
    this._inumDinode = this.AllocBlock();
    this._statfsDinode = this.AllocBlock();
    this._rindexDinode = this.AllocBlock();
    this._quotaDinode = this.AllocBlock();
    this._rootDinode = this.AllocBlock();

    this.PlanFiles();
  }

  // Allocation cursor over the data resource groups.
  private int _cursorRg;
  private long _cursorBlock;

  /// <summary>Hands out the next data block, moving to the next resource group when one fills.</summary>
  private long AllocBlock() {
    while (this._cursorRg < this._dataRgs.Count) {
      var rg = this._dataRgs[this._cursorRg];
      if (this._cursorBlock < rg.Data0 + rg.Data)
        return this._cursorBlock++;
      if (++this._cursorRg >= this._dataRgs.Count) break;
      this._cursorBlock = this._dataRgs[this._cursorRg].Data0;
    }
    throw new InvalidOperationException("GFS2: the volume has no free data blocks left.");
  }

  /// <summary>
  /// Hands out <paramref name="count" /> consecutive data blocks, all inside one
  /// resource group so callers that need a contiguous run get one.
  /// </summary>
  private long AllocRun(long count) {
    while (this._cursorRg < this._dataRgs.Count) {
      var rg = this._dataRgs[this._cursorRg];
      if (this._cursorBlock + count <= rg.Data0 + rg.Data) {
        var start = this._cursorBlock;
        this._cursorBlock += count;
        return start;
      }
      if (++this._cursorRg >= this._dataRgs.Count) break;
      this._cursorBlock = this._dataRgs[this._cursorRg].Data0;
    }
    throw new InvalidOperationException(
      $"GFS2: no resource group has {count:N0} consecutive free blocks.");
  }

  /// <summary>
  /// Gives every caller file a dinode and, when its body does not fit stuffed in
  /// that dinode, the data blocks plus the levels of indirect blocks the metadata
  /// tree needs. di_height counts those levels: at height 1 the dinode's own
  /// pointer area addresses the data blocks, and each level above multiplies the
  /// reach by the pointers one indirect block holds.
  /// </summary>
  private void PlanFiles() {
    var formalIno = NextFreeFormalIno;
    foreach (var (name, payload) in this._files) {
      var plan = new FilePlan { Name = name, Payload = payload, FormalIno = formalIno++ };
      plan.Dinode = this.AllocBlock();
      this._filePlans.Add(plan);

      if (payload.Size <= BlockSize - DinodeHeaderSize)
        continue;  // stuffed: height 0, body inline in the dinode

      var dataCount = (payload.Size + BlockSize - 1) / BlockSize;
      for (var i = 0L; i < dataCount; ++i)
        plan.DataBlocks.Add(this.AllocBlock());

      // Levels are built innermost-first: the level directly above the data, then
      // the level above that, until one level fits the dinode's own pointers.
      var below = dataCount;
      plan.Height = 1;
      while (below > PointersPerDinode) {
        var count = (below + PointersPerIndirect - 1) / PointersPerIndirect;
        var level = new List<long>((int)count);
        for (var i = 0L; i < count; ++i)
          level.Add(this.AllocBlock());
        plan.Levels.Add(level);
        below = count;
        ++plan.Height;
      }
    }
  }

  // ── Superblock ────────────────────────────────────────────────────────────

  private void WriteSuperblock() {
    var o = this._sbBlock * BlockSize;
    WriteMetaHeader(o, MtSb, FmtSb);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 24, 4), FormatFs);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 28, 4), FormatMulti);
    // __pad0 @32
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 36, 4), BlockSize);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 40, 4), BlockShift);
    // __pad1 @44
    // sb_master_dir @48
    WriteInum(o + 48, FiMaster, (ulong)this._masterDinode);
    // __pad2 @64 (zero)
    // sb_root_dir @80
    WriteInum(o + 80, FiRoot, (ulong)this._rootDinode);
    // sb_lockproto @96, sb_locktable @160
    WriteCString(o + 96, "lock_nolock", 64);
    WriteCString(o + 160, this._lockTable, 64);
    // __pad3 @224, __pad4 @240 (zero)
    // sb_uuid @256
    this._uuid.AsSpan().CopyTo(Span(o + 256, 16));
  }

  // ── Journal ─────────────────────────────────────────────────────────────--

  private void WriteJournal() {
    var nInd = (JournalBlocks + PointersPerIndirect - 1) / PointersPerIndirect;
    var indirect0 = this._journalDinode + 1;

    // Journal dinode: height 2 (dinode -> indirect -> data), GFS2_DIF_JDATA|SYSTEM.
    this.WriteDinode(
      block: this._journalDinode,
      formalIno: FiJournal,
      mode: SIfReg | 0x180, // 0600
      nlink: 1,
      size: (ulong)JournalBlocks * BlockSize,
      blocks: (ulong)(1 + nInd + JournalBlocks),
      flags: DifJData | DifSystem,
      payloadFormat: 0,
      height: 2,
      entries: 0,
      goalMeta: (ulong)(indirect0 + nInd - 1),
      goalData: (ulong)(this._journalData0 + JournalBlocks - 1));

    // Top-level dinode pointers reference the indirect blocks.
    var dinodeOff = this._journalDinode * BlockSize;
    for (var i = 0; i < nInd; i++)
      BinaryPrimitives.WriteUInt64BigEndian(
        Span(dinodeOff + DinodeHeaderSize + i * 8, 8), (ulong)(indirect0 + i));

    // Indirect blocks point at the journal data blocks.
    var dataBlk = this._journalData0;
    for (var i = 0; i < nInd; i++) {
      var ib = indirect0 + i;
      this.MarkUsed(ib);
      var io = ib * BlockSize;
      WriteMetaHeader(io, MtIndirect, FmtIndirect);
      var ptr = 0;
      while (ptr < PointersPerIndirect && dataBlk < this._journalData0 + JournalBlocks) {
        BinaryPrimitives.WriteUInt64BigEndian(
          Span(io + IndPointerBase + ptr * 8, 8), (ulong)dataBlk);
        dataBlk++;
        ptr++;
      }
    }

    // Journal data blocks: a clean unmount log header in every block.
    // The "head" is the highest sequence; fsck replays nothing because every
    // header is flagged UNMOUNT. Sequence numbers ascend then wrap so that the
    // last physical block holds seq just below the first — matching mkfs, but
    // any consistent ascending ring is accepted.
    ulong seq = 1;
    for (var i = 0; i < JournalBlocks; i++) {
      var blk = this._journalData0 + i;
      this.MarkUsed(blk);
      this.WriteLogHeader(blk, seq++, journalRelative: i, jinode: (ulong)this._journalDinode);
    }
  }

  private void WriteLogHeader(long block, ulong sequence, int journalRelative, ulong jinode) {
    var o = block * BlockSize;
    // Block is otherwise zero; write the log header.
    WriteMetaHeader(o, MtLogHeader, FmtLogHeader);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 24, 8), sequence);        // lh_sequence
    // lh_flags: USERSPACE | UNMOUNT
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 32, 4), 0x80000001u);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 36, 4), 0u);              // lh_tail
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 40, 4), (uint)journalRelative); // lh_blkno
    // lh_hash @44 computed below.
    // lh_crc @48 computed below.
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 52, 4), 0u);              // lh_nsec
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 56, 8), this._baseTime);  // lh_sec
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 64, 8), (ulong)block);    // lh_addr (absolute)
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 72, 8), jinode);          // lh_jinode
    // lh_statfs_addr/lh_quota_addr/local_* left zero (clean unmount).

    // lh_hash = CRC32 over bytes [0..48) with lh_hash zeroed.
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 44, 4), 0u);
    var hash = Crc32(this.Span(o, 48));
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 44, 4), hash);

    // lh_crc = CRC32C(init=~0, no final xor) over bytes [52..BlockSize).
    var crc = Crc32cNoFinal(this.Span(o + 52, BlockSize - 52));
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 48, 4), crc);
  }

  // ── Master directory + system inodes ────────────────────────────────────--

  private void WriteMasterTree() {
    // System dinode payloads (all stuffed, height 0).

    // inum: single u64 == next free formal inode number.
    this.WriteSystemFile(this._inumDinode, FiInum, size: 8, payloadFormat: 0,
      fill: span => BinaryPrimitives.WriteUInt64BigEndian(span, NextFreeFormalIno));

    // statfs: gfs2_statfs_change { sc_total, sc_free, sc_dinodes }. The dinode is
    // written here (to reserve/mark the block); its payload is filled later by
    // WriteStatfsPayload once all blocks are accounted for.
    this.WriteSystemFile(this._statfsDinode, FiStatfs, size: 24, payloadFormat: 0,
      fill: _ => { });

    // rindex: one gfs2_rindex (96 bytes) per resource group.
    this.WriteSystemFile(this._rindexDinode, FiRindex, size: 96 * this.ResourceGroups.Length,
      payloadFormat: PfRindex, fill: span => this.WriteRindexEntry(span));

    // quota: two gfs2_quota (root uid + root gid), 88 bytes each, value=1.
    this.WriteSystemFile(this._quotaDinode, FiQuota, size: 176, payloadFormat: PfQuota,
      fill: span => {
        for (var i = 0; i < 2; i++)
          BinaryPrimitives.WriteUInt64BigEndian(span.Slice(i * 88 + 16, 8), 1UL); // qu_value
      });

    // per_node files.
    this.WriteSystemFile(this._inumRangeDinode, FiInumRange, size: 16, payloadFormat: 0,
      fill: _ => { });
    this.WriteSystemFile(this._statfsChangeDinode, FiStatfsChange, size: 24, payloadFormat: 0,
      fill: _ => { });
    this.WriteQuotaChangeFile();

    // jindex directory: ".", "..", "journal0".
    this.WriteDirectory(this._jindexDinode, FiJindex, parentFormalIno: FiMaster,
      parentAddr: (ulong)this._masterDinode, system: true,
      children: [("journal0", FiJournal, (ulong)this._journalDinode, DtRegular)]);

    // per_node directory: ".", "..", inum_range0, statfs_change0, quota_change0.
    this.WriteDirectory(this._perNodeDinode, FiPerNode, parentFormalIno: FiMaster,
      parentAddr: (ulong)this._masterDinode, system: true,
      children: [
        ("inum_range0",    FiInumRange,    (ulong)this._inumRangeDinode,    DtRegular),
        ("statfs_change0", FiStatfsChange, (ulong)this._statfsChangeDinode, DtRegular),
        ("quota_change0",  FiQuotaChange,  (ulong)this._quotaChangeDinode,  DtRegular),
      ]);

    // master directory: ".", "..", jindex, per_node, inum, statfs, rindex, quota.
    // Its "." and ".." both point at itself (master is its own parent).
    this.WriteDirectory(this._masterDinode, FiMaster, parentFormalIno: FiMaster,
      parentAddr: (ulong)this._masterDinode, system: true,
      children: [
        ("jindex",   FiJindex,   (ulong)this._jindexDinode,   DtDir),
        ("per_node", FiPerNode,  (ulong)this._perNodeDinode,  DtDir),
        ("inum",     FiInum,     (ulong)this._inumDinode,     DtRegular),
        ("statfs",   FiStatfs,   (ulong)this._statfsDinode,   DtRegular),
        ("rindex",   FiRindex,   (ulong)this._rindexDinode,   DtRegular),
        ("quota",    FiQuota,    (ulong)this._quotaDinode,    DtRegular),
      ],
      // master nlink counts: self("."), parent ref, plus each subdirectory's
      // ".." (jindex + per_node) -> 4.
      nlinkOverride: 4);
  }

  private void WriteQuotaChangeFile() {
    // height-1 file: dinode -> QuotaChangeBlocks data blocks (each an empty QC block).
    this.WriteDinode(
      block: this._quotaChangeDinode,
      formalIno: FiQuotaChange,
      mode: SIfReg | 0x180,
      nlink: 1,
      size: (ulong)QuotaChangeBlocks * BlockSize,
      blocks: (ulong)(1 + QuotaChangeBlocks),
      flags: DifSystem,
      payloadFormat: 0,
      height: 1,
      entries: 0,
      goalMeta: (ulong)(this._quotaChangeData0 + QuotaChangeBlocks - 1),
      goalData: (ulong)this._quotaChangeDinode);

    // Top-level dinode pointers reference the data blocks directly (height 1).
    var dinodeOff = this._quotaChangeDinode * BlockSize;
    for (var i = 0; i < QuotaChangeBlocks; i++)
      BinaryPrimitives.WriteUInt64BigEndian(
        Span(dinodeOff + DinodeHeaderSize + i * 8, 8),
        (ulong)(this._quotaChangeData0 + i));

    for (var i = 0; i < QuotaChangeBlocks; i++) {
      var blk = this._quotaChangeData0 + i;
      this.MarkUsed(blk);
      WriteMetaHeader(blk * BlockSize, MtQuotaChange, FmtQuotaChange);
    }
  }

  // Resource-group geometry: header block, ri_length (header + RB blocks), first
  // data block, and data-block count. RG1 is a fixed single-block-bitmap group;
  // RG2 absorbs the remainder of the device (less the final unused block) and may
  // use a multi-block bitmap.
  private (long Header, long Length, long Data0, long Data)[] ResourceGroups =>
    [(Rg1Header, 1L, Rg1Data0, Rg1Data), .. this._dataRgs];

  private void WriteRindexEntry(Span<byte> span) {
    // One gfs2_rindex (96 bytes) per resource group.
    var rgs = this.ResourceGroups;
    for (var i = 0; i < rgs.Length; i++) {
      var (header, length, data0, data) = rgs[i];
      var s = span.Slice(i * 96, 96);
      var bitBytes = (uint)((data + 3) / 4);
      BinaryPrimitives.WriteUInt64BigEndian(s[..8], (ulong)header);      // ri_addr
      BinaryPrimitives.WriteUInt32BigEndian(s.Slice(8, 4), (uint)length); // ri_length
      // __pad @12
      BinaryPrimitives.WriteUInt64BigEndian(s.Slice(16, 8), (ulong)data0); // ri_data0
      BinaryPrimitives.WriteUInt32BigEndian(s.Slice(24, 4), (uint)data); // ri_data
      BinaryPrimitives.WriteUInt32BigEndian(s.Slice(28, 4), bitBytes);   // ri_bitbytes
    }
  }



  private (long Total, long Free, long Dinodes) ComputeStatfs() {
    // Statfs counts span all resource groups (sum of every RG's data blocks).
    var total = 0L;
    foreach (var rg in this.ResourceGroups)
      total += rg.Data;
    var dinodes = (long)this._dinodeBlocks.Count;
    var used = this._dinodeBlocks.Count + this._usedBlocks.Count;
    var free = total - used;
    return (total, free, dinodes);
  }

  private void WriteStatfsPayload() {
    var (total, free, dinodes) = this.ComputeStatfs();
    var o = this._statfsDinode * BlockSize + DinodeHeaderSize;
    BinaryPrimitives.WriteUInt64BigEndian(Span(o, 8), (ulong)total);       // sc_total
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 8, 8), (ulong)free);    // sc_free
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 16, 8), (ulong)dinodes); // sc_dinodes
  }

  // ── Root directory ────────────────────────────────────────────────────────

  private void WriteRoot() {
    // The root names every caller file; "." and ".." point at root itself.
    var children = new (string Name, ulong Fi, ulong Addr, ushort Type)[this._filePlans.Count];
    for (var i = 0; i < this._filePlans.Count; ++i) {
      var plan = this._filePlans[i];
      children[i] = (LeafName(plan.Name), plan.FormalIno, (ulong)plan.Dinode, DtRegular);
    }

    this.WriteDirectory(this._rootDinode, FiRoot, parentFormalIno: FiRoot,
      parentAddr: (ulong)this._rootDinode, system: false, children: children,
      nlinkOverride: 2, mode: SIfDir | 0x1ED /* 0755 */);

    foreach (var plan in this._filePlans)
      this.WriteFile(plan);
  }

  /// <summary>
  /// A GFS2 directory here is a single stuffed block, so a nested path is stored
  /// under its leaf name rather than fabricating a subdirectory tree.
  /// </summary>
  private static string LeafName(string name) {
    var slash = name.LastIndexOfAny(['/', '\\']);
    return slash < 0 ? name : name[(slash + 1)..];
  }

  /// <summary>
  /// Writes one caller file: a dinode, and either its body stuffed inline or the
  /// metadata tree pointing at its data blocks. Pointers at every level sit
  /// immediately after that block's header — offset 232 in a dinode, 24 in an
  /// indirect block.
  /// </summary>
  private void WriteFile(FilePlan plan) {
    var size = plan.Payload.Size;
    var blocks = 1L + plan.DataBlocks.Count;
    foreach (var level in plan.Levels) blocks += level.Count;

    this.WriteDinode(
      block: plan.Dinode, formalIno: plan.FormalIno, mode: SIfReg | 0x1A4 /* 0644 */,
      nlink: 1, size: (ulong)size, blocks: (ulong)blocks, flags: 0,
      payloadFormat: 0, height: plan.Height, entries: 0,
      goalMeta: (ulong)plan.Dinode, goalData: (ulong)plan.Dinode);

    if (plan.Height == 0) {
      // Stuffed: the body lives in the dinode past its 232-byte header.
      if (size > 0)
        this._img.Write((long)plan.Dinode * BlockSize + DinodeHeaderSize, plan.Payload.ToArray());
      return;
    }

    // The level immediately below the dinode is the outermost indirect level, or
    // the data blocks themselves when the tree is only one level deep.
    var top = plan.Levels.Count > 0 ? plan.Levels[^1] : plan.DataBlocks;
    WritePointers((long)plan.Dinode * BlockSize + DinodeHeaderSize,
      PointersPerDinode, top, 0);

    // Each indirect level points at the level below it: the last planned level is
    // the outermost, so walk them back down toward the data.
    for (var i = plan.Levels.Count - 1; i >= 0; --i) {
      var level = plan.Levels[i];
      var below = i > 0 ? plan.Levels[i - 1] : plan.DataBlocks;
      for (var j = 0; j < level.Count; ++j) {
        var block = level[j];
        this.WriteMetaHeader((long)block * BlockSize, MtIndirect, FmtIndirect);
        this.MarkUsed(block);
        WritePointers((long)block * BlockSize + IndPointerBase,
          PointersPerIndirect, below, j * PointersPerIndirect);
      }
    }

    // The data blocks are contiguous within a resource group, so the body is one
    // forward copy per run rather than a write per block.
    var offset = 0L;
    var runStart = 0;
    while (runStart < plan.DataBlocks.Count) {
      var runEnd = runStart + 1;
      while (runEnd < plan.DataBlocks.Count
          && plan.DataBlocks[runEnd] == plan.DataBlocks[runEnd - 1] + 1)
        ++runEnd;

      var runBytes = Math.Min((long)(runEnd - runStart) * BlockSize, size - offset);
      if (runBytes > 0) {
        var skip = offset;
        var payload = plan.Payload;
        this._payloads.Add((long)plan.DataBlocks[runStart] * BlockSize,
          FilePayload.FromStream(runBytes, () => SkipTo(payload.Open(), skip)));
      }
      offset += (long)(runEnd - runStart) * BlockSize;
      runStart = runEnd;
    }

    foreach (var block in plan.DataBlocks)
      this.MarkUsed(block);
  }

  /// <summary>Fills a pointer area with a slice of the level below it.</summary>
  private void WritePointers(long areaOffset, int capacity, List<long> below, int firstIndex) {
    var count = Math.Min(capacity, below.Count - firstIndex);
    for (var i = 0; i < count; ++i)
      BinaryPrimitives.WriteUInt64BigEndian(
        this.Span(areaOffset + i * 8, 8), (ulong)below[firstIndex + i]);
  }

  /// <summary>Advances <paramref name="source" /> to <paramref name="offset" />, reading through when it cannot seek.</summary>
  private static Stream SkipTo(Stream source, long offset) {
    if (offset <= 0) return source;
    if (source.CanSeek) {
      source.Position = offset;
      return source;
    }
    var buffer = new byte[64 * 1024];
    var remaining = offset;
    while (remaining > 0) {
      var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (n <= 0) break;
      remaining -= n;
    }
    return source;
  }

  // ── Resource group + bitmap ─────────────────────────────────────────────--

  private void WriteResourceGroup() {
    var rgs = this.ResourceGroups;
    for (var i = 0; i < rgs.Length; i++) {
      var (header, length, data0, data) = rgs[i];
      var dataEnd = data0 + data; // exclusive
      var bitBytes = (int)((data + 3) / 4);

      // Per-RG accounting: count dinodes and used blocks that fall in [data0,dataEnd).
      var dinodeCount = 0;
      var usedCount = 0;
      foreach (var blk in this._dinodeBlocks)
        if (blk >= data0 && blk < dataEnd) dinodeCount++;
      foreach (var blk in this._usedBlocks)
        if (blk >= data0 && blk < dataEnd) usedCount++;
      var free = data - dinodeCount - usedCount;
      // rg_skip is the distance to the next rgrp header; 0 for the last RG.
      var skip = i + 1 < rgs.Length ? rgs[i + 1].Header - header : 0;

      var o = header * BlockSize;
      WriteMetaHeader(o, MtRg, FmtRg);
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 24, 4), 0u);              // rg_flags
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 28, 4), (uint)free);      // rg_free
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 32, 4), (uint)dinodeCount); // rg_dinodes
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 36, 4), (uint)skip);      // rg_skip
      BinaryPrimitives.WriteUInt64BigEndian(Span(o + 40, 8), 0UL);             // rg_igeneration
      BinaryPrimitives.WriteUInt64BigEndian(Span(o + 48, 8), (ulong)data0);    // rg_data0
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 56, 4), (uint)data);      // rg_data
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 60, 4), (uint)bitBytes);  // rg_bitbytes
      // rg_crc @64 computed below; rg_reserved[60] @68..128 zero.

      // Bitmap headers for the extra RB blocks (the rgrp block carries the first
      // chunk inline; each subsequent bitmap block is a gfs2_meta_header RB).
      for (var rb = 1; rb < length; rb++)
        this.WriteMetaHeader((header + rb) * BlockSize, MtRb, FmtRb);

      // Paint the logically-contiguous bitmap, which is physically split across
      // the rgrp block (offset 128) and the RB blocks (offset 24).
      foreach (var blk in this._dinodeBlocks)
        if (blk >= data0 && blk < dataEnd)
          this.SetRgBitmap(header, blk - data0, BlkstDinode);
      foreach (var blk in this._usedBlocks)
        if (blk >= data0 && blk < dataEnd)
          this.SetRgBitmap(header, blk - data0, BlkstUsed);

      // rg_crc = CRC32 over the 128-byte gfs2_rgrp header with rg_crc zeroed.
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 64, 4), 0u);
      var crc = Crc32(this.Span(o, 128));
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 64, 4), crc);
    }
  }

  /// <summary>
  /// Sets the 2-bit block state for logical data-block <paramref name="dataIndex"/>
  /// (0-based within the resource group) in the resource group whose header is at
  /// <paramref name="rgHeader"/>. The bitmap is logically contiguous but split:
  /// the first <see cref="RgrpBitmapBytes"/> live in the rgrp block at offset 128,
  /// then each RB block contributes <see cref="RbBitmapBytes"/> at offset 24.
  /// </summary>
  private void SetRgBitmap(long rgHeader, long dataIndex, int state) {
    var byteOffset = dataIndex / 4;
    var shift = (int)(dataIndex % 4) * 2;
    long absByte;
    if (byteOffset < RgrpBitmapBytes) {
      absByte = rgHeader * BlockSize + 128 + byteOffset;
    } else {
      var rest = byteOffset - RgrpBitmapBytes;
      var rbIndex = rest / RbBitmapBytes;       // which RB block (0-based)
      var inRb = rest % RbBitmapBytes;
      absByte = (rgHeader + 1 + rbIndex) * BlockSize + IndPointerBase + inRb;
    }
    this._img[absByte] = (byte)((this._img[absByte] & ~(0x3 << shift)) | ((state & 0x3) << shift));
  }

  // ── Dinode / directory helpers ──────────────────────────────────────────--

  private void WriteSystemFile(long block, ulong formalIno, int size,
                               uint payloadFormat, Action<Span<byte>> fill) {
    this.WriteDinode(
      block: block, formalIno: formalIno, mode: SIfReg | 0x180, nlink: 1,
      size: (ulong)size, blocks: 1, flags: DifSystem | DifJData,
      payloadFormat: payloadFormat, height: 0, entries: 0,
      goalMeta: (ulong)block, goalData: (ulong)block);
    var o = block * BlockSize + DinodeHeaderSize;
    fill(Span(o, size));
  }

  private void WriteDirectory(long block, ulong formalIno, ulong parentFormalIno,
                              ulong parentAddr, bool system,
                              (string Name, ulong Fi, ulong Addr, ushort Type)[] children,
                              int? nlinkOverride = null, uint? mode = null) {
    var entryCount = (uint)(2 + children.Length); // "." + ".." + children
    var nlink = nlinkOverride ?? 2;
    // Directory di_flags as mkfs marks them: system dirs use JDATA|SYSTEM (0x201),
    // the root uses JDATA (0x1).
    var flags = DifJData | (system ? DifSystem : 0u);

    this.WriteDinode(
      block: block, formalIno: formalIno,
      mode: mode ?? (SIfDir | (system ? 0x1C0u : 0x1EDu)), // system 0700, root 0755
      nlink: (uint)nlink,
      size: (ulong)(BlockSize - DinodeHeaderSize), // stuffed dir size mkfs uses
      blocks: 1, flags: flags, payloadFormat: PfDirent, height: 0,
      entries: entryCount,
      goalMeta: (ulong)block, goalData: (ulong)block);

    // Write dirents inline starting at offset 232. Each record is sized to fit
    // its name; the final record's rec_len extends to end-of-block.
    var areaStart = block * BlockSize + DinodeHeaderSize;
    var areaLen = BlockSize - DinodeHeaderSize;
    var pos = 0;

    var all = new List<(string Name, ulong Fi, ulong Addr, ushort Type)> {
      (".", formalIno, (ulong)block, DtDir),
      ("..", parentFormalIno, parentAddr, DtDir),
    };
    all.AddRange(children);

    for (var i = 0; i < all.Count; i++) {
      var (name, fi, addr, type) = all[i];
      var nameLen = Encoding.UTF8.GetByteCount(name);
      var minRec = (DirentSize + nameLen + 7) & ~7;
      var recLen = i == all.Count - 1 ? areaLen - pos : minRec;
      this.WriteDirent(areaStart + pos, fi, addr, name, nameLen, (ushort)recLen, type);
      pos += recLen;
    }
  }

  private void WriteDirent(long off, ulong fi, ulong addr, string name, int nameLen,
                           ushort recLen, ushort type) {
    WriteInum(off, fi, addr);                                                // de_inum
    BinaryPrimitives.WriteUInt32BigEndian(Span(off + 16, 4), Crc32(Encoding.UTF8.GetBytes(name))); // de_hash
    BinaryPrimitives.WriteUInt16BigEndian(Span(off + 20, 2), recLen);        // de_rec_len
    BinaryPrimitives.WriteUInt16BigEndian(Span(off + 22, 2), (ushort)nameLen); // de_name_len
    BinaryPrimitives.WriteUInt16BigEndian(Span(off + 24, 2), type);          // de_type
    // de_rahead @26 + reserved/cookie [12] @28 left zero.
    var nameBytes = Encoding.UTF8.GetBytes(name);
    nameBytes.AsSpan().CopyTo(Span(off + DirentSize, nameLen));
  }

  private void WriteDinode(long block, ulong formalIno, uint mode, uint nlink,
                           ulong size, ulong blocks, uint flags, uint payloadFormat,
                           ushort height, uint entries,
                           ulong goalMeta, ulong goalData) {
    var o = block * BlockSize;
    WriteMetaHeader(o, MtDinode, FmtDinode);
    WriteInum(o + 24, formalIno, (ulong)block);                             // di_num
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 40, 4), mode);           // di_mode
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 44, 4), 0u);             // di_uid (root)
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 48, 4), 0u);             // di_gid (root)
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 52, 4), nlink);          // di_nlink
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 56, 8), size);           // di_size
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 64, 8), blocks);         // di_blocks
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 72, 8), this._baseTime); // di_atime
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 80, 8), this._baseTime); // di_mtime
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 88, 8), this._baseTime); // di_ctime
    // di_major/minor @96/100 zero.
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 104, 8), goalMeta);      // di_goal_meta
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 112, 8), goalData);      // di_goal_data
    // di_generation @120 zero.
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 128, 4), flags);         // di_flags
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 132, 4), payloadFormat); // di_payload_format
    // __pad1 @136
    BinaryPrimitives.WriteUInt16BigEndian(Span(o + 138, 2), height);        // di_height
    // __pad2 @140, __pad3 @144
    BinaryPrimitives.WriteUInt16BigEndian(Span(o + 146, 2), 0);             // di_depth
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 148, 4), entries);       // di_entries
    // __pad4 @152 (gfs2_inum, zero); di_eattr and the *_nsec/reserved tail stay
    // zero — there are no extended attributes and timestamps carry no nanoseconds.

    this.MarkDinode(block);
  }

  // ── Bookkeeping ─────────────────────────────────────────────────────────--

  private void MarkDinode(long block) {
    this._usedBlocks.Remove(block);
    this._dinodeBlocks.Add(block);
  }

  private void MarkUsed(long block) {
    if (!this._dinodeBlocks.Contains(block))
      this._usedBlocks.Add(block);
  }

  // ── Primitives ──────────────────────────────────────────────────────────--

  private Span<byte> Span(long offset, int length) => this._img.At(offset, length);

  /// <summary>Writes the 24-byte gfs2_meta_header at the given absolute offset.</summary>
  private void WriteMetaHeader(long absOffset, uint type, uint format) {
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(absOffset, 4), MetaMagic); // mh_magic
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(absOffset + 4, 4), type);  // mh_type
    // __pad0 (8) @8 stays zero.
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(absOffset + 16, 4), format); // mh_format
    // mh_jid @20 stays zero.
  }

  private void WriteInum(long absOffset, ulong formalIno, ulong addr) {
    BinaryPrimitives.WriteUInt64BigEndian(this.Span(absOffset, 8), formalIno);
    BinaryPrimitives.WriteUInt64BigEndian(this.Span(absOffset + 8, 8), addr);
  }

  private void WriteCString(long absOffset, string value, int fieldLen) {
    var bytes = Encoding.ASCII.GetBytes(value);
    var n = Math.Min(bytes.Length, fieldLen - 1);
    bytes.AsSpan(0, n).CopyTo(this.Span(absOffset, n));
    // remainder already zero
  }

  // Standard CRC-32 (reflected, poly 0xEDB88320), as used for lh_hash, rg_crc,
  // and the directory entry hash.
  private static readonly uint[] _crc32Table = BuildCrc32Table(0xEDB88320u);
  // CRC-32C (Castagnoli, poly 0x82F63B78), used for lh_crc (no final inversion).
  private static readonly uint[] _crc32cTable = BuildCrc32Table(0x82F63B78u);

  private static uint[] BuildCrc32Table(uint poly) {
    var t = new uint[256];
    for (var i = 0u; i < 256; i++) {
      var c = i;
      for (var k = 0; k < 8; k++)
        c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
      t[i] = c;
    }
    return t;
  }

  private static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = _crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }

  private static uint Crc32cNoFinal(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = _crc32cTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc; // GFS2 lh_crc applies no final XOR
  }
}
