#pragma warning disable CS1591

namespace Compression.Registry;

/// <summary>
/// Compatibility alias for the original block-device provider name. New code
/// uses <see cref="IRandomAccessBlockDeviceProvider"/> so containers, decoded
/// track media and raw images expose exactly one logical-block abstraction.
/// </summary>
[Obsolete("Use IRandomAccessBlockDeviceProvider; both names represent the same logical-block boundary.")]
public interface IBlockDeviceProvider : IRandomAccessBlockDeviceProvider { }

/// <summary>
/// Optional filesystem-core capability for implementations whose native parser
/// already works directly on a block device. This is the long-term driver core:
/// the same filesystem implementation can mount raw disks, virtual disks,
/// forensic images, or decoded track media without container-specific code.
/// </summary>
public interface IBlockDeviceFilesystemDriverProvider {
  FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device);
  IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options);
}

/// <summary>
/// Fixed-size random-access block device over an ordinary seekable stream. This
/// is the bridge for raw filesystem images while parsers migrate away from
/// direct Stream.Position access.
/// </summary>
public sealed class StreamBlockDevice : IRandomAccessBlockDevice {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private bool _disposed;

  public StreamBlockDevice(
      Stream stream,
      int logicalBlockSize,
      bool writable,
      bool leaveOpen = true,
      int? physicalBlockSize = null) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("A stream block device requires a readable, seekable stream.", nameof(stream));
    if (logicalBlockSize <= 0 || (logicalBlockSize & (logicalBlockSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(logicalBlockSize), "Logical block size must be a positive power of two.");
    if (stream.Length % logicalBlockSize != 0)
      throw new InvalidDataException(
        $"Stream length {stream.Length:N0} is not an exact multiple of logical block size {logicalBlockSize:N0}.");
    if (writable && !stream.CanWrite)
      throw new ArgumentException("Writable block-device access requires a writable stream.", nameof(stream));

    _stream = stream;
    _leaveOpen = leaveOpen;
    CanWrite = writable;
    Geometry = new BlockDeviceGeometry(
      logicalBlockSize,
      stream.Length / logicalBlockSize,
      physicalBlockSize.GetValueOrDefault(logicalBlockSize),
      SupportsTrim: false);
  }

  public BlockDeviceGeometry Geometry { get; }
  public bool CanWrite { get; }

  public int ReadBlocks(long firstBlock, Span<byte> destination) {
    ThrowIfDisposed();
    ValidateBuffer(firstBlock, destination.Length, nameof(destination));
    if (destination.Length == 0) return 0;
    lock (_gate) {
      _stream.Position = checked(firstBlock * Geometry.LogicalBlockSize);
      _stream.ReadExactly(destination);
    }
    return destination.Length / Geometry.LogicalBlockSize;
  }

  public void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source) {
    ThrowIfDisposed();
    if (!CanWrite) throw new NotSupportedException("The stream block device was opened read-only.");
    ValidateBuffer(firstBlock, source.Length, nameof(source));
    if (source.Length == 0) return;
    lock (_gate) {
      _stream.Position = checked(firstBlock * Geometry.LogicalBlockSize);
      _stream.Write(source);
    }
  }

  public void Trim(long firstBlock, long blockCount) {
    ThrowIfDisposed();
    if (!CanWrite) throw new NotSupportedException("The stream block device was opened read-only.");
    ValidateRange(firstBlock, blockCount);
    throw new NotSupportedException("An ordinary stream has no portable deallocate/TRIM primitive.");
  }

  public void Flush() {
    ThrowIfDisposed();
    _stream.Flush();
  }

  public void Dispose() {
    if (_disposed) return;
    if (CanWrite) _stream.Flush();
    _disposed = true;
    if (!_leaveOpen) _stream.Dispose();
  }

  private void ValidateBuffer(long firstBlock, int byteCount, string parameterName) {
    if (byteCount < 0 || byteCount % Geometry.LogicalBlockSize != 0)
      throw new ArgumentException("Block I/O buffers must contain a whole number of logical blocks.", parameterName);
    ValidateRange(firstBlock, byteCount / Geometry.LogicalBlockSize);
  }

  private void ValidateRange(long firstBlock, long blockCount) {
    if (firstBlock < 0) throw new ArgumentOutOfRangeException(nameof(firstBlock));
    if (blockCount < 0) throw new ArgumentOutOfRangeException(nameof(blockCount));
    if (firstBlock > Geometry.BlockCount || blockCount > Geometry.BlockCount - firstBlock)
      throw new ArgumentOutOfRangeException(nameof(firstBlock), "Block range extends beyond the device.");
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// Seekable byte-stream view over a block device. Legacy filesystem parsers can
/// therefore run on VHD/QCOW2/GCR-backed logical disks before they are rewritten
/// to issue block requests directly. Unaligned writes use read-modify-write of
/// only the touched edge blocks; unrelated blocks are never rewritten.
/// </summary>
public sealed class BlockDeviceStream : Stream {
  private readonly IRandomAccessBlockDevice _device;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private long _position;
  private bool _disposed;

  public BlockDeviceStream(IRandomAccessBlockDevice device, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(device);
    _device = device;
    _leaveOpen = leaveOpen;
  }

  public override bool CanRead => !_disposed;
  public override bool CanSeek => !_disposed;
  public override bool CanWrite => !_disposed && _device.CanWrite;
  public override long Length {
    get {
      ThrowIfDisposed();
      return _device.Geometry.Length;
    }
  }

  public override long Position {
    get {
      ThrowIfDisposed();
      return _position;
    }
    set {
      ThrowIfDisposed();
      if (value < 0 || value > Length) throw new ArgumentOutOfRangeException(nameof(value));
      _position = value;
    }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    return Read(buffer.AsSpan(offset, count));
  }

  public override int Read(Span<byte> buffer) {
    ThrowIfDisposed();
    if (buffer.Length == 0 || _position >= Length) return 0;
    var count = checked((int)Math.Min(buffer.Length, Length - _position));
    ReadAt(_position, buffer[..count]);
    _position += count;
    return count;
  }

  public override int ReadByte() {
    Span<byte> one = stackalloc byte[1];
    return Read(one) == 0 ? -1 : one[0];
  }

  public override void Write(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    Write(buffer.AsSpan(offset, count));
  }

  public override void Write(ReadOnlySpan<byte> buffer) {
    ThrowIfDisposed();
    if (!CanWrite) throw new NotSupportedException("The block-device stream is read-only.");
    if (buffer.Length == 0) return;
    if ((long)buffer.Length > Length - _position)
      throw new IOException("Block-device streams have fixed length and cannot be extended.");
    WriteAt(_position, buffer);
    _position += buffer.Length;
  }

  public override void WriteByte(byte value) {
    Span<byte> one = stackalloc byte[1] { value };
    Write(one);
  }

  public override long Seek(long offset, SeekOrigin origin) {
    ThrowIfDisposed();
    long target;
    try {
      target = origin switch {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => checked(_position + offset),
        SeekOrigin.End => checked(Length + offset),
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
      };
    } catch (OverflowException) {
      throw new IOException("Seek target overflows the block-device address space.");
    }
    if (target < 0 || target > Length) throw new IOException("Seek target lies outside the fixed block device.");
    return _position = target;
  }

  public override void SetLength(long value)
    => throw new NotSupportedException("Block-device streams have fixed geometry.");

  public override void Flush() {
    ThrowIfDisposed();
    _device.Flush();
  }

  protected override void Dispose(bool disposing) {
    if (!_disposed && disposing) {
      if (_device.CanWrite) _device.Flush();
      if (!_leaveOpen) _device.Dispose();
    }
    _disposed = true;
    base.Dispose(disposing);
  }

  private void ReadAt(long offset, Span<byte> destination) {
    var blockSize = _device.Geometry.LogicalBlockSize;
    lock (_gate) {
      var cursor = 0;
      var logicalOffset = offset;
      while (cursor < destination.Length) {
        var block = logicalOffset / blockSize;
        var within = checked((int)(logicalOffset % blockSize));
        var take = Math.Min(destination.Length - cursor, blockSize - within);
        if (within == 0 && take == blockSize) {
          _device.ReadBlocks(block, destination.Slice(cursor, blockSize));
        } else {
          var scratch = new byte[blockSize];
          _device.ReadBlocks(block, scratch);
          scratch.AsSpan(within, take).CopyTo(destination.Slice(cursor, take));
        }
        cursor += take;
        logicalOffset += take;
      }
    }
  }

  private void WriteAt(long offset, ReadOnlySpan<byte> source) {
    var blockSize = _device.Geometry.LogicalBlockSize;
    lock (_gate) {
      var cursor = 0;
      var logicalOffset = offset;
      while (cursor < source.Length) {
        var block = logicalOffset / blockSize;
        var within = checked((int)(logicalOffset % blockSize));
        var take = Math.Min(source.Length - cursor, blockSize - within);
        if (within == 0 && take == blockSize) {
          _device.WriteBlocks(block, source.Slice(cursor, blockSize));
        } else {
          var scratch = new byte[blockSize];
          _device.ReadBlocks(block, scratch);
          source.Slice(cursor, take).CopyTo(scratch.AsSpan(within, take));
          _device.WriteBlocks(block, scratch);
        }
        cursor += take;
        logicalOffset += take;
      }
    }
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
