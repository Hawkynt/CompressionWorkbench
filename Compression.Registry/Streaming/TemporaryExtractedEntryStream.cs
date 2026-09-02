namespace Compression.Registry.Streaming;

/// <summary>
/// Seekable read-only stream over an entry extracted into an isolated temporary
/// directory. The directory tree is removed when the stream is disposed.
///
/// This is the default bridge for archive/filesystem descriptors that have not
/// yet implemented a native <c>OpenEntry</c>. Unlike the historical byte-array
/// fallback it has no Array.MaxLength / whole-file-RAM ceiling and therefore is
/// safe to use beneath filesystem-driver positional spooling.
/// </summary>
internal sealed class TemporaryExtractedEntryStream : Stream {
  private readonly FileStream _inner;
  private readonly string _temporaryDirectory;
  private bool _disposed;

  private TemporaryExtractedEntryStream(FileStream inner, string temporaryDirectory) {
    _inner = inner;
    _temporaryDirectory = temporaryDirectory;
  }

  public static TemporaryExtractedEntryStream Open(
      IArchiveFormatOperations operations,
      Stream archive,
      string entryName,
      string? password) {
    ArgumentNullException.ThrowIfNull(operations);
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_entry_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try {
      if (archive.CanSeek) archive.Position = 0;
      operations.Extract(archive, tempDir, password, [entryName]);

      var wanted = Normalize(entryName);
      string? path = null;
      foreach (var candidate in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)) {
        var relative = Normalize(Path.GetRelativePath(tempDir, candidate));
        if (!relative.Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(relative).Equals(Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase))
          continue;
        if (path != null && !relative.Equals(wanted, StringComparison.OrdinalIgnoreCase))
          throw new InvalidDataException($"Extraction of '{entryName}' produced multiple ambiguous files.");
        path = candidate;
        if (relative.Equals(wanted, StringComparison.OrdinalIgnoreCase)) break;
      }

      if (path == null)
        throw new FileNotFoundException(
          $"Archive extraction did not materialize requested entry '{entryName}'.", entryName);

      var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        64 * 1024,
        FileOptions.RandomAccess);
      return new TemporaryExtractedEntryStream(stream, tempDir);
    } catch {
      TryDelete(tempDir);
      throw;
    }
  }

  public override bool CanRead => !_disposed && _inner.CanRead;
  public override bool CanSeek => !_disposed && _inner.CanSeek;
  public override bool CanWrite => false;
  public override long Length { get { ThrowIfDisposed(); return _inner.Length; } }
  public override long Position {
    get { ThrowIfDisposed(); return _inner.Position; }
    set { ThrowIfDisposed(); _inner.Position = value; }
  }

  public override int Read(byte[] buffer, int offset, int count) {
    ThrowIfDisposed();
    return _inner.Read(buffer, offset, count);
  }

  public override int Read(Span<byte> buffer) {
    ThrowIfDisposed();
    return _inner.Read(buffer);
  }

  public override int ReadByte() {
    ThrowIfDisposed();
    return _inner.ReadByte();
  }

  public override long Seek(long offset, SeekOrigin origin) {
    ThrowIfDisposed();
    return _inner.Seek(offset, origin);
  }

  public override void Flush() => ThrowIfDisposed();
  public override void SetLength(long value) => throw new NotSupportedException("Temporary extracted entry streams are read-only.");
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Temporary extracted entry streams are read-only.");
  public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("Temporary extracted entry streams are read-only.");

  protected override void Dispose(bool disposing) {
    if (_disposed) {
      base.Dispose(disposing);
      return;
    }
    _disposed = true;
    if (disposing) {
      _inner.Dispose();
      TryDelete(_temporaryDirectory);
    }
    base.Dispose(disposing);
  }

  private static string Normalize(string value)
    => value.Replace('\\', '/').TrimStart('/');

  private static void TryDelete(string directory) {
    try {
      if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    } catch {
      // Best-effort temporary cleanup. The OS/temp cleaner may remove an entry
      // held by an external scanner after our file handle has already closed.
    }
  }

  private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
