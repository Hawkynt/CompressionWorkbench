#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Xfs;

/// <summary>
/// In-place XFS block mover for the WORM writer profile. Moves data extents
/// within an XFS image and patches the inode's BMBT_REC packed 128-bit extent
/// record so the file remains reachable at its new location, then recomputes
/// CRC-32C on every touched inode.
///
/// <para>Only single-extent inodes are supported (the common case from our
/// writer). Multi-extent inodes throw <see cref="NotSupportedException"/> and
/// the rebuild fallback takes over.</para>
///
/// <para>The bundled <see cref="XfsWriter"/> uses a simple flat layout: one
/// AG with all file data in consecutive blocks, so only the file inode and
/// free-space B-tree need patching.</para>
///
/// <para>
/// Streaming: the mover never loads the whole image. <see cref="Init(Stream)"/>
/// reads only the 512-byte superblock; metadata updates are targeted reads/
/// writes via <see cref="SectorCache"/> + <see cref="Stream.Flush"/> barriers
/// so a multi-TB XFS image needs only ~256 MB cache RAM regardless of size.
/// </para>
/// </summary>
public sealed class XfsBlockMover : IFilesystemBlockMover {

  private const uint XfsMagic = 0x58465342; // "XFSB"
  private const ushort InodeMagic = 0x494E;  // "IN"
  private const int DiCrcOffset = 100;

  // Cached superblock fields populated by Init().
  private int _blockSize;
  private ushort _inodeSize;
  private ulong _rootIno;
  private uint _agBlocks;
  private uint _agCount;
  private ushort _versionNum;
  private byte _agBlkLog;
  private uint _featuresIncompat;
  private bool _isV5;
  private bool _hasFtype;
  private int _forkOff;
  private long _imageLength;

  /// <summary>
  /// Streaming init — reads only the 512-byte superblock at offset 0. All
  /// subsequent metadata access goes through <see cref="SectorCache"/>.
  /// </summary>
  /// <summary>A block, which is what an extent record counts in.</summary>
  public int BlockSize => this._blockSizeForPlanner;

  /// <summary>First byte a file's extent may occupy: past the volume's own head.</summary>
  public long FirstDataByte => this._firstDataByte;

  /// <summary>
  /// Each call repoints the record naming the run it is given and leaves the
  /// inode's other records alone.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  private int _blockSizeForPlanner = 4096;

  private long _firstDataByte;

  /// <summary>
  /// Performs the init operation.
  /// </summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 512)
      throw new InvalidDataException("XFS image too small to contain a superblock.");

    Span<byte> sb = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(sb);

    if (BinaryPrimitives.ReadUInt32BigEndian(sb) != XfsMagic)
      throw new InvalidDataException("XFS superblock magic missing.");

    _blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(sb[4..]);
    _rootIno = BinaryPrimitives.ReadUInt64BigEndian(sb[56..]);
    _agBlocks = BinaryPrimitives.ReadUInt32BigEndian(sb[84..]);
    _agCount = BinaryPrimitives.ReadUInt32BigEndian(sb[88..]);
    _versionNum = BinaryPrimitives.ReadUInt16BigEndian(sb[100..]);
    _inodeSize = BinaryPrimitives.ReadUInt16BigEndian(sb[104..]);
    _agBlkLog = sb[124];
    _featuresIncompat = 0;
    if ((_versionNum & 0xF) >= 5)
      _featuresIncompat = BinaryPrimitives.ReadUInt32BigEndian(sb[216..]);

    if (_blockSize == 0) _blockSize = 4096;
    if (_inodeSize == 0) _inodeSize = 256;

    // The log ends where a file may begin; everything before it is the
    // volume's own, and a pass that starts lower writes over it.
    this._blockSizeForPlanner = _blockSize;
    var logStart = BinaryPrimitives.ReadUInt64BigEndian(sb[48..]);
    var logBlocks = BinaryPrimitives.ReadUInt32BigEndian(sb[96..]);
    var logAg = (long)(logStart >> (_agBlkLog == 0 ? 1 : _agBlkLog));
    var logAgBlock = (long)(logStart & ((1UL << (_agBlkLog == 0 ? 1 : _agBlkLog)) - 1));
    this._firstDataByte = (logAg * _agBlocks + logAgBlock + logBlocks) * (long)_blockSize;
    if (_agBlocks == 0) _agBlocks = (uint)(image.Length / _blockSize);
    if (_agBlkLog == 0) {
      var v = _agBlocks;
      while (v > 1) { _agBlkLog++; v >>= 1; }
    }
    _isV5 = (_versionNum & 0xF) >= 5;
    _hasFtype = (_featuresIncompat & 0x1) != 0;
    _forkOff = _isV5 ? 176 : 100;
    _imageLength = image.Length;
  }

  // ── IFilesystemBlockMover ──────────────────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Streaming targeted patch: locates the target inode by walking the root
  /// directory through <see cref="SectorCache"/>, rewrites a single 16-byte
  /// BMBT_REC, and recomputes the inode's CRC-32C — total disk write is one
  /// inode-sized region (256 bytes on V5).
  /// </remarks>
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    if (_blockSize == 0)
      Init(image);

    using var cache = new SectorCache(image);

    // Find the target inode by walking the root directory through the cache.
    var targetIno = FindInodeByNameStream(cache, _rootIno, fileName);
    if (targetIno == 0) return;

    var inodeOff = InodeOffset(targetIno);
    if (inodeOff < 0 || inodeOff + _inodeSize > _imageLength) return;

    // Read the inode via cache, patch, write back as a single region.
    var inode = cache.Read(inodeOff, _inodeSize);
    if (BinaryPrimitives.ReadUInt16BigEndian(inode) != InodeMagic) return;

    var format = inode[5];
    if (format != 2)
      throw new NotSupportedException("XfsBlockMover: only extents-format inodes are supported.");

    var nextents = BinaryPrimitives.ReadUInt32BigEndian(inode.AsSpan(76));
    if (nextents > 1)
      throw new NotSupportedException(
        $"XfsBlockMover: multi-extent inodes ({nextents} extents) are not supported. Use rebuild fallback.");

    if (nextents == 0) return;

    // Read the single BMBT_REC.
    if (_forkOff + 16 > _inodeSize) return;
    var hi = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(_forkOff));
    var lo = BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(_forkOff + 8));

    var blockCount = (ulong)(lo & 0x1FFFFF);
    var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
    var extentByteOff = (long)startBlock * _blockSize;
    if (extentByteOff != oldOffset) return;

    // Compute new start block from new offset.
    var newStartBlock = (ulong)newOffset / (ulong)_blockSize;

    // Rebuild the BMBT_REC:
    //   hi[63]    = flag (0)
    //   hi[62:9]  = startoff (logical file offset in blocks) — preserve from original
    //   hi[8:0]   = upper 9 bits of startblock
    //   lo[63:21] = lower 43 bits of startblock
    //   lo[20:0]  = blockcount
    var startoff = (hi >> 9) & 0x003FFFFFFFFFFFFF; // 54 bits
    var flag = hi >> 63;
    var newHi = (flag << 63) | (startoff << 9) | ((newStartBlock >> 43) & 0x1FF);
    var newLo = ((newStartBlock & 0x7FFFFFFFFFF) << 21) | (blockCount & 0x1FFFFF);

    BinaryPrimitives.WriteUInt64BigEndian(inode.AsSpan(_forkOff), newHi);
    BinaryPrimitives.WriteUInt64BigEndian(inode.AsSpan(_forkOff + 8), newLo);

    // Recompute CRC-32C on the inode if v5.
    if (_isV5)
      BackfillCrc(inode.AsSpan(0, _inodeSize), DiCrcOffset);

    // Targeted write — just the inode-sized region.
    image.Position = inodeOff;
    image.Write(inode, 0, _inodeSize);
    cache.Invalidate(inodeOff, _inodeSize);
    image.Flush();
  }

  // ── Inode lookup helpers ──────────────────────────────────────────────

  private long InodeOffset(ulong ino) {
    var inoPerBlock = _blockSize / _inodeSize;
    var inoPbLog = 0;
    for (var v = inoPerBlock; v > 1; v >>= 1) inoPbLog++;
    var aginoLog = _agBlkLog + inoPbLog;
    var agNo = (uint)(ino >> aginoLog);
    var agIno = ino & ((1UL << aginoLog) - 1);
    var block = agIno / (ulong)inoPerBlock;
    var offset = agIno % (ulong)inoPerBlock;
    return (long)((agNo * _agBlocks + block) * (uint)_blockSize + offset * _inodeSize);
  }

  /// <summary>
  /// Walks the root directory (short-form or extents-form) through the cache
  /// to find an inode number by file name. Returns 0 if not found.
  /// </summary>
  private ulong FindInodeByNameStream(SectorCache cache, ulong rootIno, string targetName) {
    var rootOff = InodeOffset(rootIno);
    if (rootOff < 0 || rootOff + _inodeSize > _imageLength) return 0;

    var rootInode = cache.Read(rootOff, _inodeSize);
    if (BinaryPrimitives.ReadUInt16BigEndian(rootInode) != InodeMagic) return 0;

    var mode = BinaryPrimitives.ReadUInt16BigEndian(rootInode.AsSpan(2));
    if ((mode & 0xF000) != 0x4000) return 0; // not directory

    var format = rootInode[5];
    var size = (long)BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(56));

    if (format == 1) {
      // Short-form directory — entries are stored inline in the inode.
      return FindInShortFormDir(rootInode, _forkOff, Math.Min((int)size, _inodeSize - _forkOff),
        _hasFtype, targetName);
    }

    if (format == 2) {
      // Extents-format directory — read each block via the cache.
      var nextents = BinaryPrimitives.ReadUInt32BigEndian(rootInode.AsSpan(76));
      if (nextents == 0 || nextents > 100) return 0;

      var extOff = _forkOff;
      for (uint e = 0; e < nextents; e++) {
        if (extOff + 16 > _inodeSize) break;
        var hi = BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(extOff));
        var lo = BinaryPrimitives.ReadUInt64BigEndian(rootInode.AsSpan(extOff + 8));
        extOff += 16;
        var blockCount = (int)(lo & 0x1FFFFF);
        var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);

        for (var b = 0; b < blockCount; b++) {
          var blockOff = (long)(startBlock + (ulong)b) * _blockSize;
          if (blockOff + 8 > _imageLength) continue;
          var blockBytes = cache.Read(blockOff, _blockSize);
          var result = FindInBlockFormDir(blockBytes, _blockSize, targetName);
          if (result != 0) return result;
        }
      }
    }

    return 0;
  }

  private static ulong FindInShortFormDir(byte[] data, int dataOff, int dataLen, bool hasFtype,
      string targetName) {
    if (dataOff + 6 > data.Length) return 0;
    var count = data[dataOff];
    var i8count = data[dataOff + 1];
    var pos = dataOff + 6;
    if (i8count > 0) pos = dataOff + 10;

    for (var i = 0; i < count + i8count && pos + 3 < dataOff + dataLen; i++) {
      var nameLen = data[pos];
      if (nameLen == 0) break;
      if (pos + 3 + nameLen > data.Length) break;
      var name = Encoding.UTF8.GetString(data, pos + 3, nameLen);
      var ftypeLen = hasFtype ? 1 : 0;
      var inoPos = pos + 3 + nameLen + ftypeLen;
      ulong childIno;
      if (i < count && i8count == 0) {
        if (inoPos + 4 > data.Length) break;
        childIno = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(inoPos));
        pos = inoPos + 4;
      } else {
        if (inoPos + 8 > data.Length) break;
        childIno = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(inoPos));
        pos = inoPos + 8;
      }

      if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
          targetName.Equals("*", StringComparison.Ordinal))
        return childIno;
    }

    return 0;
  }

  private static ulong FindInBlockFormDir(byte[] data, int blockLen, string targetName) {
    var pos = 0;
    var end = blockLen;
    if (pos + 4 <= data.Length) {
      var bMagic = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      if (bMagic is 0x58443242 or 0x58444233) pos += 48; // skip header
    }

    while (pos + 12 <= end && pos + 12 <= data.Length) {
      var entIno = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos));
      var nameLen = data[pos + 8];
      if (nameLen == 0 || entIno == 0) { pos += 12; continue; }
      if (pos + 11 + nameLen > data.Length) break;
      var name = Encoding.UTF8.GetString(data, pos + 9, nameLen);

      if (name != "." && name != ".." &&
          (name.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
           targetName.Equals("*", StringComparison.Ordinal)))
        return entIno;

      var entLen = 8 + 1 + nameLen + 2;
      entLen = (entLen + 7) & ~7;
      pos += entLen;
    }

    return 0;
  }

  // ── CRC helpers ──────────────────────────────────────────────────────

  /// <summary>
  /// Writes each allocation group's free-space btrees from the layout the pass
  /// finished with.
  /// </summary>
  /// <remarks>
  /// A group records its free space twice — once ordered by where an extent
  /// starts and once by how long it is — and the header carries the totals.
  /// Moving files changes which blocks are free, so all three have to be said
  /// again; leaving them is a volume xfs_repair calls corrupt even though every
  /// file reads back.
  /// </remarks>
  public void SettleFreeSpace(Stream image, IEnumerable<(long Offset, long Length)> live) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(live);
    if (this._nodeSizeUnused) { /* geometry is read below */ }

    var superblock = new byte[SectorSize];
    image.Position = 0;
    image.ReadExactly(superblock);

    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(superblock.AsSpan(4));
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(superblock.AsSpan(84));
    var agCount = BinaryPrimitives.ReadUInt32BigEndian(superblock.AsSpan(88));
    if (blockSize <= 0 || agBlocks == 0 || agCount == 0) return;

    var taken = new HashSet<long>();
    foreach (var (offset, length) in live) {
      if (length <= 0) continue;
      var first = offset / blockSize;
      var last = (offset + length + blockSize - 1) / blockSize;
      for (var block = first; block < last; ++block)
        if (block >= 0) taken.Add(block);
    }

    ulong freeTotal = 0;
    for (var ag = 0u; ag < agCount; ++ag) {
      var agStart = (long)ag * agBlocks;
      var agEnd = Math.Min(agStart + agBlocks, image.Length / blockSize);

      // Whatever no structure and no file covers is free.
      var free = new List<(uint Start, uint Length)>();
      var block = agStart;
      while (block < agEnd) {
        if (taken.Contains(block)) { ++block; continue; }

        var runStart = block;
        while (block < agEnd && !taken.Contains(block)) ++block;
        free.Add(((uint)(runStart - agStart), (uint)(block - runStart)));
      }

      foreach (var (_, length) in free) freeTotal += length;
      WriteFreeBtree(image, agStart, blockSize, BnobtBlock, free.OrderBy(f => f.Start).ToList());
      WriteFreeBtree(image, agStart, blockSize, CntbtBlock,
        free.OrderBy(f => f.Length).ThenBy(f => f.Start).ToList());

      var agf = new byte[SectorSize];
      image.Position = (agStart + AgfBlock) * blockSize + AgfSectorOffset;
      image.ReadExactly(agf);
      BinaryPrimitives.WriteUInt32BigEndian(agf.AsSpan(52), (uint)free.Sum(f => f.Length));
      BinaryPrimitives.WriteUInt32BigEndian(agf.AsSpan(56),
        free.Count == 0 ? 0u : free.Max(f => f.Length));
      BackfillCrc(agf, AgfCrcOffset);
      image.Position = (agStart + AgfBlock) * blockSize + AgfSectorOffset;
      image.Write(agf, 0, SectorSize);
    }

    // The superblock's count of free blocks is the sum over the groups.
    BinaryPrimitives.WriteUInt64BigEndian(superblock.AsSpan(144), freeTotal);
    BackfillCrc(superblock, SbCrcOffset);
    image.Position = 0;
    image.Write(superblock, 0, SectorSize);
    image.Flush();
  }

  /// <summary>Writes one free-space btree leaf with the records it is given.</summary>
  private static void WriteFreeBtree(Stream image, long agStart, int blockSize, int blockInAg,
      IReadOnlyList<(uint Start, uint Length)> records) {
    var at = (agStart + blockInAg) * blockSize;
    if (at < 0 || at + blockSize > image.Length) return;

    var block = new byte[blockSize];
    image.Position = at;
    image.ReadExactly(block);

    BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(6), (ushort)Math.Min(records.Count, 505));
    for (var i = 0; i < records.Count && i < 505; ++i) {
      BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(BtreeRecOffset + i * 8), records[i].Start);
      BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(BtreeRecOffset + i * 8 + 4), records[i].Length);
    }

    BackfillCrc(block, BtreeCrcOffset);
    image.Position = at;
    image.Write(block, 0, blockSize);
  }

  /// <summary>Blocks of an allocation group the format puts its own structures in.</summary>
  private const int AgfBlock = 0;

  private const int AgfSectorOffset = 512;

  private const int BnobtBlock = 1;

  private const int CntbtBlock = 2;

  private const int SectorSize = 512;

  private const int BtreeRecOffset = 56;

  private const int BtreeCrcOffset = 52;

  private const int AgfCrcOffset = 216;

  private const int SbCrcOffset = 224;

  /// <summary>Never true; the geometry this needs is read where it is used.</summary>
  private bool _nodeSizeUnused => false;

  private static void BackfillCrc(Span<byte> block, int crcFieldOffset) {
    block[crcFieldOffset] = 0;
    block[crcFieldOffset + 1] = 0;
    block[crcFieldOffset + 2] = 0;
    block[crcFieldOffset + 3] = 0;
    var crc = Crc32.Compute(block, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(block[crcFieldOffset..], crc);
  }
}
