#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.D81;

/// <summary>
/// Sector-addressable view of the data portion of a standard 80-track D81.
/// The optional 3200-byte sector-error table is intentionally outside the
/// exposed geometry and is preserved by ordinary block writes.
/// </summary>
public sealed class D81BlockDevice : IRandomAccessBlockDevice {
  public const int LogicalSectorSize = 256;
  public const int SectorCount = 3200;
  public const int DataLength = LogicalSectorSize * SectorCount;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private bool _disposed;

  public D81BlockDevice(Stream stream, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("D81 block access requires a readable, seekable stream.", nameof(stream));
    if (stream.Length < DataLength)
      throw new InvalidDataException($"D81 image is {stream.Length} bytes; at least {DataLength} bytes are required.");
    if (writable && !stream.CanWrite)
      throw new ArgumentException("Writable D81 block access requires a writable stream.", nameof(stream));
    _stream = stream;
    _leaveOpen = leaveOpen;
    CanWrite = writable;
  }

  public BlockDeviceGeometry Geometry { get; } = new(LogicalSectorSize, SectorCount, LogicalSectorSize, false);
  public bool CanWrite { get; }

  public int ReadBlocks(long firstBlock, Span<byte> destination) {
    ThrowIfDisposed();
    ValidateTransfer(firstBlock, destination.Length);
    var blockCount = destination.Length / LogicalSectorSize;
    lock (_gate) {
      _stream.Position = checked(firstBlock * LogicalSectorSize);
      _stream.ReadExactly(destination);
    }
    return blockCount;
  }

  public void WriteBlocks(long firstBlock, ReadOnlySpan<byte> source) {
    ThrowIfDisposed();
    EnsureWritable();
    ValidateTransfer(firstBlock, source.Length);
    lock (_gate) {
      _stream.Position = checked(firstBlock * LogicalSectorSize);
      _stream.Write(source);
    }
  }

  public void Trim(long firstBlock, long blockCount) {
    ThrowIfDisposed();
    EnsureWritable();
    if (firstBlock < 0 || blockCount < 0 || firstBlock > SectorCount - blockCount)
      throw new ArgumentOutOfRangeException(nameof(firstBlock));
    throw new NotSupportedException("D81 has no discard/trim primitive; filesystem allocation owns free-sector contents.");
  }

  public void Flush() {
    ThrowIfDisposed();
    lock (_gate) _stream.Flush();
  }

  public void Dispose() {
    if (_disposed) return;
    if (CanWrite) Flush();
    _disposed = true;
    if (!_leaveOpen) _stream.Dispose();
  }

  private static void ValidateTransfer(long firstBlock, int byteCount) {
    if (byteCount == 0) return;
    if (byteCount < 0 || byteCount % LogicalSectorSize != 0)
      throw new ArgumentException($"Block transfers must be a multiple of {LogicalSectorSize} bytes.", nameof(byteCount));
    var blocks = byteCount / LogicalSectorSize;
    if (firstBlock < 0 || firstBlock > SectorCount - blocks)
      throw new ArgumentOutOfRangeException(nameof(firstBlock));
  }

  private void EnsureWritable() {
    if (!CanWrite) throw new NotSupportedException("The D81 block device was opened read-only.");
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
