#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nilfs2;

/// <summary>
/// Writes a kernel-mountable NILFS2 image. Emits the full single-checkpoint log
/// structure the Linux <c>nilfs2</c> driver needs to mount — a super root with
/// the DAT / cpfile / sufile inodes, a segment summary with the spec checksums,
/// an ifile holding the root directory inode, a DAT (disk-address-translation)
/// table, and a root directory carrying the user files — alongside the byte-
/// accurate, CRC-valid superblock pair. A real <c>mount -t nilfs2</c> mounts the
/// image and reads the files back (verified via the libguestfs appliance kernel).
/// </summary>
/// <remarks>
/// <para><b>Verified mountable.</b> The emitted image mounts under the real
/// kernel nilfs2 driver: the directory lists, the file contents read back, and
/// the kernel can write new files into it. This was confirmed segment-by-segment
/// against a real <c>mkfs.nilfs2</c> reference image and gated by a guestfish
/// mount + read-back test.</para>
///
/// <para><b>On-disk layout (4 KiB blocks shown; any legal block size works).</b></para>
/// <list type="bullet">
///   <item><description>0x000..0x3FF — boot sector area (zeroed).</description></item>
///   <item><description>0x400..0x7FF — primary NILFS2 superblock (magic 0x3434 at
///   +6, <c>s_rev_level == 2</c>, crc32_le-sealed <c>s_sum</c>, label at +0xA8).
///   <c>s_last_pseg</c> points at the committed log.</description></item>
///   <item><description>0x800..(log start) — writer-private compact directory
///   guarded by <see cref="WriterMagic"/> ("NILFS2WB") + 8-byte directory length
///   + (u32 name_len, name, u64 payload_offset, u64 size) entries + the payloads.
///   This is read by <see cref="Nilfs2Reader"/> and mutated by the in-place
///   modifier; the kernel never looks here (it jumps straight to
///   <c>s_last_pseg</c>).</description></item>
///   <item><description>log region — a single partial segment at the first
///   segment boundary past the writer body: segment summary, payload blocks
///   (root dir, user-file data, ifile palloc blocks, DAT palloc blocks, cpfile,
///   sufile) and the super root as the last block of the segment.</description></item>
///   <item><description>secondary superblock one block before EOF
///   (<c>dev_size - 4096</c>).</description></item>
/// </list>
///
/// <para><b>Scope.</b> Single checkpoint (cno=1), single partial segment. Each
/// embedded user file uses a NILFS direct block map, so files in the mountable
/// root directory are capped at <see cref="MaxKernelFileBlocks"/> blocks; the
/// writer-private directory always carries every file in full for the reader.
/// Snapshots / multi-checkpoint chains are out of scope.</para>
/// </remarks>
public sealed class Nilfs2Writer {

  /// <summary>
  /// Magic prefix that identifies the writer-private directory. Lets our reader
  /// pick up files without disturbing the kernel mount path (the kernel reads the
  /// superblock's <c>s_last_pseg</c> and never scans for this magic).
  /// </summary>
  internal static readonly byte[] WriterMagic = "NILFS2WB"u8.ToArray();

  /// <summary>
  /// Magic that prefixes each appended log-segment block written by
  /// <c>Nilfs2InPlaceModifier</c> (our snapshot-append mechanism — distinct from
  /// the kernel log). Each segment carries a u64 checkpoint number + a directory
  /// + a payload region; the reader merges all segments by highest-cno-per-name.
  /// </summary>
  internal static readonly byte[] SegmentMagic = "NILFS2SG"u8.ToArray();

  /// <summary>Where the writer-private directory + payload region begins.</summary>
  internal const int SegmentStart = 2048;

  /// <summary>Superblock offset on disk (NILFS2 spec).</summary>
  internal const int SuperblockOffsetOnDisk = 1024;

  /// <summary>Offset of <c>s_last_cno</c> field within the superblock.</summary>
  internal const int LastCnoFieldOffset = 0x38;

  /// <summary>Largest user file (in blocks) embedded into the kernel root dir.</summary>
  internal const int MaxKernelFileBlocks = 6; // NILFS direct bmap capacity.

  // NILFS2 on-disk constants (cross-checked against fs/nilfs2/nilfs2_ondisk.h).
  private const int SegBlocks = 16;                  // NILFS_SEG_MIN_BLOCKS / blocks per segment.
  private const int MinSegments = 128;               // spare segments for the cleaner / new checkpoints.
  private const uint SegsumMagic = 0x1eaffa11;       // NILFS_SEGSUM_MAGIC.
  private const int SegsumHeaderBytes = 64;
  private const int InodeSize = 128;                 // NILFS_MIN_INODE_SIZE.
  private const int CheckpointSize = 192;
  private const int SegUsageSize = 16;
  private const int DatEntrySize = 32;
  private const int SuperRootBytes = 16 + InodeSize * 3; // sr header + 3 inodes = 400.
  private const ulong CtimeFixed = 1_700_000_000ul;  // byte-reproducible.
  private const ulong LiveEnd = 0xffffffffffffffffUL; // de_end == -1 -> entry is live.

  // Segment summary flags (NILFS_SS_*).
  private const ushort SsLogBgn = 0x0001, SsLogEnd = 0x0002, SsSr = 0x0004;
  // Special inode numbers (NILFS_*_INO).
  private const ulong RootIno = 2, DatIno = 3, CpfileIno = 4, SufileIno = 5, IfileIno = 6;
  private const int UserIno = 11;                    // NILFS_USER_INO — first user inode.
  // File modes.
  private const ushort SIfdir = 0x4000, SIfreg = 0x8000;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Deterministic UUID / CRC-seed source so output is byte-reproducible.
  /// </summary>
  private static readonly byte[] FixedUuid = [
    0xC0, 0x4F, 0x5B, 0x21, 0x77, 0xE5, 0x4A, 0x12,
    0x9D, 0xC1, 0xF1, 0x80, 0x03, 0xBE, 0x8C, 0xD2,
  ];
  private const uint FixedCrcSeed = 0x5A4C3A80u;

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name.Replace('\\', '/'), data));
  }

  /// <summary>
  /// Builds the NILFS2 image. <paramref name="blockSize"/> must be a power of two
  /// in [1024, 65536]. <paramref name="volumeLabel"/> is written into the
  /// superblock's volume-label slot at +0xA8.
  /// </summary>
  public byte[] Build(int blockSize = 4096, string? volumeLabel = null) {
    if (blockSize < 1024 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentException("blockSize must be a power of two in [1024, 65536].", nameof(blockSize));

    // The single-block metadata structures (inode table, DAT entry table, root
    // directory) must each fit in one block for the mountable log. Raise the
    // block size to the smallest power of two that holds them; the superblock
    // records the actual size, so the image stays self-consistent and mountable.
    var minBlock = this.MinBlockSizeForLog();
    if (blockSize < minBlock) blockSize = minBlock;
    var logBlockSize = (uint)(System.Numerics.BitOperations.Log2((uint)blockSize) - 10);

    // ── writer-private directory body (read by Nilfs2Reader) ────────────────
    var dirSize = this.ComputeDirectoryBytes();
    var dataSize = 0L;
    foreach (var (_, data) in this._files) dataSize += data.LongLength;
    var bodyBytes = WriterMagic.Length + 8 + dirSize + dataSize;

    // The kernel log starts at the first segment boundary past the writer body.
    var bodyEnd = SegmentStart + bodyBytes;
    var psegStart = (int)(((bodyEnd + (long)blockSize * SegBlocks - 1) / ((long)blockSize * SegBlocks)) * SegBlocks);
    if (psegStart < SegBlocks) psegStart = SegBlocks;

    // Plan the kernel log blocks (counts) so we can size the image up front.
    var plan = PlanLog(psegStart, blockSize);

    // The kernel needs spare segments to commit new checkpoints, so the volume
    // must span several segments past the one holding the log (NILFS reserves
    // segments for the cleaner; mkfs.nilfs2's smallest accepted volume is a few
    // MiB). Size to at least MinSegments segments and one tail block for the
    // secondary superblock, rounded to whole blocks.
    var tailReserve = Math.Max(Nilfs2Superblock.SecondaryBackOffset, blockSize);
    var logEndBytes = (long)(psegStart + plan.NBlocks) * blockSize + tailReserve;
    var minSegBytes = (long)MinSegments * SegBlocks * blockSize;
    var minImageBytes = Math.Max(Math.Max(64L * 1024, minSegBytes), logEndBytes);
    // Round up to a whole number of segments so s_nsegments * blocks_per_segment
    // describes the device exactly.
    var segBytes = (long)SegBlocks * blockSize;
    var imageBytes = ((minImageBytes + segBytes - 1) / segBytes) * segBytes;
    var totalBlocks = (ulong)(imageBytes / blockSize);
    var nSegments = Math.Max(1ul, totalBlocks / SegBlocks);

    var img = new byte[imageBytes];

    // ── writer-private directory + payloads at SegmentStart (reader path) ────
    WritePrivateDirectory(img);

    // ── kernel-mountable log at psegStart ───────────────────────────────────
    WriteKernelLog(img, plan, blockSize);

    // ── superblock pair ─────────────────────────────────────────────────────
    var freeBlocks = (nSegments - 1) * SegBlocks; // one segment consumed by the log.
    Nilfs2Superblock.Encode(
      img.AsSpan(Nilfs2Superblock.PrimaryOffset),
      logBlockSize, nSegments, (ulong)imageBytes, SegBlocks,
      lastCno: 1, lastPseg: (ulong)psegStart, lastSeq: 0, freeBlocks: freeBlocks,
      ctime: CtimeFixed, state: Nilfs2Superblock.StateValid,
      crcSeed: FixedCrcSeed, uuid: FixedUuid, volumeLabel: volumeLabel);

    var secondaryOffset = imageBytes - Nilfs2Superblock.SecondaryBackOffset;
    if (secondaryOffset >= (long)(psegStart + plan.NBlocks) * blockSize)
      Nilfs2Superblock.Encode(
        img.AsSpan((int)secondaryOffset),
        logBlockSize, nSegments, (ulong)imageBytes, SegBlocks,
        lastCno: 1, lastPseg: (ulong)psegStart, lastSeq: 0, freeBlocks: freeBlocks,
        ctime: CtimeFixed, state: Nilfs2Superblock.StateValid,
        crcSeed: FixedCrcSeed, uuid: FixedUuid, volumeLabel: volumeLabel);

    return img;
  }

  /// <summary>Writes the assembled image to a stream.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var img = this.Build();
    output.Write(img, 0, img.Length);
  }

  /// <summary>
  /// Smallest legal block size (power of two in [1024, 65536]) whose single
  /// block can hold each of the metadata structures the mountable log keeps in
  /// one block: the ifile inode table, the DAT entry table, and the root
  /// directory. Files too large for a direct bmap are excluded (they live only
  /// in the writer-private directory).
  /// </summary>
  private int MinBlockSizeForLog() {
    var nFiles = 0;
    var rootDirBytes = 16 + 16; // "." + ".." minimum.
    foreach (var (name, data) in this._files) {
      // Match the embed rule in PlanLog (assume 4 KiB to bound the block count).
      if (name.Contains('/')) continue;
      var nblk = Math.Max(1, (data.Length + 4095) / 4096);
      if (nblk > MaxKernelFileBlocks) continue;
      ++nFiles;
      rootDirBytes += DirRecLen(Encoding.UTF8.GetByteCount(name));
    }
    var inodeTableBytes = (UserIno + nFiles) * InodeSize;
    // DAT entries are indexed by virtual block number; max vblk = number of
    // DAT-translated blocks (root dir + file blocks + 3 ifile + cpfile + sufile).
    var nVblk = 1 + nFiles * MaxKernelFileBlocks + 3 + 2 + 1;
    var datEntryBytes = nVblk * DatEntrySize;

    var need = Math.Max(Math.Max(inodeTableBytes, datEntryBytes), rootDirBytes);
    var bs = 1024;
    while (bs < need && bs < 65536) bs <<= 1;
    return bs;
  }

  private int ComputeDirectoryBytes() {
    var total = 0;
    foreach (var (name, _) in this._files) {
      var nameLen = Encoding.UTF8.GetByteCount(name);
      total += 4 + nameLen + 16;
    }
    return total;
  }

  private void WritePrivateDirectory(byte[] img) {
    var dirSize = this.ComputeDirectoryBytes();
    var seg = img.AsSpan(SegmentStart);
    WriterMagic.CopyTo(seg);
    BinaryPrimitives.WriteInt64LittleEndian(seg[WriterMagic.Length..], dirSize);

    var dirOffset = WriterMagic.Length + 8;
    var payloadOffset = dirOffset + dirSize;
    var payloadCursor = 0L;
    foreach (var (name, data) in this._files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      BinaryPrimitives.WriteUInt32LittleEndian(seg[dirOffset..], (uint)nameBytes.Length);
      nameBytes.CopyTo(seg[(dirOffset + 4)..]);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length)..], payloadCursor);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length + 8)..], data.LongLength);
      dirOffset += 4 + nameBytes.Length + 16;

      data.CopyTo(seg[(payloadOffset + (int)payloadCursor)..]);
      payloadCursor += data.LongLength;
    }
  }

  // ── kernel log ────────────────────────────────────────────────────────────

  /// <summary>One user file embedded into the mountable root directory.</summary>
  private sealed class KFile {
    public required string Name;
    public required byte[] Data;
    public ulong Ino;
    public int NBlocks;
    public int[] Phys = [];   // physical block numbers of the data blocks.
    public ulong[] Vblk = []; // virtual block numbers (DAT-translated).
  }

  private sealed class LogPlan {
    public int PsegStart;
    public int NBlocks;
    public List<KFile> Files = [];
    public int PRootDir, PIfileGd, PIfileBm, PIfileIt, PDatGd, PDatBm, PDatEntry, PCpfile, PSufile, PSr;
    public ulong VRootDir, VIfileGd, VIfileBm, VIfileIt, VCpfile, VSufile;
    public int NInoUsed;
    public Dictionary<ulong, int> DatMap = [];
  }

  /// <summary>
  /// Assigns physical + virtual block numbers for the single partial segment.
  /// Block order on disk: summary, root dir, user data, ifile palloc (3), DAT
  /// palloc (3), cpfile, sufile, super root (last).
  /// </summary>
  private LogPlan PlanLog(int psegStart, int blockSize) {
    var p = new LogPlan { PsegStart = psegStart };

    // Only files that fit in a direct bmap are embedded into the mountable tree;
    // the writer-private directory still carries every file for the reader.
    var ino = (ulong)UserIno;
    foreach (var (name, data) in this._files) {
      // Subdirectories are not materialised in the mountable tree (only a flat
      // root directory); such files stay readable via the writer-private
      // directory. Files too large for a direct bmap are likewise skipped.
      if (name.Contains('/')) continue;
      var nblk = Math.Max(1, (data.Length + blockSize - 1) / blockSize);
      if (nblk > MaxKernelFileBlocks) continue;
      p.Files.Add(new KFile { Name = name, Data = data, Ino = ino, NBlocks = nblk });
      ++ino;
    }
    p.NInoUsed = (int)ino;

    var cur = psegStart + 1; // block 0 of the pseg is the segment summary.
    p.PRootDir = cur++;
    foreach (var f in p.Files) {
      f.Phys = new int[f.NBlocks];
      for (var i = 0; i < f.NBlocks; ++i) f.Phys[i] = cur++;
    }
    p.PIfileGd = cur++; p.PIfileBm = cur++; p.PIfileIt = cur++;
    p.PDatGd = cur++; p.PDatBm = cur++; p.PDatEntry = cur++;
    p.PCpfile = cur++; p.PSufile = cur++;
    p.PSr = cur++;
    p.NBlocks = cur - psegStart;

    // Virtual block numbers (DAT-translated). Only the DAT itself is physical.
    var v = 1ul;
    ulong VAlloc(int phys) { var vb = v++; p.DatMap[vb] = phys; return vb; }
    p.VRootDir = VAlloc(p.PRootDir);
    foreach (var f in p.Files) {
      f.Vblk = new ulong[f.NBlocks];
      for (var i = 0; i < f.NBlocks; ++i) f.Vblk[i] = VAlloc(f.Phys[i]);
    }
    p.VIfileGd = VAlloc(p.PIfileGd); p.VIfileBm = VAlloc(p.PIfileBm); p.VIfileIt = VAlloc(p.PIfileIt);
    p.VCpfile = VAlloc(p.PCpfile); p.VSufile = VAlloc(p.PSufile);
    return p;
  }

  private void WriteKernelLog(byte[] img, LogPlan p, int blockSize) {
    var nSegments = (ulong)(img.LongLength / blockSize) / SegBlocks;

    // ── DAT (physical pointers) ───────────────────────────────────────────
    var datEntry = Block(blockSize);
    foreach (var (vblk, phys) in p.DatMap) {
      var o = (int)vblk * DatEntrySize;
      BinaryPrimitives.WriteUInt64LittleEndian(datEntry.AsSpan(o), (ulong)phys); // de_blocknr
      BinaryPrimitives.WriteUInt64LittleEndian(datEntry.AsSpan(o + 8), 1);       // de_start (cno)
      BinaryPrimitives.WriteUInt64LittleEndian(datEntry.AsSpan(o + 16), LiveEnd);// de_end (-1 = live)
    }
    WriteBlock(img, p.PDatEntry, blockSize, datEntry);
    var nDatUsed = p.DatMap.Count == 0 ? 1 : ((int)p.DatMap.Keys.Max() + 1);
    WritePallocMeta(img, p.PDatGd, p.PDatBm, blockSize, nDatUsed);

    // ── ifile (palloc: group desc, bitmap, inode table) ────────────────────
    var it = Block(blockSize);
    for (var i = 0; i < p.NInoUsed; ++i) {
      var io = i * InodeSize;
      if (i == (int)RootIno)
        WriteInode(it.AsSpan(io), 1, (ulong)blockSize, (ushort)(SIfdir | 0x1ED), 2, [p.VRootDir]);
      else
        WriteInode(it.AsSpan(io), 0, 0, SIfreg, 1, []);
    }
    foreach (var f in p.Files)
      WriteInode(it.AsSpan((int)f.Ino * InodeSize), (ulong)f.NBlocks, (ulong)f.Data.Length,
        (ushort)(SIfreg | 0x1A4), 1, f.Vblk);
    WriteBlock(img, p.PIfileIt, blockSize, it);
    WritePallocMeta(img, p.PIfileGd, p.PIfileBm, blockSize, p.NInoUsed);

    // ── root directory ─────────────────────────────────────────────────────
    var rd = Block(blockSize);
    var off = 0;
    off += WriteDirent(rd.AsSpan(off), RootIno, ".", FtDir, 16);
    var dotdotRec = p.Files.Count > 0 ? 16 : (blockSize - off);
    off += WriteDirent(rd.AsSpan(off), RootIno, "..", FtDir, dotdotRec);
    for (var i = 0; i < p.Files.Count; ++i) {
      var f = p.Files[i];
      var last = i == p.Files.Count - 1;
      var rec = last ? (blockSize - off) : DirRecLen(Encoding.UTF8.GetByteCount(f.Name));
      WriteDirent(rd.AsSpan(off), f.Ino, f.Name, FtReg, rec);
      off += last ? rec : DirRecLen(Encoding.UTF8.GetByteCount(f.Name));
    }
    WriteBlock(img, p.PRootDir, blockSize, rd);
    foreach (var f in p.Files)
      for (var bi = 0; bi < f.NBlocks; ++bi) {
        var chunk = f.Data.AsSpan(bi * blockSize, Math.Min(blockSize, f.Data.Length - bi * blockSize));
        chunk.CopyTo(img.AsSpan(f.Phys[bi] * blockSize));
      }

    // ── cpfile ──────────────────────────────────────────────────────────────
    var cp = Block(blockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(0), 1); // ch_ncheckpoints
    var cpoff = ((48 + CheckpointSize - 1) / CheckpointSize) * CheckpointSize; // first cp slot.
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 24), 1);                 // cp_cno
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 32), CtimeFixed);        // cp_create
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 40), (ulong)p.NBlocks);  // cp_nblk_inc
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 48), (ulong)p.NInoUsed); // cp_inodes_count
    BinaryPrimitives.WriteUInt64LittleEndian(cp.AsSpan(cpoff + 56), (ulong)p.NBlocks);  // cp_blocks_count
    WriteInode(cp.AsSpan(cpoff + 64), 3, (ulong)(3 * blockSize), SIfreg, 1,
      [p.VIfileGd, p.VIfileBm, p.VIfileIt]); // cp_ifile_inode
    WriteBlock(img, p.PCpfile, blockSize, cp);

    // ── sufile ──────────────────────────────────────────────────────────────
    var segOfPseg = p.PsegStart / SegBlocks;
    var su = Block(blockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(0), nSegments - 1);     // sh_ncleansegs
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(8), 1);                 // sh_ndirtysegs
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(16), (ulong)segOfPseg); // sh_last_alloc
    var suoff = ((24 + SegUsageSize - 1) / SegUsageSize) * SegUsageSize;
    var suSlot = suoff + segOfPseg * SegUsageSize;
    BinaryPrimitives.WriteUInt64LittleEndian(su.AsSpan(suSlot), CtimeFixed);       // su_lastmod
    BinaryPrimitives.WriteUInt32LittleEndian(su.AsSpan(suSlot + 8), (uint)p.NBlocks);// su_nblocks
    BinaryPrimitives.WriteUInt32LittleEndian(su.AsSpan(suSlot + 12), 0x1);         // su_flags = DIRTY
    WriteBlock(img, p.PSufile, blockSize, su);

    // ── super root (last block of the pseg) ─────────────────────────────────
    var sr = Block(blockSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sr.AsSpan(4), SuperRootBytes); // sr_bytes
    BinaryPrimitives.WriteUInt64LittleEndian(sr.AsSpan(8), CtimeFixed);     // sr_nongc_ctime
    WriteInode(sr.AsSpan(16), 3, 0, SIfreg, 1,
      [(ulong)p.PDatGd, (ulong)p.PDatBm, (ulong)p.PDatEntry]); // DAT: physical ptrs.
    WriteInode(sr.AsSpan(16 + InodeSize), 1, 0, SIfreg, 1, [p.VCpfile]); // cpfile: virtual.
    WriteInode(sr.AsSpan(16 + InodeSize * 2), 1, 0, SIfreg, 1, [p.VSufile]); // sufile: virtual.
    var srSum = Nilfs2Superblock.Crc32Le(FixedCrcSeed, sr.AsSpan(4, SuperRootBytes - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(sr.AsSpan(0), srSum); // sr_sum over [4..sr_bytes].
    WriteBlock(img, p.PSr, blockSize, sr);

    // ── segment summary (block 0 of the pseg) ───────────────────────────────
    WriteSegmentSummary(img, p, blockSize, segOfPseg);
  }

  private void WriteSegmentSummary(byte[] img, LogPlan p, int blockSize, int segOfPseg) {
    using var ms = new MemoryStream();
    void Finfo(ulong ino, int nblk, int ndatablk) {
      Span<byte> b = stackalloc byte[24];
      BinaryPrimitives.WriteUInt64LittleEndian(b, ino);
      BinaryPrimitives.WriteUInt64LittleEndian(b[8..], 1); // fi_cno
      BinaryPrimitives.WriteUInt32LittleEndian(b[16..], (uint)nblk);
      BinaryPrimitives.WriteUInt32LittleEndian(b[20..], (uint)ndatablk);
      ms.Write(b);
    }
    void BinfoV(ulong vblk, ulong blkoff) {
      Span<byte> b = stackalloc byte[16];
      BinaryPrimitives.WriteUInt64LittleEndian(b, vblk);
      BinaryPrimitives.WriteUInt64LittleEndian(b[8..], blkoff);
      ms.Write(b);
    }
    void BinfoDat(ulong blkoff, byte level) {
      Span<byte> b = stackalloc byte[16];
      BinaryPrimitives.WriteUInt64LittleEndian(b, blkoff);
      b[8] = level;
      ms.Write(b);
    }

    var nfinfo = 0;
    // Order MUST match the physical payload order in PlanLog.
    Finfo(RootIno, 1, 1); BinfoV(p.VRootDir, 0); ++nfinfo;
    foreach (var f in p.Files) {
      Finfo(f.Ino, f.NBlocks, f.NBlocks);
      for (var bi = 0; bi < f.NBlocks; ++bi) BinfoV(f.Vblk[bi], (ulong)bi);
      ++nfinfo;
    }
    Finfo(IfileIno, 3, 3); BinfoV(p.VIfileGd, 0); BinfoV(p.VIfileBm, 1); BinfoV(p.VIfileIt, 2); ++nfinfo;
    Finfo(DatIno, 3, 3); BinfoDat(0, 0); BinfoDat(1, 0); BinfoDat(2, 0); ++nfinfo;
    Finfo(CpfileIno, 1, 1); BinfoV(p.VCpfile, 0); ++nfinfo;
    Finfo(SufileIno, 1, 1); BinfoV(p.VSufile, 0); ++nfinfo;

    var summary = ms.ToArray();
    var sumbytes = SegsumHeaderBytes + summary.Length;

    var ss = Block(blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(8), SegsumMagic);
    BinaryPrimitives.WriteUInt16LittleEndian(ss.AsSpan(12), SegsumHeaderBytes); // ss_bytes
    BinaryPrimitives.WriteUInt16LittleEndian(ss.AsSpan(14), (ushort)(SsLogBgn | SsLogEnd | SsSr));
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(16), 0);                 // ss_seq
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(24), CtimeFixed);        // ss_create
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(32), (ulong)((segOfPseg + 1) * SegBlocks)); // ss_next
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(40), (uint)p.NBlocks);   // ss_nblocks
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(44), (uint)nfinfo);      // ss_nfinfo
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(48), (uint)sumbytes);    // ss_sumbytes
    BinaryPrimitives.WriteUInt64LittleEndian(ss.AsSpan(56), 1);                 // ss_cno
    summary.CopyTo(ss.AsSpan(SegsumHeaderBytes));

    // ss_sumsum = crc32_le over [8 .. sumbytes].
    var sumsum = Nilfs2Superblock.Crc32Le(FixedCrcSeed, ss.AsSpan(8, sumbytes - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(4), sumsum);
    WriteBlock(img, p.PsegStart, blockSize, ss);

    // ss_datasum = crc32_le over [pseg+4 .. pseg + nblocks*blockSize].
    var datasum = Nilfs2Superblock.Crc32Le(FixedCrcSeed,
      img.AsSpan(p.PsegStart * blockSize + 4, p.NBlocks * blockSize - 4));
    BinaryPrimitives.WriteUInt32LittleEndian(ss.AsSpan(0), datasum);
    WriteBlock(img, p.PsegStart, blockSize, ss);
  }

  // ── helpers ─────────────────────────────────────────────────────────────

  private const byte FtReg = 1, FtDir = 2; // NILFS_FT_* low 3 bits.

  private static byte[] Block(int blockSize) => new byte[blockSize];

  private static void WriteBlock(byte[] img, int blk, int blockSize, byte[] data) =>
    data.AsSpan(0, blockSize).CopyTo(img.AsSpan(blk * blockSize));

  /// <summary>Writes a <c>nilfs_inode</c> with a direct block map.</summary>
  private static void WriteInode(Span<byte> dst, ulong blocks, ulong size, ushort mode,
      ushort links, ReadOnlySpan<ulong> bmapPtrs) {
    dst[..InodeSize].Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(dst, blocks);          // i_blocks
    BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], size);       // i_size
    BinaryPrimitives.WriteUInt64LittleEndian(dst[16..], CtimeFixed);// i_ctime
    BinaryPrimitives.WriteUInt64LittleEndian(dst[24..], CtimeFixed);// i_mtime
    BinaryPrimitives.WriteUInt16LittleEndian(dst[48..], mode);      // i_mode
    BinaryPrimitives.WriteUInt16LittleEndian(dst[50..], links);     // i_links_count
    // i_bmap[7] at +56: a direct map has bmap[0] = 0 (NILFS_BMAP_LARGE clear),
    // bmap[1+key] = pointer for that key.
    for (var k = 0; k < bmapPtrs.Length && k < 6; ++k)
      BinaryPrimitives.WriteUInt64LittleEndian(dst[(56 + (k + 1) * 8)..], bmapPtrs[k]);
  }

  /// <summary>Writes a palloc group-descriptor block + bitmap block for group 0.</summary>
  private static void WritePallocMeta(byte[] img, int gdBlk, int bmBlk, int blockSize, int used) {
    var gd = Block(blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(gd.AsSpan(0), (uint)(blockSize * 8 - used)); // pg_nfrees.
    WriteBlock(img, gdBlk, blockSize, gd);

    var bm = Block(blockSize);
    for (var i = 0; i < used; ++i) bm[i >> 3] |= (byte)(1 << (i & 7));
    WriteBlock(img, bmBlk, blockSize, bm);
  }

  private static int DirRecLen(int nameLen) => (nameLen + 12 + 7) & ~7; // NILFS_DIR_REC_LEN.

  private static int WriteDirent(Span<byte> dst, ulong ino, string name, byte fileType, int recLen) {
    var nb = Encoding.UTF8.GetBytes(name);
    BinaryPrimitives.WriteUInt64LittleEndian(dst, ino);
    BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], (ushort)recLen);
    dst[10] = (byte)nb.Length;
    dst[11] = fileType;
    nb.CopyTo(dst[12..]);
    return recLen;
  }
}
