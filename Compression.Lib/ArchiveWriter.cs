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

  // A queued entry can be either:
  //   - byte[] backed (up-front path, BoundedWriteStream.CreateBuffered)
  //   - Func<Stream> backed (deferred path, DeferredLengthWriteStream)
  // Both flavours collapse into a StreamingArchiveInput at commit time.
  private sealed record QueuedEntry(
    string Name, long Size, bool IsDirectory,
    byte[]? Data,
    Func<Stream>? OpenStreamFactory);

  private readonly string _outputPath;
  private readonly string _tempPath;
  private readonly IArchiveCreatable _creator;
  private readonly FormatCreateOptions _options;
  private readonly List<QueuedEntry> _queued = [];
  // Tracks the currently-open per-entry stream — only one entry can be open
  // at a time. The base type Stream covers both BoundedWriteStream (up-front)
  // and DeferredLengthWriteStream (deferred). The wrapper interfaces below
  // call Cancel via reflection-free direct casts on the concrete types.
  private Stream? _activeEntry;
  private string? _activeEntryName;
  private bool _disposed;
  private bool _failed;

  /// <summary>For tests / diagnostics: total count of
  /// <see cref="DeferredLengthWriteStream"/> instances handed out by this
  /// writer since construction. The auto-pick path increments this only
  /// when a length-up-front read was impossible.</summary>
  public int DeferredEntriesIssued { get; private set; }

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
    // Capability-based resolution: the path names a file that does not exist yet, so a shared
    // extension cannot be settled by content. Of the claimants, the one that can create wins.
    var format = FormatDetector.DetectByExtensionForCreate(outputPath);
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
    this._queued.Add(new QueuedEntry(normalised, 0, IsDirectory: true, Data: null, OpenStreamFactory: null));
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
      this._queued.Add(new QueuedEntry(normalised, contentLength, IsDirectory: false, Data: bytes, OpenStreamFactory: null));
    });
    this._activeEntry = stream;
    this._activeEntryName = normalised;
    return new ActiveEntryStream(this, stream);
  }

  /// <summary>
  /// Opens a deferred-length write stream for a new file entry. Use this
  /// only when the caller genuinely cannot know the entry size up-front
  /// (e.g. piping a non-seekable producer stream into the archive); prefer
  /// <see cref="CreateFileEntry(string, long, DateTime?)"/> everywhere else
  /// so two-pass writers can plan layout without buffering.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The returned stream buffers writes in memory up to
  /// <see cref="DeferredLengthWriteStream.DefaultSpillThresholdBytes"/>;
  /// past that, it spills to a temp file. On dispose, the entry is queued
  /// for commit with its actual byte count — so the target format's
  /// <see cref="IArchiveCreatable.CreateFromStreams"/> sees a normal
  /// streaming input with a known size, even though the size wasn't known
  /// when the caller started writing.
  /// </para>
  /// </remarks>
  /// <param name="name">Archive-relative name (forward-slash separated).</param>
  /// <param name="lastModified">Optional last-modified timestamp (reserved;
  /// see <see cref="CreateFileEntry(string, long, DateTime?)"/>).</param>
  public Stream CreateFileEntry(string name, DateTime? lastModified = null) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    ArgumentNullException.ThrowIfNull(name);
    this.CheckNoActiveEntry();

    var normalised = name.Replace('\\', '/');
    _ = lastModified; // reserved for per-format threading

    // The DeferredLengthWriteStream fires its commit callback with the
    // final byte count + a Func<Stream> that re-opens the buffered
    // content. We queue that as a streaming entry (Size already known by
    // commit time); no overrun/underrun checks are needed because the
    // size is whatever the caller produced.
    DeferredLengthWriteStream stream = null!;
    stream = new DeferredLengthWriteStream((size, openContent) => {
      this._queued.Add(new QueuedEntry(
        Name: normalised,
        Size: size,
        IsDirectory: false,
        Data: null,
        OpenStreamFactory: openContent));
    });
    this._activeEntry = stream;
    this._activeEntryName = normalised;
    ++this.DeferredEntriesIssued;
    return new ActiveEntryStream(this, stream);
  }

  /// <summary>
  /// Auto-picks the zero-buffer (length-up-front) or deferred-length path
  /// based on whether the source stream's remaining length can be queried.
  /// </summary>
  /// <remarks>
  /// Decision: when <paramref name="source"/> reports <c>CanSeek == true</c>
  /// and <c>Length</c> succeeds, the remaining bytes (<c>Length - Position</c>)
  /// become the declared size and the entry takes the zero-buffer
  /// <see cref="BoundedWriteStream"/> path. Otherwise (CanSeek false, or
  /// Length threw <see cref="NotSupportedException"/> despite CanSeek being
  /// true — this DOES happen for some custom Stream subclasses) the deferred
  /// path is used. The latter is also why we wrap the <c>.Length</c> read
  /// in a try/catch even on seekable streams.
  /// </remarks>
  public void AddEntry(string archivePath, Stream source, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(archivePath);
    ArgumentNullException.ThrowIfNull(source);

    long? length = null;
    if (source.CanSeek) {
      try {
        length = source.Length - source.Position;
      } catch (NotSupportedException) {
        // Rare but real: some Stream subclasses claim CanSeek=true and
        // still throw on .Length. Treat exactly as non-seekable.
        length = null;
      }
    }

    if (length.HasValue) {
      using var dst = this.CreateFileEntry(archivePath, length.Value, lastModified);
      source.CopyTo(dst);
    } else {
      using var dst = this.CreateFileEntry(archivePath, lastModified);
      source.CopyTo(dst);
    }
  }

  /// <summary>
  /// Convenience overload that adds a file from disk. Length is always
  /// known so the zero-buffer path is taken unconditionally.
  /// </summary>
  public void AddEntry(string archivePath, FileInfo source, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(archivePath);
    ArgumentNullException.ThrowIfNull(source);
    if (!source.Exists)
      throw new FileNotFoundException("Source file not found.", source.FullName);

    using var fs = source.OpenRead();
    using var dst = this.CreateFileEntry(archivePath, source.Length, lastModified ?? source.LastWriteTimeUtc);
    fs.CopyTo(dst);
  }

  /// <summary>
  /// Convenience overload for a byte[] payload. Length is known so the
  /// zero-buffer path is taken unconditionally.
  /// </summary>
  public void AddEntry(string archivePath, byte[] source, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(archivePath);
    ArgumentNullException.ThrowIfNull(source);
    using var dst = this.CreateFileEntry(archivePath, source.LongLength, lastModified);
    dst.Write(source, 0, source.Length);
  }

  /// <summary>
  /// Convenience overload for a <see cref="ReadOnlySpan{T}"/> payload.
  /// Length is known so the zero-buffer path is taken unconditionally.
  /// </summary>
  public void AddEntry(string archivePath, ReadOnlySpan<byte> source, DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(archivePath);
    using var dst = this.CreateFileEntry(archivePath, source.Length, lastModified);
    dst.Write(source);
  }

  // Wraps the underlying per-entry write stream (BoundedWriteStream for the
  // up-front path, DeferredLengthWriteStream for the deferred path) so the
  // writer's active-entry tracking is cleared on dispose REGARDLESS of
  // whether the stream completed successfully or threw on underrun. Write
  // calls pass through verbatim — the bound (for up-front) or buffer-spill
  // (for deferred) is enforced on the inner stream — and the writer's
  // pointer is cleared before re-throwing any inner exception so a
  // subsequent Dispose() of the writer doesn't trip over a stale pointer.
  private sealed class ActiveEntryStream : Stream {
    private readonly ArchiveWriter _writer;
    private readonly Stream _inner;
    private bool _disposed;

    public ActiveEntryStream(ArchiveWriter writer, Stream inner) {
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
      try { CancelActiveEntry(this._activeEntry); this._activeEntry.Dispose(); } catch { /* swallow inner */ }
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
      // Build the StreamingArchiveInput list from the queued entries. For
      // up-front entries (Data != null) the OpenStream factory returns a
      // fresh MemoryStream over the buffered bytes; for deferred entries
      // (OpenStreamFactory != null) it returns whatever the deferred
      // stream's commit callback handed us — typically a fresh seekable
      // view that owns the temp file's lifetime.
      var inputs = this._queued.Select(q => new StreamingArchiveInput(
        Name: q.Name,
        Size: q.Size,
        IsDirectory: q.IsDirectory,
        OpenStream: q.IsDirectory
          ? () => Stream.Null
          : q.OpenStreamFactory ?? (() => new MemoryStream(q.Data!, writable: false)))).ToList();

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
    if (this._activeEntry != null)
      CancelActiveEntry(this._activeEntry);
  }

  // Dispatches Cancel() to the appropriate concrete type — both the up-front
  // and deferred per-entry streams expose a Cancel method but they don't
  // share an interface, so we pattern-match.
  private static void CancelActiveEntry(Stream entry) {
    switch (entry) {
      case BoundedWriteStream b: b.Cancel(); break;
      case DeferredLengthWriteStream d: d.Cancel(); break;
    }
  }

  private void CheckNoActiveEntry() {
    if (this._activeEntry != null)
      throw new InvalidOperationException(
        $"Previous CreateFileEntry stream for '{this._activeEntryName}' is still open. " +
        "Dispose it before calling MkDir or CreateFileEntry again.");
  }
}
