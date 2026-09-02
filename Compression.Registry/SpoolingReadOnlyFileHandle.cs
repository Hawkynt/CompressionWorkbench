#pragma warning disable CS1591

namespace Compression.Registry;

/// <summary>
/// Transitional positional handle for native filesystem readers that can stream
/// a file correctly but do not yet expose a seekable block/extent map. Small
/// files stay in memory; large files are spooled to a delete-on-close temporary
/// file. This preserves driver-style positional reads without imposing a whole-
/// file RAM ceiling while the filesystem's direct block mapping is implemented.
/// </summary>
public sealed class SpoolingReadOnlyFileHandle : IFilesystemFileHandle {
  public const long DefaultMemoryThreshold = 8L * 1024 * 1024;

  private readonly Stream _spool;
  private readonly object _gate = new();
  private readonly long _length;
  private bool _disposed;

  private SpoolingReadOnlyFileHandle(FilesystemNodeId nodeId, Stream spool, long length) {
    NodeId = nodeId;
    _spool = spool;
    _length = length;
  }

  public static SpoolingReadOnlyFileHandle Create(
      FilesystemNodeId nodeId,
      long expectedLength,
      Action<Stream> writeContent,
      long memoryThreshold = DefaultMemoryThreshold) {
    ArgumentNullException.ThrowIfNull(writeContent);
    if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
    if (memoryThreshold < 0) throw new ArgumentOutOfRangeException(nameof(memoryThreshold));

    Stream spool;
    if (expectedLength <= memoryThreshold && expectedLength <= int.MaxValue) {
      spool = new MemoryStream(checked((int)expectedLength));
    } else {
      var path = Path.Combine(Path.GetTempPath(), "cwb_handle_" + Guid.NewGuid().ToString("N") + ".tmp");
      spool = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.Read | FileShare.Delete,
        64 * 1024,
        FileOptions.RandomAccess | FileOptions.DeleteOnClose);
    }

    try {
      writeContent(spool);
      if (spool.Length < expectedLength)
        throw new InvalidDataException(
          $"Filesystem reader produced {spool.Length:N0} bytes for a {expectedLength:N0}-byte file.");
      if (spool.Length > expectedLength) spool.SetLength(expectedLength);
      spool.Position = 0;
      return new SpoolingReadOnlyFileHandle(nodeId, spool, expectedLength);
    } catch {
      spool.Dispose();
      throw;
    }
  }

  public FilesystemNodeId NodeId { get; }
  public long Length {
    get {
      ThrowIfDisposed();
      return _length;
    }
  }

  public int Read(long offset, Span<byte> destination) {
    ThrowIfDisposed();
    if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
    if (destination.Length == 0 || offset >= _length) return 0;
    var count = checked((int)Math.Min(destination.Length, _length - offset));
    lock (_gate) {
      _spool.Position = offset;
      var total = 0;
      while (total < count) {
        var read = _spool.Read(destination.Slice(total, count - total));
        if (read == 0) break;
        total += read;
      }
      if (total != count)
        throw new EndOfStreamException("The filesystem spool ended before the advertised logical file length.");
      return total;
    }
  }

  public void Write(long offset, ReadOnlySpan<byte> source)
    => throw new NotSupportedException("The spooled filesystem handle is read-only.");

  public void SetLength(long length)
    => throw new NotSupportedException("The spooled filesystem handle is read-only.");

  public void Flush() => ThrowIfDisposed();

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    _spool.Dispose();
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
