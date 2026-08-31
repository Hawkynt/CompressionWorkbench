#pragma warning disable CS1591

namespace Compression.Registry;

/// <summary>
/// Bounded view of a contiguous range of another random-access block device.
/// The wrapper never changes the parent's geometry and never copies partition
/// contents; block requests are translated by adding <paramref name="firstBlock"/>.
/// </summary>
public sealed class PartitionBlockDevice : IRandomAccessBlockDevice {
  private readonly IRandomAccessBlockDevice _inner;
  private readonly long _firstBlock;
  private readonly bool _leaveOpen;
  private bool _disposed;

  public PartitionBlockDevice(
      IRandomAccessBlockDevice inner,
      long firstBlock,
      long blockCount,
      bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(inner);
    if (firstBlock < 0) throw new ArgumentOutOfRangeException(nameof(firstBlock));
    if (blockCount < 0) throw new ArgumentOutOfRangeException(nameof(blockCount));
    if (firstBlock > inner.Geometry.BlockCount || blockCount > inner.Geometry.BlockCount - firstBlock)
      throw new ArgumentOutOfRangeException(nameof(blockCount), "Partition range extends beyond the parent block device.");

    _inner = inner;
    _firstBlock = firstBlock;
    _leaveOpen = leaveOpen;
    Geometry = inner.Geometry with { BlockCount = blockCount };
  }

  public BlockDeviceGeometry Geometry { get; }
  public bool CanWrite => !_disposed && _inner.CanWrite;

  public int ReadBlocks(long firstBlock, Span<byte> destination) {
    ThrowIfDisposed();
    ValidateBuffer(firstBlock, destination.Length, nameof(destination));
    return destination.Length == 0
      ? 0
      : _inner.ReadBlocks(checked(_firstBlock + firstBlock), destination);
  }

  public void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source) {
    ThrowIfDisposed();
    if (!CanWrite) throw new NotSupportedException("The partition block device is read-only.");
    ValidateBuffer(firstBlock, source.Length, nameof(source));
    if (source.Length == 0) return;
    _inner.WriteBlocks(checked(_firstBlock + firstBlock), source);
  }

  public void Trim(long firstBlock, long blockCount) {
    ThrowIfDisposed();
    if (!CanWrite) throw new NotSupportedException("The partition block device is read-only.");
    ValidateRange(firstBlock, blockCount);
    _inner.Trim(checked(_firstBlock + firstBlock), blockCount);
  }

  public void Flush() {
    ThrowIfDisposed();
    _inner.Flush();
  }

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    if (!_leaveOpen)
      _inner.Dispose();
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
      throw new ArgumentOutOfRangeException(nameof(firstBlock), "Block range extends beyond the partition.");
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
