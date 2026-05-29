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

    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var src = srcOffset;
      var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = dst;
        image.Write(buffer, 0, chunk);
        src += chunk;
        dst += chunk;
        remaining -= chunk;
      }
      // Crash barrier: data must land on disk before metadata references it.
      image.Flush();

      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length;
        src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src;
          image.Write(buffer, 0, chunk);
          src += chunk;
          remaining -= chunk;
        }
        image.Flush();
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// Streaming targeted patch: locates the target inode by walking the root
  /// directory through <see cref="SectorCache"/>, rewrites a single 16-byte
  /// BMBT_REC, and recomputes the inode's CRC-32C — total disk write is one
  /// inode-sized region (256 bytes on V5).
  /// </remarks>
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

  private static void BackfillCrc(Span<byte> block, int crcFieldOffset) {
    block[crcFieldOffset] = 0;
    block[crcFieldOffset + 1] = 0;
    block[crcFieldOffset + 2] = 0;
    block[crcFieldOffset + 3] = 0;
    var crc = Crc32.Compute(block, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(block[crcFieldOffset..], crc);
  }
}
