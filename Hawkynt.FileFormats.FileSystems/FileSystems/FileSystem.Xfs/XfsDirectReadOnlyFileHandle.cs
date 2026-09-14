#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Xfs;

/// <summary>
/// Positional read-only view over an XFS local or inline-extent data fork.
/// Holes and unwritten extents read as zeroes; written extents are addressed
/// directly on the backing data device without materializing the whole file.
/// </summary>
internal sealed class XfsDirectReadOnlyFileHandle : IFilesystemFileHandle {
  private const ulong ExtentFlagMask = 1UL << 63;
  private const ulong StartOffsetMask = (1UL << 54) - 1;
  private const ulong BlockCountMask = (1UL << 21) - 1;

  private readonly Stream _image;
  private readonly object _ioGate;
  private readonly uint _blockSize;
  private readonly long _length;
  private readonly long _localDataOffset;
  private readonly XfsExtent[] _extents;
  private bool _disposed;

  private XfsDirectReadOnlyFileHandle(
      Stream image,
      object ioGate,
      XfsDriverGeometry geometry,
      FilesystemNodeId nodeId,
      XfsDriverInode inode) {
    _image = image;
    _ioGate = ioGate;
    _blockSize = geometry.BlockSize;
    _length = inode.Size;
    NodeId = nodeId;

    var inodeOffset = ComputeInodeOffset(geometry, inode.Number);
    var forkOffset = geometry.Version >= 5 ? 176 : 100;
    if (inode.Format == 1) {
      if (inode.Size > geometry.InodeSize - forkOffset)
        throw new InvalidDataException($"XFS local inode {inode.Number} size does not fit its data fork.");
      _localDataOffset = checked(inodeOffset + forkOffset);
      _extents = [];
      return;
    }

    if (inode.Format != 2)
      throw new NotSupportedException($"XFS inode {inode.Number} uses data-fork format {inode.Format}; direct mounted reads support local/extents only.");

    _localDataOffset = -1;
    Span<byte> header = stackalloc byte[96];
    ReadExactlyAt(inodeOffset, header);
    var nextents = BinaryPrimitives.ReadUInt32BigEndian(header[76..80]);
    var capacity = (geometry.InodeSize - forkOffset) / 16;
    if (nextents > capacity)
      throw new NotSupportedException($"XFS inode {inode.Number} has {nextents} inline extents; extent-btree decoding is required.");

    var extentCount = checked((int)nextents);
    var bytes = new byte[checked(extentCount * 16)];
    if (bytes.Length != 0)
      ReadExactlyAt(checked(inodeOffset + forkOffset), bytes);

    _extents = new XfsExtent[extentCount];
    ulong previousEnd = 0;
    for (var i = 0; i < extentCount; ++i) {
      var hi = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(i * 16, 8));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(i * 16 + 8, 8));
      var startOffset = (hi >> 9) & StartOffsetMask;
      var startBlock = ((hi & 0x1FF) << 43) | (lo >> 21);
      var blockCount = lo & BlockCountMask;
      var unwritten = (hi & ExtentFlagMask) != 0;

      if (blockCount == 0)
        throw new InvalidDataException($"XFS inode {inode.Number} contains a zero-length extent.");
      if (i != 0 && startOffset < previousEnd)
        throw new InvalidDataException($"XFS inode {inode.Number} contains overlapping/out-of-order extents.");
      if (startBlock >= geometry.DataBlocks || blockCount > geometry.DataBlocks - startBlock)
        throw new InvalidDataException($"XFS inode {inode.Number} extent points outside the data device.");

      _extents[i] = new XfsExtent(startOffset, startBlock, blockCount, unwritten);
      previousEnd = checked(startOffset + blockCount);
    }
  }

  public FilesystemNodeId NodeId { get; }

  public long Length {
    get {
      ThrowIfDisposed();
      return _length;
    }
  }

  public static IFilesystemFileHandle Open(
      Stream image,
      object ioGate,
      XfsDriverGeometry geometry,
      FilesystemNodeId nodeId,
      XfsDriverInode inode) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(ioGate);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("XFS direct reads require a readable, seekable image.", nameof(image));
    if (inode.Kind != FilesystemNodeKind.RegularFile)
      throw new InvalidOperationException($"XFS inode {inode.Number} is not a regular file.");
    return new XfsDirectReadOnlyFileHandle(image, ioGate, geometry, nodeId, inode);
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.IsEmpty || offset >= _length) return 0;

    var remaining = checked((int)Math.Min(destination.Length, _length - offset));
    var written = 0;
    while (written < remaining) {
      var logicalByte = checked(offset + written);
      if (_localDataOffset >= 0) {
        var count = remaining - written;
        ReadExactlyAt(checked(_localDataOffset + logicalByte), destination.Slice(written, count));
        written += count;
        continue;
      }

      var logicalBlock = checked((ulong)(logicalByte / _blockSize));
      var offsetInBlock = checked((int)(logicalByte % _blockSize));
      var extentIndex = FindExtent(logicalBlock);
      if (extentIndex < 0) {
        var nextStart = NextExtentStart(logicalBlock);
        var holeEndByte = nextStart is null
          ? _length
          : Math.Min(_length, checked((long)(nextStart.Value * _blockSize)));
        var count = checked((int)Math.Min(remaining - written, holeEndByte - logicalByte));
        destination.Slice(written, count).Clear();
        written += count;
        continue;
      }

      ref readonly var extent = ref _extents[extentIndex];
      var blockDelta = logicalBlock - extent.StartOffset;
      var extentEndByte = checked((long)((extent.StartOffset + extent.BlockCount) * _blockSize));
      var countInExtent = checked((int)Math.Min(remaining - written, extentEndByte - logicalByte));
      if (extent.Unwritten) {
        destination.Slice(written, countInExtent).Clear();
        written += countInExtent;
        continue;
      }

      var physical = checked((long)((extent.StartBlock + blockDelta) * _blockSize) + offsetInBlock);
      ReadExactlyAt(physical, destination.Slice(written, countInExtent));
      written += countInExtent;
    }

    return written;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("XFS mounted file handles are read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("XFS mounted file handles are read-only.");

  public void Flush() {
    ThrowIfDisposed();
  }

  public void Dispose() => _disposed = true;

  private int FindExtent(ulong logicalBlock) {
    var lo = 0;
    var hi = _extents.Length - 1;
    while (lo <= hi) {
      var mid = lo + ((hi - lo) >> 1);
      ref readonly var extent = ref _extents[mid];
      if (logicalBlock < extent.StartOffset) {
        hi = mid - 1;
      } else if (logicalBlock >= extent.StartOffset + extent.BlockCount) {
        lo = mid + 1;
      } else {
        return mid;
      }
    }
    return -1;
  }

  private ulong? NextExtentStart(ulong logicalBlock) {
    foreach (var extent in _extents)
      if (extent.StartOffset > logicalBlock)
        return extent.StartOffset;
    return null;
  }

  private static long ComputeInodeOffset(XfsDriverGeometry geometry, ulong inodeNumber) {
    var inodesPerBlock = geometry.BlockSize / geometry.InodeSize;
    if (inodesPerBlock == 0 || (inodesPerBlock & (inodesPerBlock - 1)) != 0)
      throw new NotSupportedException("XFS inodes-per-block is not a power of two.");

    var inodePerBlockLog = 0;
    for (var value = inodesPerBlock; value > 1; value >>= 1) ++inodePerBlockLog;
    var aginoLog = checked(geometry.AgBlockLog + inodePerBlockLog);
    if (aginoLog >= 64)
      throw new InvalidDataException("XFS inode geometry overflows native inode encoding.");

    var agNumber = inodeNumber >> aginoLog;
    var agInodeMask = (1UL << aginoLog) - 1;
    var agInode = inodeNumber & agInodeMask;
    var block = agInode / inodesPerBlock;
    var index = agInode % inodesPerBlock;
    if (agNumber >= geometry.AgCount || block >= geometry.AgBlocks)
      throw new InvalidDataException($"XFS inode {inodeNumber} encodes an invalid allocation-group position.");

    return checked((long)((agNumber * geometry.AgBlocks + block) * geometry.BlockSize + index * geometry.InodeSize));
  }

  private void ReadExactlyAt(long offset, Span<byte> destination) {
    if (offset < 0 || offset > _image.Length - destination.Length)
      throw new InvalidDataException("XFS file data read lies outside the data device.");
    lock (_ioGate) {
      _image.Position = offset;
      _image.ReadExactly(destination);
    }
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private readonly record struct XfsExtent(
    ulong StartOffset,
    ulong StartBlock,
    ulong BlockCount,
    bool Unwritten);
}
