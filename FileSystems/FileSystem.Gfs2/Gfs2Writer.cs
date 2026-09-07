#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Gfs2;

/// <summary>
/// Clean-room GFS2 (Global File System 2) image writer producing a minimal,
/// standalone (<c>lock_nolock</c>, single-journal) volume that real
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
///   <item><description>Resource groups whose bitmaps cover every data block and
///   correctly mark each used metadata block.</description></item>
///   <item><description>Master directory dinode and the system inodes hung off it:
///   <c>jindex</c>, <c>per_node</c>, <c>inum</c>, <c>statfs</c>, <c>rindex</c>,
///   <c>quota</c>.</description></item>
///   <item><description>A formatted 8&#160;MB journal (<c>journal0</c>) whose 2048
///   blocks each carry a clean unmount log header (correct <c>lh_hash</c> CRC32
///   and <c>lh_crc</c> CRC32C).</description></item>
///   <item><description><c>per_node</c> system inodes <c>inum_range0</c>,
///   <c>statfs_change0</c>, <c>quota_change0</c> (the latter a 1&#160;MB file of
///   empty quota-change blocks).</description></item>
///   <item><description>A root directory plus nested stuffed directories inferred
///   from caller paths; regular files may be stuffed or use indirect trees.</description></item>
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
  // holding the journal + master + jindex, and one or more data RGs.
  private const long Rg1Header = 17;
  private const long Rg1Data0 = 18;
  private const long Rg1Data = 2056;
  private const long Rg2Header = Rg1Data0 + Rg1Data;

  private const int RgrpBitmapBytes = BlockSize - 128;
  private const int RbBitmapBytes = BlockSize - IndPointerBase;

  private long _rg2Length;
  private long _rg2Data0;
  private long _rg2Data;

  private long _sbBlock;
  private long _journalDinode;
  private long _journalData0;
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

  private const ulong FiJournal = 1, FiMaster = 2, FiJindex = 3, FiPerNode = 4,
                      FiInumRange = 5, FiStatfsChange = 6, FiQuotaChange = 7,
                      FiInum = 8, FiStatfs = 9, FiRindex = 10, FiQuota = 11,
                      FiRoot = 12;
  private const ulong FirstUserFormalIno = 13;

  private readonly string _lockTable;

  private readonly SortedSet<long> _dinodeBlocks = [];
  private readonly SortedSet<long> _usedBlocks = [];

  private readonly List<(string Name, FilePayload Payload)> _files = [];
  private readonly List<FilePlan> _filePlans = [];
  private readonly List<DirectoryPlan> _directoryPlans = [];
  private readonly DeferredPayloads _payloads = new();
  private DirectoryPlan? _rootPlan;
  private ulong _nextFreeFormalIno = FirstUserFormalIno;

  private const long MaxRgData = 256L * 1024 * 1024 / BlockSize - 4;
  private readonly List<(long Header, long Length, long Data0, long Data)> _dataRgs = [];
  private const int PointersPerDinode = (BlockSize - DinodeHeaderSize) / 8;

  private sealed class DirectoryPlan {
    public required string Name;
    public required string Path;
    public DirectoryPlan? Parent;
    public required ulong FormalIno;
    public long Dinode;
    public readonly List<DirectoryPlan> Directories = [];
    public readonly List<FilePlan> Files = [];
  }

  private sealed class FilePlan {
    public required string Name;
    public required string Path;
    public required DirectoryPlan Parent;
    public required FilePayload Payload;
    public required ulong FormalIno;
    public long Dinode;
    public ushort Height;
    public readonly List<long> DataBlocks = [];
    public readonly List<List<long>> Levels = [];
  }

  /// <summary>
  /// Adds a regular file. Forward slashes and backslashes delimit nested
  /// directories; each directory is emitted as a native stuffed GFS2 dinode.
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
  /// Directory paths need additional dinode blocks and therefore consume some of
  /// the sizing slack this estimate deliberately keeps.
  /// </summary>
  public static long EstimateSize(IEnumerable<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var blocks = (long)Rg2Header + QuotaChangeBlocks + 32;
    foreach (var size in fileSizes) {
      ++blocks;
      if (size <= BlockSize - DinodeHeaderSize) continue;
      var data = (size + BlockSize - 1) / BlockSize;
      blocks += data;
      for (var count = data; count > PointersPerDinode;) {
        count = (count + PointersPerIndirect - 1) / PointersPerIndirect;
        blocks += count;
      }
    }
    blocks += blocks / (RbBitmapBytes * 4) + 8;
    blocks += blocks / 20;
    var bytes = blocks * BlockSize;
    return Math.Max(32L * 1024 * 1024, (bytes + (1L << 20) - 1) & ~((1L << 20) - 1));
  }

  public Gfs2Writer(long sizeBytes = 32L * 1024 * 1024, byte[]? uuid = null,
                    DateTime? timestamp = null, string? lockTable = null) {
    this._lockTable = lockTable ?? "";
    this._totalBlocks = sizeBytes / BlockSize;
    if (this._totalBlocks < 4096)
      throw new ArgumentOutOfRangeException(nameof(sizeBytes),
        "GFS2 image must be at least 16 MB (4096 blocks) to hold the journal.");
    this._img = new SparseBlockImage(BlockSize, this._totalBlocks * BlockSize);
    this._uuid = uuid ?? Guid.NewGuid().ToByteArray(bigEndian: true);
    if (this._uuid.Length != 16)
      throw new ArgumentException("UUID must be 16 bytes.", nameof(uuid));
    this._baseTime = (ulong)new DateTimeOffset(timestamp ?? DateTime.UtcNow).ToUnixTimeSeconds();
  }

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
    this.WriteUserTree();
    this.WriteStatfsPayload();
    this.WriteResourceGroup();
    return this._img;
  }

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

    var b = Rg1Data0;
    this._journalDinode = b++;
    var nInd = (JournalBlocks + PointersPerIndirect - 1) / PointersPerIndirect;
    b += nInd;
    this._journalData0 = b;
    b += JournalBlocks;
    this._masterDinode = b++;
    this._jindexDinode = b++;

    if (b != Rg2Header)
      throw new InvalidOperationException(
        $"RG1 metadata does not fill the fixed first resource group (ended at {b}, expected {Rg2Header}).");

    this._dataRgs.Clear();
    var header = Rg2Header;
    while (header < this._totalBlocks - 1) {
      var riLength = 1L;
      long data = 0;
      for (var iter = 0; iter < 8; iter++) {
        var data0 = header + riLength;
        var avail = (this._totalBlocks - 1) - data0;
        if (avail < 4) break;
        data = Math.Min(MaxRgData, avail);
        data -= data % 4;
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

    this.PlanTree();
  }

  private int _cursorRg;
  private long _cursorBlock;

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

  private void PlanTree() {
    this._filePlans.Clear();
    this._directoryPlans.Clear();
    this._nextFreeFormalIno = FirstUserFormalIno;

    var root = new DirectoryPlan {
      Name = "",
      Path = "",
      Parent = null,
      FormalIno = FiRoot,
      Dinode = this._rootDinode,
    };
    this._rootPlan = root;
    var directories = new Dictionary<string, DirectoryPlan>(StringComparer.Ordinal) { [""] = root };

    foreach (var (rawPath, payload) in this._files) {
      var parts = SplitPath(rawPath);
      var parent = root;
      var directoryPath = "";

      for (var i = 0; i < parts.Length - 1; ++i) {
        var segment = parts[i];
        directoryPath = directoryPath.Length == 0 ? segment : $"{directoryPath}/{segment}";
        if (directories.TryGetValue(directoryPath, out var existing)) {
          parent = existing;
          continue;
        }
        if (parent.Files.Any(file => file.Name == segment))
          throw new InvalidDataException($"GFS2 path '{rawPath}' uses file '{segment}' as a directory.");

        var directory = new DirectoryPlan {
          Name = segment,
          Path = directoryPath,
          Parent = parent,
          FormalIno = this._nextFreeFormalIno++,
          Dinode = this.AllocBlock(),
        };
        directories.Add(directoryPath, directory);
        parent.Directories.Add(directory);
        this._directoryPlans.Add(directory);
        parent = directory;
      }

      var leaf = parts[^1];
      if (parent.Files.Any(file => file.Name == leaf) || parent.Directories.Any(dir => dir.Name == leaf))
        throw new InvalidDataException($"GFS2 contains more than one entry at '{rawPath}'.");

      var path = string.Join('/', parts);
      var plan = new FilePlan {
        Name = leaf,
        Path = path,
        Parent = parent,
        Payload = payload,
        FormalIno = this._nextFreeFormalIno++,
        Dinode = this.AllocBlock(),
      };
      parent.Files.Add(plan);
      this._filePlans.Add(plan);
      this.PlanFileBlocks(plan);
    }
  }

  private static string[] SplitPath(string name) {
    if (name.IndexOf('\0') >= 0)
      throw new InvalidDataException("GFS2 entry names cannot contain NUL.");
    var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.None);
    if (parts.Length == 0 || parts.Any(static part => part.Length == 0 || part is "." or ".."))
      throw new InvalidDataException($"GFS2 path '{name}' is not a relative canonical path.");
    foreach (var part in parts)
      if (Encoding.UTF8.GetByteCount(part) > 255)
        throw new InvalidDataException($"GFS2 name '{part}' exceeds the 255-byte directory-entry limit.");
    return parts;
  }

  private void PlanFileBlocks(FilePlan plan) {
    if (plan.Payload.Size <= BlockSize - DinodeHeaderSize)
      return;

    var dataCount = (plan.Payload.Size + BlockSize - 1) / BlockSize;
    for (var i = 0L; i < dataCount; ++i)
      plan.DataBlocks.Add(this.AllocBlock());

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

  private void WriteSuperblock() {
    var o = this._sbBlock * BlockSize;
    WriteMetaHeader(o, MtSb, FmtSb);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 24, 4), FormatFs);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 28, 4), FormatMulti);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 36, 4), BlockSize);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 40, 4), BlockShift);
    WriteInum(o + 48, FiMaster, (ulong)this._masterDinode);
    WriteInum(o + 80, FiRoot, (ulong)this._rootDinode);
    WriteCString(o + 96, "lock_nolock", 64);
    WriteCString(o + 160, this._lockTable, 64);
    this._uuid.AsSpan().CopyTo(Span(o + 256, 16));
  }

  private void WriteJournal() {
    var nInd = (JournalBlocks + PointersPerIndirect - 1) / PointersPerIndirect;
    var indirect0 = this._journalDinode + 1;

    this.WriteDinode(
      block: this._journalDinode,
      formalIno: FiJournal,
      mode: SIfReg | 0x180,
      nlink: 1,
      size: (ulong)JournalBlocks * BlockSize,
      blocks: (ulong)(1 + nInd + JournalBlocks),
      flags: DifJData | DifSystem,
      payloadFormat: 0,
      height: 2,
      entries: 0,
      goalMeta: (ulong)(indirect0 + nInd - 1),
      goalData: (ulong)(this._journalData0 + JournalBlocks - 1));

    var dinodeOff = this._journalDinode * BlockSize;
    for (var i = 0; i < nInd; i++)
      BinaryPrimitives.WriteUInt64BigEndian(
        Span(dinodeOff + DinodeHeaderSize + i * 8, 8), (ulong)(indirect0 + i));

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

    ulong seq = 1;
    for (var i = 0; i < JournalBlocks; i++) {
      var blk = this._journalData0 + i;
      this.MarkUsed(blk);
      this.WriteLogHeader(blk, seq++, journalRelative: i, jinode: (ulong)this._journalDinode);
    }
  }

  private void WriteLogHeader(long block, ulong sequence, int journalRelative, ulong jinode) {
    var o = block * BlockSize;
    WriteMetaHeader(o, MtLogHeader, FmtLogHeader);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 24, 8), sequence);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 32, 4), 0x80000001u);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 36, 4), 0u);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 40, 4), (uint)journalRelative);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 52, 4), 0u);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 56, 8), this._baseTime);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 64, 8), (ulong)block);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 72, 8), jinode);

    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 44, 4), 0u);
    var hash = Crc32(this.Span(o, 48));
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 44, 4), hash);

    var crc = Crc32cNoFinal(this.Span(o + 52, BlockSize - 52));
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 48, 4), crc);
  }

  private void WriteMasterTree() {
    this.WriteSystemFile(this._inumDinode, FiInum, size: 8, payloadFormat: 0,
      fill: span => BinaryPrimitives.WriteUInt64BigEndian(span, this._nextFreeFormalIno));

    this.WriteSystemFile(this._statfsDinode, FiStatfs, size: 24, payloadFormat: 0,
      fill: _ => { });

    this.WriteSystemFile(this._rindexDinode, FiRindex, size: 96 * this.ResourceGroups.Length,
      payloadFormat: PfRindex, fill: span => this.WriteRindexEntry(span));

    this.WriteSystemFile(this._quotaDinode, FiQuota, size: 176, payloadFormat: PfQuota,
      fill: span => {
        for (var i = 0; i < 2; i++)
          BinaryPrimitives.WriteUInt64BigEndian(span.Slice(i * 88 + 16, 8), 1UL);
      });

    this.WriteSystemFile(this._inumRangeDinode, FiInumRange, size: 16, payloadFormat: 0,
      fill: _ => { });
    this.WriteSystemFile(this._statfsChangeDinode, FiStatfsChange, size: 24, payloadFormat: 0,
      fill: _ => { });
    this.WriteQuotaChangeFile();

    this.WriteDirectory(this._jindexDinode, FiJindex, parentFormalIno: FiMaster,
      parentAddr: (ulong)this._masterDinode, system: true,
      children: [("journal0", FiJournal, (ulong)this._journalDinode, DtRegular)]);

    this.WriteDirectory(this._perNodeDinode, FiPerNode, parentFormalIno: FiMaster,
      parentAddr: (ulong)this._masterDinode, system: true,
      children: [
        ("inum_range0",    FiInumRange,    (ulong)this._inumRangeDinode,    DtRegular),
        ("statfs_change0", FiStatfsChange, (ulong)this._statfsChangeDinode, DtRegular),
        ("quota_change0",  FiQuotaChange,  (ulong)this._quotaChangeDinode,  DtRegular),
      ]);

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
      nlinkOverride: 4);
  }

  private void WriteQuotaChangeFile() {
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

  private (long Header, long Length, long Data0, long Data)[] ResourceGroups =>
    [(Rg1Header, 1L, Rg1Data0, Rg1Data), .. this._dataRgs];

  private void WriteRindexEntry(Span<byte> span) {
    var rgs = this.ResourceGroups;
    for (var i = 0; i < rgs.Length; i++) {
      var (header, length, data0, data) = rgs[i];
      var s = span.Slice(i * 96, 96);
      var bitBytes = (uint)((data + 3) / 4);
      BinaryPrimitives.WriteUInt64BigEndian(s[..8], (ulong)header);
      BinaryPrimitives.WriteUInt32BigEndian(s.Slice(8, 4), (uint)length);
      BinaryPrimitives.WriteUInt64BigEndian(s.Slice(16, 8), (ulong)data0);
      BinaryPrimitives.WriteUInt32BigEndian(s.Slice(24, 4), (uint)data);
      BinaryPrimitives.WriteUInt32BigEndian(s.Slice(28, 4), bitBytes);
    }
  }

  private (long Total, long Free, long Dinodes) ComputeStatfs() {
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
    BinaryPrimitives.WriteUInt64BigEndian(Span(o, 8), (ulong)total);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 8, 8), (ulong)free);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 16, 8), (ulong)dinodes);
  }

  private void WriteUserTree() {
    var root = this._rootPlan ?? throw new InvalidOperationException("GFS2 user tree was not planned.");
    this.WriteDirectoryPlan(root);
    foreach (var plan in this._filePlans)
      this.WriteFile(plan);
  }

  private void WriteDirectoryPlan(DirectoryPlan plan) {
    var children = new (string Name, ulong Fi, ulong Addr, ushort Type)[plan.Directories.Count + plan.Files.Count];
    var index = 0;
    foreach (var directory in plan.Directories)
      children[index++] = (directory.Name, directory.FormalIno, (ulong)directory.Dinode, DtDir);
    foreach (var file in plan.Files)
      children[index++] = (file.Name, file.FormalIno, (ulong)file.Dinode, DtRegular);

    var parent = plan.Parent ?? plan;
    this.WriteDirectory(plan.Dinode, plan.FormalIno,
      parentFormalIno: parent.FormalIno,
      parentAddr: (ulong)parent.Dinode,
      system: false,
      children: children,
      nlinkOverride: 2 + plan.Directories.Count,
      mode: SIfDir | 0x1ED);

    foreach (var child in plan.Directories)
      this.WriteDirectoryPlan(child);
  }

  private void WriteFile(FilePlan plan) {
    var size = plan.Payload.Size;
    var blocks = 1L + plan.DataBlocks.Count;
    foreach (var level in plan.Levels) blocks += level.Count;

    this.WriteDinode(
      block: plan.Dinode, formalIno: plan.FormalIno, mode: SIfReg | 0x1A4,
      nlink: 1, size: (ulong)size, blocks: (ulong)blocks, flags: 0,
      payloadFormat: 0, height: plan.Height, entries: 0,
      goalMeta: (ulong)plan.Dinode, goalData: (ulong)plan.Dinode);

    if (plan.Height == 0) {
      if (size > 0)
        this._img.Write((long)plan.Dinode * BlockSize + DinodeHeaderSize, plan.Payload.ToArray());
      return;
    }

    var top = plan.Levels.Count > 0 ? plan.Levels[^1] : plan.DataBlocks;
    WritePointers((long)plan.Dinode * BlockSize + DinodeHeaderSize,
      PointersPerDinode, top, 0);

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

  private void WritePointers(long areaOffset, int capacity, List<long> below, int firstIndex) {
    var count = Math.Min(capacity, below.Count - firstIndex);
    for (var i = 0; i < count; ++i)
      BinaryPrimitives.WriteUInt64BigEndian(
        this.Span(areaOffset + i * 8, 8), (ulong)below[firstIndex + i]);
  }

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

  private void WriteResourceGroup() {
    var rgs = this.ResourceGroups;
    for (var i = 0; i < rgs.Length; i++) {
      var (header, length, data0, data) = rgs[i];
      var dataEnd = data0 + data;
      var bitBytes = (int)((data + 3) / 4);

      var dinodeCount = 0;
      var usedCount = 0;
      foreach (var blk in this._dinodeBlocks)
        if (blk >= data0 && blk < dataEnd) dinodeCount++;
      foreach (var blk in this._usedBlocks)
        if (blk >= data0 && blk < dataEnd) usedCount++;
      var free = data - dinodeCount - usedCount;
      var skip = i + 1 < rgs.Length ? rgs[i + 1].Header - header : 0;

      var o = header * BlockSize;
      WriteMetaHeader(o, MtRg, FmtRg);
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 24, 4), 0u);
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 28, 4), (uint)free);
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 32, 4), (uint)dinodeCount);
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 36, 4), (uint)skip);
      BinaryPrimitives.WriteUInt64BigEndian(Span(o + 40, 8), 0UL);
      BinaryPrimitives.WriteUInt64BigEndian(Span(o + 48, 8), (ulong)data0);
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 56, 4), (uint)data);
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 60, 4), (uint)bitBytes);

      for (var rb = 1; rb < length; rb++)
        this.WriteMetaHeader((header + rb) * BlockSize, MtRb, FmtRb);

      foreach (var blk in this._dinodeBlocks)
        if (blk >= data0 && blk < dataEnd)
          this.SetRgBitmap(header, blk - data0, BlkstDinode);
      foreach (var blk in this._usedBlocks)
        if (blk >= data0 && blk < dataEnd)
          this.SetRgBitmap(header, blk - data0, BlkstUsed);

      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 64, 4), 0u);
      var crc = Crc32(this.Span(o, 128));
      BinaryPrimitives.WriteUInt32BigEndian(Span(o + 64, 4), crc);
    }
  }

  private void SetRgBitmap(long rgHeader, long dataIndex, int state) {
    var byteOffset = dataIndex / 4;
    var shift = (int)(dataIndex % 4) * 2;
    long absByte;
    if (byteOffset < RgrpBitmapBytes) {
      absByte = rgHeader * BlockSize + 128 + byteOffset;
    } else {
      var rest = byteOffset - RgrpBitmapBytes;
      var rbIndex = rest / RbBitmapBytes;
      var inRb = rest % RbBitmapBytes;
      absByte = (rgHeader + 1 + rbIndex) * BlockSize + IndPointerBase + inRb;
    }
    this._img[absByte] = (byte)((this._img[absByte] & ~(0x3 << shift)) | ((state & 0x3) << shift));
  }

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
    var all = new List<(string Name, ulong Fi, ulong Addr, ushort Type)> {
      (".", formalIno, (ulong)block, DtDir),
      ("..", parentFormalIno, parentAddr, DtDir),
    };
    all.AddRange(children);

    var areaLen = BlockSize - DinodeHeaderSize;
    var minimumBytes = 0;
    foreach (var entry in all) {
      var nameLen = Encoding.UTF8.GetByteCount(entry.Name);
      if (nameLen is <= 0 or > 255)
        throw new InvalidDataException($"GFS2 directory entry '{entry.Name}' has an invalid UTF-8 name length.");
      minimumBytes += (DirentSize + nameLen + 7) & ~7;
    }
    if (minimumBytes > areaLen)
      throw new NotSupportedException(
        $"GFS2 directory at block {block} needs {minimumBytes} bytes, beyond the {areaLen}-byte stuffed-directory capacity; ExHash writing is not implemented.");

    var entryCount = (uint)all.Count;
    var nlink = nlinkOverride ?? 2;
    var flags = DifJData | (system ? DifSystem : 0u);

    this.WriteDinode(
      block: block, formalIno: formalIno,
      mode: mode ?? (SIfDir | (system ? 0x1C0u : 0x1EDu)),
      nlink: (uint)nlink,
      size: (ulong)areaLen,
      blocks: 1, flags: flags, payloadFormat: PfDirent, height: 0,
      entries: entryCount,
      goalMeta: (ulong)block, goalData: (ulong)block);

    var areaStart = block * BlockSize + DinodeHeaderSize;
    var pos = 0;
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
    WriteInum(off, fi, addr);
    BinaryPrimitives.WriteUInt32BigEndian(Span(off + 16, 4), Crc32(Encoding.UTF8.GetBytes(name)));
    BinaryPrimitives.WriteUInt16BigEndian(Span(off + 20, 2), recLen);
    BinaryPrimitives.WriteUInt16BigEndian(Span(off + 22, 2), (ushort)nameLen);
    BinaryPrimitives.WriteUInt16BigEndian(Span(off + 24, 2), type);
    var nameBytes = Encoding.UTF8.GetBytes(name);
    nameBytes.AsSpan().CopyTo(Span(off + DirentSize, nameLen));
  }

  private void WriteDinode(long block, ulong formalIno, uint mode, uint nlink,
                           ulong size, ulong blocks, uint flags, uint payloadFormat,
                           ushort height, uint entries,
                           ulong goalMeta, ulong goalData) {
    var o = block * BlockSize;
    WriteMetaHeader(o, MtDinode, FmtDinode);
    WriteInum(o + 24, formalIno, (ulong)block);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 40, 4), mode);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 44, 4), 0u);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 48, 4), 0u);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 52, 4), nlink);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 56, 8), size);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 64, 8), blocks);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 72, 8), this._baseTime);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 80, 8), this._baseTime);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 88, 8), this._baseTime);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 104, 8), goalMeta);
    BinaryPrimitives.WriteUInt64BigEndian(Span(o + 112, 8), goalData);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 128, 4), flags);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 132, 4), payloadFormat);
    BinaryPrimitives.WriteUInt16BigEndian(Span(o + 138, 2), height);
    BinaryPrimitives.WriteUInt16BigEndian(Span(o + 146, 2), 0);
    BinaryPrimitives.WriteUInt32BigEndian(Span(o + 148, 4), entries);

    this.MarkDinode(block);
  }

  private void MarkDinode(long block) {
    this._usedBlocks.Remove(block);
    this._dinodeBlocks.Add(block);
  }

  private void MarkUsed(long block) {
    if (!this._dinodeBlocks.Contains(block))
      this._usedBlocks.Add(block);
  }

  private Span<byte> Span(long offset, int length) => this._img.At(offset, length);

  private void WriteMetaHeader(long absOffset, uint type, uint format) {
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(absOffset, 4), MetaMagic);
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(absOffset + 4, 4), type);
    BinaryPrimitives.WriteUInt32BigEndian(this.Span(absOffset + 16, 4), format);
  }

  private void WriteInum(long absOffset, ulong formalIno, ulong addr) {
    BinaryPrimitives.WriteUInt64BigEndian(this.Span(absOffset, 8), formalIno);
    BinaryPrimitives.WriteUInt64BigEndian(this.Span(absOffset + 8, 8), addr);
  }

  private void WriteCString(long absOffset, string value, int fieldLen) {
    var bytes = Encoding.ASCII.GetBytes(value);
    var n = Math.Min(bytes.Length, fieldLen - 1);
    bytes.AsSpan(0, n).CopyTo(this.Span(absOffset, n));
  }

  private static readonly uint[] _crc32Table = BuildCrc32Table(0xEDB88320u);
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
    return crc;
  }
}
