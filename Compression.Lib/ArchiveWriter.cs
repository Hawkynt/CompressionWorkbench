using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Lib;

/// <summary>
/// Ergonomic high-level facade over <see cref="IArchiveCreatable"/>: declare
/// each entry's length up-front, get a write-bounded <see cref="Stream"/> for
/// its bytes, and let the facade commit the assembled archive on dispose
/// via the atomic temp-file + rename protocol.
/// </summary>
/// <remarks>
/// <para>
/// Length-up-front is the locked design: every entry's size is declared at
/// <see cref="CreateFileEntry"/> time so two-pass writers (FAT, ext, future
/// ZIP store) can plan layout/geometry without buffering the world. Overrun
/// is caught at the moment of the offending <c>Write</c> (the
/// <see cref="BoundedWriteStream"/> throws); underrun is caught when the
/// entry stream is disposed (also via <see cref="BoundedWriteStream"/>).
/// Either way the facade refuses torn entries.
/// </para>
/// <para>
/// First-cut implementation: each <see cref="CreateFileEntry"/> returns a
/// buffered <see cref="BoundedWriteStream"/> backed by a
/// <see cref="MemoryStream"/>. The buffer is sized to the declared length
/// (still bounded — the per-entry memory cost equals the entry size, not
/// the archive size). On dispose the buffered bytes become the entry's
/// payload and are handed to the format's
/// <see cref="IArchiveCreatable.CreateFromStreams"/> via a
/// <see cref="StreamingArchiveInput"/> whose <c>OpenStream</c> factory just
/// returns a fresh <see cref="MemoryStream"/> over the buffered bytes.
/// </para>
/// <para>
/// TODO: per-format native <c>CreateFromStreams</c> overrides for the
/// archive formats (only FAT has one today). A native override can tee
/// <see cref="BoundedWriteStream"/> writes directly into a pre-allocated
/// region of the target stream, eliminating the per-entry buffer. Until
/// those land, the buffer-per-entry path is still bounded to
/// <c>contentLength</c> per entry — most entries are small, so the cost is
/// usually negligible.
/// </para>
/// </remarks>
public sealed class ArchiveWriter : IDisposable {

  private sealed record QueuedEntry(string Name, long Size, bool IsDirectory, byte[]? Data);

  private readonly string _outputPath;
  private readonly string _tempPath;
  private readonly IArchiveCreatable _creator;
  private readonly FormatCreateOptions _options;
  private readonly List<QueuedEntry> _queued = [];
  private BoundedWriteStream? _activeEntry;
  private string? _activeEntryName;
  private bool _disposed;
  private bool _failed;

  /// <summary>Format ID this writer targets (e.g. <c>Zip</c>, <c>Tar</c>).</summary>
  public string FormatId { get; }

  private ArchiveWriter(string outputPath, string tempPath, string formatId,
                        IArchiveCreatable creator, FormatCreateOptions options) {
    this._outputPath = outputPath;
    this._tempPath = tempPath;
    this._creator = creator;
    this._options = options;
    this.FormatId = formatId;
  }

  /// <summary>
  /// Creates a fresh archive at <paramref name="outputPath"/> in the format
  /// implied by the extension (or named explicitly via <paramref name="formatId"/>).
  /// </summary>
  /// <param name="outputPath">Final destination path; the writer stages to
  /// a sibling temp file and renames into place on <see cref="Dispose"/>.</param>
  /// <param name="formatId">Explicit format ID (e.g. <c>Zip</c>, <c>SevenZip</c>);
  /// when <c>null</c>, the format is detected from <paramref name="outputPath"/>'s
  /// extension.</param>
  /// <param name="options">Optional format-specific create options.</param>
  /// <exception cref="NotSupportedException">When the target format does
  /// not implement <see cref="IArchiveCreatable"/>.</exception>
  public static ArchiveWriter Create(string outputPath, string formatId,
                                     FormatCreateOptions? options = null) {
    ArgumentNullException.ThrowIfNull(outputPath);
    ArgumentNullException.ThrowIfNull(formatId);

    FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(formatId)
      ?? throw new NotSupportedException($"Unknown archive format: {formatId}");
    if (ops is not IArchiveCreatable creator)
      throw new NotSupportedException($"Format {formatId} does not support creation.");

    var tempPath = AtomicFileWriter.MakeTempPath(outputPath);
    return new ArchiveWriter(outputPath, tempPath, formatId, creator, options ?? new FormatCreateOptions());
  }

  /// <summary>
  /// Convenience overload: detects the format from <paramref name="outputPath"/>'s
  /// extension and creates a writer.
  /// </summary>
  public static ArchiveWriter Create(string outputPath, FormatCreateOptions? options = null) {
    ArgumentNullException.ThrowIfNull(outputPath);
    var format = FormatDetector.DetectByExtension(outputPath);
    if (format == FormatDetector.Format.Unknown)
      throw new NotSupportedException(
        $"Cannot determine format from extension: {Path.GetExtension(outputPath)}. " +
        "Use the overload that takes a formatId.");
    return Create(outputPath, format.ToString(), options);
  }

  /// <summary>
  /// Adds a directory entry. No content; just registers the path in the
  /// queued input list. Only useful for archive formats that record directory
  /// entries explicitly (ZIP, TAR, etc.); formats without directory entries
  /// silently drop the placeholder.
  /// </summary>
  public void MkDir(string path) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(path);
    this.CheckNoActiveEntry();

    var normalised = path.Replace('\\', '/').TrimEnd('/');
    this._queued.Add(new QueuedEntry(normalised, 0, IsDirectory: true, Data: null));
  }

  /// <summary>
  /// Opens a write stream for a new file entry. The caller MUST write exactly
  /// <paramref name="contentLength"/> bytes before disposing the returned
  /// stream: writing more throws <see cref="InvalidOperationException"/> at
  /// the offending <c>Write</c>; writing fewer throws on the entry stream's
  /// <c>Dispose</c>. The writer pre-allocates target space for the declared
  /// size and consumes the bytes through to the buffered payload.
  /// </summary>
  /// <param name="name">Archive-relative name (forward-slash separated).</param>
  /// <param name="contentLength">Exact size in bytes of the entry payload.</param>
  /// <param name="lastModified">Optional last-modified timestamp (currently
  /// not threaded into <see cref="StreamingArchiveInput"/> — declared so the
  /// signature stays stable when per-format last-modified threading lands).</param>
  /// <returns>A bounded write stream the caller fills with exactly
  /// <paramref name="contentLength"/> bytes.</returns>
  public Stream CreateFileEntry(string name, long contentLength,
                                DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(name);
    if (contentLength < 0)
      throw new ArgumentOutOfRangeException(nameof(contentLength), "contentLength must be >= 0.");
    this.CheckNoActiveEntry();

    var normalised = name.Replace('\\', '/');
    _ = lastModified; // reserved for per-format threading

    // The BoundedWriteStream enforces both overrun (Write past LogicalSize
    // throws) and underrun (Dispose with consumed<LogicalSize throws). The
    // commit callback fires only when the stream is disposed at exactly
    // LogicalSize bytes — that's the only path that queues the entry.
    var stream = BoundedWriteStream.CreateBuffered(contentLength, bytes => {
      this._queued.Add(new QueuedEntry(normalised, contentLength, IsDirectory: false, Data: bytes));
    });
    this._activeEntry = stream;
    this._activeEntryName = normalised;
    return new ActiveEntryStream(this, stream);
  }

  // Wraps the BoundedWriteStream so the writer's active-entry tracking is
  // cleared on dispose REGARDLESS of whether the stream completed successfully
  // or threw on underrun. The wrapper passes Write through verbatim — the
  // bound is enforced on the inner stream — and clears the writer's pointer
  // before re-throwing any underrun exception so a subsequent Dispose() of
  // the writer doesn't trip over the stale pointer.
  private sealed class ActiveEntryStream : Stream {
    private readonly ArchiveWriter _writer;
    private readonly BoundedWriteStream _inner;
    private bool _disposed;

    public ActiveEntryStream(ArchiveWriter writer, BoundedWriteStream inner) {
      this._writer = writer;
      this._inner = inner;
    }

    public override bool CanRead => false;
    public override bool CanWrite => !this._disposed && this._inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => this._inner.Length;
    public override long Position { get => this._inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => this._inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => this._inner.Write(buffer);
    public override void WriteByte(byte value) => this._inner.WriteByte(value);

    protected override void Dispose(bool disposing) {
      if (this._disposed) {
        base.Dispose(disposing);
        return;
      }
      this._disposed = true;
      try {
        if (disposing) this._inner.Dispose();
      } finally {
        // Always clear the writer's active-entry pointer, even when the inner
        // dispose threw on underrun. That way a subsequent writer.Dispose() can
        // proceed (it'll either commit the queued entries or, if Cancel was
        // called, drop the temp file cleanly).
        this._writer._activeEntry = null;
        this._writer._activeEntryName = null;
        base.Dispose(disposing);
      }
    }
  }

  /// <summary>
  /// Commits the archive on dispose: assembles the queued entry list, calls
  /// <see cref="IArchiveCreatable.CreateFromStreams"/> on the target stream,
  /// then atomically renames the temp file over the destination.
  /// </summary>
  /// <exception cref="InvalidOperationException">When a previously-returned
  /// <see cref="CreateFileEntry"/> stream is still active (the caller forgot
  /// to dispose it) or when the active stream has fewer bytes than declared.</exception>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;

    // If the caller never disposed the last CreateFileEntry stream, the
    // underrun check fires on its Dispose — surface that to the user before
    // we try to commit a torn archive.
    if (this._activeEntry != null) {
      var name = this._activeEntryName ?? "<unknown>";
      this._failed = true;
      try { this._activeEntry.Cancel(); this._activeEntry.Dispose(); } catch { /* swallow inner */ }
      AtomicFileWriter.TryDelete(this._tempPath);
      throw new InvalidOperationException(
        $"ArchiveWriter disposed while entry '{name}' was still open. " +
        "Dispose the stream returned by CreateFileEntry before disposing the writer.");
    }

    if (this._failed) {
      AtomicFileWriter.TryDelete(this._tempPath);
      return;
    }

    try {
      // Build the StreamingArchiveInput list from the queued entries. Each
      // file's OpenStream returns a fresh MemoryStream over the buffered
      // bytes — that's still a bounded source (the byte[] exists in memory),
      // and the target's CreateFromStreams default just CopyTo's it.
      var inputs = this._queued.Select(q => new StreamingArchiveInput(
        Name: q.Name,
        Size: q.Size,
        IsDirectory: q.IsDirectory,
        OpenStream: q.IsDirectory
          ? () => Stream.Null
          : () => new MemoryStream(q.Data!, writable: false))).ToList();

      // Atomic rename: stage to temp, flush, rename.
      AtomicFileWriter.WriteAtomic(this._outputPath,
        fs => this._creator.CreateFromStreams(fs, inputs, this._options));
    } catch {
      AtomicFileWriter.TryDelete(this._tempPath);
      throw;
    }
  }

  /// <summary>
  /// Marks the writer as failed so <see cref="Dispose"/> drops the temp file
  /// instead of trying to commit. Useful when a caller wants to bail mid-build
  /// without surfacing an unrelated commit error.
  /// </summary>
  public void Cancel() {
    this._failed = true;
    if (this._activeEntry != null) {
      this._activeEntry.Cancel();
    }
  }

  private void CheckNoActiveEntry() {
    if (this._activeEntry != null)
      throw new InvalidOperationException(
        $"Previous CreateFileEntry stream for '{this._activeEntryName}' is still open. " +
        "Dispose it before calling MkDir or CreateFileEntry again.");
  }
}
