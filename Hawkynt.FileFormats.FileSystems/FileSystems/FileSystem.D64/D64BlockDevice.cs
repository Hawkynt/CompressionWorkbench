#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.D64;

/// <summary>
/// Sector-addressable view of the data portion of a standard 35-track D64.
/// The optional per-sector error table in 175531-byte images is deliberately
/// outside this device geometry and is therefore preserved by ordinary writes.
/// </summary>
public sealed class D64BlockDevice : IRandomAccessBlockDevice {
  public const int LogicalSectorSize = 256;
  public const int SectorCount = 683;
  public const int DataLength = LogicalSectorSize * SectorCount;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly object _gate = new();
  private bool _disposed;

  public D64BlockDevice(Stream stream, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("D64 block access requires a readable, seekable stream.", nameof(stream));
    if (stream.Length < DataLength)
      throw new InvalidDataException($"D64 image is {stream.Length} bytes; at least {DataLength} bytes are required.");
    if (writable && !stream.CanWrite)
      throw new ArgumentException("Writable D64 block access requires a writable stream.", nameof(stream));
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
    throw new NotSupportedException("D64 has no discard/trim primitive; filesystem allocation owns free-sector contents.");
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
    if (!CanWrite) throw new NotSupportedException("The D64 block device was opened read-only.");
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
