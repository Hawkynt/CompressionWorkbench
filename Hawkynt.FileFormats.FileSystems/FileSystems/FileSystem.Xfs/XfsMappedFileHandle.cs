#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Xfs;

/// <summary>
/// Direct positional reader for XFS local and inline-extent data forks.
/// Sparse holes and unwritten extents read as zeroes; btree-format forks remain
/// rejected by <see cref="XfsDriverGeometry"/> before this handle is created.
/// </summary>
internal sealed class XfsMappedFileHandle : IFilesystemFileHandle {
  private readonly Stream _image;
  private readonly object _gate;
  private readonly long _length;
  private readonly byte[]? _local;
  private readonly Extent[] _extents;
  private bool _disposed;

  private XfsMappedFileHandle(
      FilesystemNodeId nodeId,
      Stream image,
      object gate,
      long length,
      byte[]? local,
      Extent[] extents) {
    NodeId = nodeId;
    _image = image;
    _gate = gate;
    _length = Math.Max(0, length);
    _local = local;
    _extents = extents;
  }

  public FilesystemNodeId NodeId { get; }
  public long Length { get { ThrowIfDisposed(); return _length; } }

  internal static XfsMappedFileHandle Create(
      FilesystemNodeId nodeId,
      Stream image,
      object gate,
      XfsDriverGeometry geometry,
      XfsDriverInode inode) {
    if (inode.Kind != FilesystemNodeKind.RegularFile)
      throw new InvalidOperationException($"XFS inode {inode.Number} is not a regular file.");

    var forkOffset = geometry.Version >= 5 ? 176 : 100;
    var inodeOffset = InodeOffset(geometry, inode.Number);
    if (inode.Format == 1) {
      if (inode.Size > geometry.InodeSize - forkOffset)
        throw new InvalidDataException($"XFS local inode {inode.Number} data does not fit its data fork.");
      var bytes = new byte[checked((int)inode.Size)];
      if (bytes.Length != 0) {
        lock (gate) {
          image.Position = checked(inodeOffset + forkOffset);
          image.ReadExactly(bytes);
        }
      }
      return new XfsMappedFileHandle(nodeId, image, gate, inode.Size, bytes, []);
    }

    if (inode.Format != 2)
      throw new NotSupportedException($"XFS inode {inode.Number} data-fork format {inode.Format} is not a direct extent map.");

    Span<byte> header = stackalloc byte[96];
    lock (gate) {
      image.Position = inodeOffset;
      image.ReadExactly(header);
    }
    var nextents = BinaryPrimitives.ReadUInt32BigEndian(header[76..80]);
    var capacity = (geometry.InodeSize - forkOffset) / 16;
    if (nextents > capacity)
      throw new NotSupportedException($"XFS inode {inode.Number} uses an extent btree ({nextents} records exceed inline capacity {capacity}).");

    var extentBytes = new byte[checked((int)nextents * 16)];
    if (extentBytes.Length != 0) {
      lock (gate) {
        image.Position = checked(inodeOffset + forkOffset);
        image.ReadExactly(extentBytes);
      }
    }

    var extents = new Extent[nextents];
    ulong previousEnd = 0;
    for (var i = 0; i < extents.Length; ++i) {
      var hi = BinaryPrimitives.ReadUInt64BigEndian(extentBytes.AsSpan(i * 16, 8));
      var lo = BinaryPrimitives.ReadUInt64BigEndian(extentBytes.AsSpan(i * 16 + 8, 8));
      var unwritten = (hi & 0x8000_0000_0000_0000UL) != 0;
      var startOff = (hi >> 9) & 0x003F_FFFF_FFFF_FFFFUL;
      var startBlock = ((hi & 0x1FFUL) << 43) | (lo >> 21);
      var blockCount = lo & 0x1F_FFFFUL;
      if (blockCount == 0)
        throw new InvalidDataException($"XFS inode {inode.Number} contains a zero-length extent.");
      if (startOff < previousEnd)
        throw new InvalidDataException($"XFS inode {inode.Number} has overlapping or unsorted extent records.");
      if (startBlock >= geometry.DataBlocks || blockCount > geometry.DataBlocks - startBlock)
        throw new InvalidDataException($"XFS inode {inode.Number} extent points outside the data device.");

      var logical = checked((long)(startOff * geometry.BlockSize));
      var length = checked((long)(blockCount * geometry.BlockSize));
      var physical = checked((long)(startBlock * geometry.BlockSize));
      extents[i] = new Extent(logical, length, physical, unwritten);
      previousEnd = checked(startOff + blockCount);
    }

    return new XfsMappedFileHandle(nodeId, image, gate, inode.Size, null, extents);
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.IsEmpty || offset >= _length) return 0;

    var wanted = checked((int)Math.Min(destination.Length, _length - offset));
    var output = destination[..wanted];
    if (_local is not null) {
      _local.AsSpan(checked((int)offset), wanted).CopyTo(output);
      return wanted;
    }

    var written = 0;
    var logical = offset;
    var extentIndex = 0;
    while (extentIndex < _extents.Length && _extents[extentIndex].End <= logical) ++extentIndex;

    while (written < wanted) {
      if (extentIndex >= _extents.Length) {
        output[written..].Clear();
        break;
      }

      var extent = _extents[extentIndex];
      if (logical < extent.LogicalOffset) {
        var hole = checked((int)Math.Min(wanted - written, extent.LogicalOffset - logical));
        output.Slice(written, hole).Clear();
        logical += hole;
        written += hole;
        continue;
      }

      if (logical >= extent.End) {
        ++extentIndex;
        continue;
      }

      var within = logical - extent.LogicalOffset;
      var count = checked((int)Math.Min(wanted - written, extent.Length - within));
      if (extent.Unwritten) {
        output.Slice(written, count).Clear();
      } else {
        lock (_gate) {
          _image.Position = checked(extent.PhysicalOffset + within);
          _image.ReadExactly(output.Slice(written, count));
        }
      }
      logical += count;
      written += count;
      if (logical >= extent.End) ++extentIndex;
    }

    return wanted;
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("XFS mounted file handles are read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("XFS mounted file handles are read-only.");

  public void Flush() { ThrowIfDisposed(); }

  public void Dispose() => _disposed = true;

  private static long InodeOffset(XfsDriverGeometry geometry, ulong inodeNumber) {
    var inodesPerBlock = geometry.BlockSize / geometry.InodeSize;
    if (inodesPerBlock == 0 || (inodesPerBlock & (inodesPerBlock - 1)) != 0)
      throw new NotSupportedException("XFS inodes-per-block is not a power of two.");
    var inoPbLog = 0;
    for (var value = inodesPerBlock; value > 1; value >>= 1) ++inoPbLog;
    var aginoLog = checked(geometry.AgBlockLog + inoPbLog);
    if (aginoLog >= 64)
      throw new InvalidDataException("XFS inode geometry overflows native inode encoding.");
    var agNo = inodeNumber >> aginoLog;
    var agInoMask = (1UL << aginoLog) - 1;
    var agIno = inodeNumber & agInoMask;
    var block = agIno / inodesPerBlock;
    var index = agIno % inodesPerBlock;
    if (agNo >= geometry.AgCount || block >= geometry.AgBlocks)
      throw new InvalidDataException($"XFS inode {inodeNumber} encodes an invalid allocation-group position.");
    return checked((long)((agNo * geometry.AgBlocks + block) * geometry.BlockSize + index * geometry.InodeSize));
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

  private readonly record struct Extent(long LogicalOffset, long Length, long PhysicalOffset, bool Unwritten) {
    public long End => checked(LogicalOffset + Length);
  }
}
