namespace Compression.Registry.Streaming;

/// <summary>
/// A write-only <see cref="Stream"/> for sources whose length is NOT known
/// up-front. Buffers writes into a <see cref="MemoryStream"/> until they
/// cross <see cref="SpillThresholdBytes"/>, then spills to a temp file and
/// switches further writes to it. On <see cref="Dispose"/>, fires a commit
/// callback with the accumulated byte count plus a <see cref="Func{TResult}"/>
/// that re-opens the buffered content for reading; the temp file (if any)
/// is best-effort deleted when the consumer disposes the re-opened stream.
/// </summary>
/// <remarks>
/// <para>
/// This is the escape hatch alongside <see cref="BoundedWriteStream"/>:
/// length-up-front is preferred everywhere it works because it lets two-pass
/// writers plan layout/geometry without buffering. When a caller genuinely
/// can't know the size (e.g. piping the output of a non-seekable producer
/// stream into an archive entry), <see cref="DeferredLengthWriteStream"/>
/// absorbs the write and surfaces the final size + a re-openable view of
/// the bytes to the archive writer at commit time.
/// </para>
/// <para>
/// Spill protocol: while the buffered byte count stays at or below
/// <see cref="SpillThresholdBytes"/>, writes accumulate in an in-memory
/// <see cref="MemoryStream"/>. When the next write would push the total
/// past the threshold, a temp file is created in the configured spill
/// directory, the already-buffered bytes are flushed to it, and the
/// underlying sink switches to the temp file. The spilled file is named
/// <c>cwb_dlw_*</c> so debugging tooling can identify leaks.
/// </para>
/// <para>
/// Lifecycle: the temp file is owned by the <c>Func&lt;Stream&gt;</c>
/// passed to the commit callback. Disposing the returned stream deletes
/// the temp file. If <see cref="Cancel"/> is invoked the temp file is
/// dropped immediately. As a last resort the finalizer best-effort deletes
/// any temp file the consumer didn't get to.
/// </para>
/// </remarks>
public sealed class DeferredLengthWriteStream : Stream {

  /// <summary>Default spill threshold = 256 MiB
  /// (mirrors <c>InMemoryProcessing.ThresholdBytes / 8</c>: at 2 GiB ceiling
  /// that's 256 MiB per stream so several can coexist before exhausting RAM).
  /// Compression.Registry cannot reference Compression.Lib, so this constant
  /// is duplicated here — keep the values in sync if the lib-side ceiling
  /// changes.</summary>
  public const long DefaultSpillThresholdBytes = 256L * 1024 * 1024;

  private readonly Action<long, Func<Stream>>? _onClose;
  private readonly long _spillThresholdBytes;
  private readonly string _spillDirectory;

  private MemoryStream? _buffer;
  private FileStream? _spillStream;
  private string? _spillPath;
  private long _bytesWritten;
  private bool _disposed;
  private bool _cancelled;

  /// <summary>Number of bytes written so far through this stream (in-memory
  /// + spilled).</summary>
  public long BytesWritten => this._bytesWritten;

  /// <summary>The configured spill threshold. Writes that would push the
  /// total count above this value trigger a switch to a temp file.</summary>
  public long SpillThresholdBytes => this._spillThresholdBytes;

  /// <summary>The path of the spill file, or <c>null</c> if the stream has
  /// not yet spilled. Exposed for diagnostics / tests.</summary>
  public string? SpillPath => this._spillPath;

  /// <summary>True if the stream has spilled to disk; false if still
  /// entirely in-memory.</summary>
  public bool HasSpilled => this._spillStream != null;

  /// <summary>
  /// Creates a new deferred-length write stream.
  /// </summary>
  /// <param name="onClose">Callback invoked on <see cref="Dispose"/> with the
  /// final byte count and a factory that re-opens the buffered content as a
  /// readable <see cref="Stream"/>. Disposing the returned stream deletes
  /// the temp file (if any). Not invoked when <see cref="Cancel"/> was
  /// called first.</param>
  /// <param name="spillThresholdBytes">Threshold past which the buffer
  /// spills to a temp file. Pass <c>-1</c> for the default
  /// (<see cref="DefaultSpillThresholdBytes"/>).</param>
  /// <param name="spillDirectory">Directory to place the temp file in;
  /// defaults to <see cref="Path.GetTempPath"/>.</param>
  public DeferredLengthWriteStream(
      Action<long, Func<Stream>> onClose,
      long spillThresholdBytes = -1,
      string? spillDirectory = null) {
    ArgumentNullException.ThrowIfNull(onClose);
    this._onClose = onClose;
    this._spillThresholdBytes = spillThresholdBytes < 0
      ? DefaultSpillThresholdBytes
      : spillThresholdBytes;
    this._spillDirectory = spillDirectory ?? Path.GetTempPath();
    this._buffer = new MemoryStream();
  }

  /// <summary>
  /// Cancels the commit callback: the buffered content is discarded and
  /// the spill file (if any) is deleted on dispose. The callback registered
  /// via the constructor will NOT fire.
  /// </summary>
  public void Cancel() {
    this._cancelled = true;
  }

  public override bool CanRead => false;
  public override bool CanWrite => !this._disposed;
  public override bool CanSeek => false;
  public override long Length => this._bytesWritten;
  public override long Position {
    get => this._bytesWritten;
    set => throw new NotSupportedException("DeferredLengthWriteStream is write-only and not seekable.");
  }

  public override void Write(byte[] buffer, int offset, int count) {
    ArgumentNullException.ThrowIfNull(buffer);
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (count <= 0) return;
    this.EnsureSinkFor(count);
    if (this._spillStream != null)
      this._spillStream.Write(buffer, offset, count);
    else
      this._buffer!.Write(buffer, offset, count);
    this._bytesWritten += count;
  }

  public override void Write(ReadOnlySpan<byte> buffer) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (buffer.Length == 0) return;
    this.EnsureSinkFor(buffer.Length);
    if (this._spillStream != null)
      this._spillStream.Write(buffer);
    else
      this._buffer!.Write(buffer);
    this._bytesWritten += buffer.Length;
  }

  public override void WriteByte(byte value) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    this.EnsureSinkFor(1);
    if (this._spillStream != null)
      this._spillStream.WriteByte(value);
    else
      this._buffer!.WriteByte(value);
    this._bytesWritten += 1;
  }

  public override int Read(byte[] buffer, int offset, int count)
    => throw new NotSupportedException("DeferredLengthWriteStream is write-only.");

  public override long Seek(long offset, SeekOrigin origin)
    => throw new NotSupportedException("DeferredLengthWriteStream is write-only and not seekable.");

  public override void SetLength(long value)
    => throw new NotSupportedException("DeferredLengthWriteStream is write-only.");

  public override void Flush() {
    if (this._spillStream != null) this._spillStream.Flush();
    // MemoryStream.Flush is a no-op.
  }

  /// <summary>
  /// Spills the in-memory buffer to a tempfile when the next write would
  /// push the total past <see cref="SpillThresholdBytes"/>. After spill all
  /// subsequent writes flow straight to the file.
  /// </summary>
  private void EnsureSinkFor(int incomingBytes) {
    if (this._spillStream != null) return; // already spilled
    var prospective = this._bytesWritten + incomingBytes;
    if (prospective <= this._spillThresholdBytes) return;

    // Spill: create the temp file, flush the in-memory buffer into it, and
    // switch the active sink. The MemoryStream is released so the GC can
    // reclaim its bytes immediately.
    Directory.CreateDirectory(this._spillDirectory);
    var name = "cwb_dlw_" + Guid.NewGuid().ToString("N")[..12];
    this._spillPath = Path.Combine(this._spillDirectory, name);
    this._spillStream = new FileStream(this._spillPath, FileMode.CreateNew,
      FileAccess.ReadWrite, FileShare.Read, bufferSize: 4096, FileOptions.None);
    var pending = this._buffer!.GetBuffer();
    var pendingLen = (int)this._buffer.Length;
    if (pendingLen > 0)
      this._spillStream.Write(pending, 0, pendingLen);
    this._buffer.Dispose();
    this._buffer = null;
  }

  /// <summary>
  /// Re-opens the buffered content for reading. The returned stream is a
  /// fresh handle that owns the lifetime of the spilled file (if any) —
  /// disposing it deletes the temp file. Called by the commit callback.
  /// </summary>
  private Stream OpenContentForReading(byte[]? memorySnapshot, string? spillPath) {
    if (spillPath != null)
      return new SpillFileReadStream(spillPath);
    return new MemoryStream(memorySnapshot ?? Array.Empty<byte>(), writable: false);
  }

  protected override void Dispose(bool disposing) {
    if (this._disposed) {
      base.Dispose(disposing);
      return;
    }
    this._disposed = true;

    try {
      if (!disposing) {
        // Finalizer path: best-effort delete the spill file.
        this.TryDeleteSpillBestEffort();
        return;
      }

      if (this._cancelled) {
        // Drop everything; don't fire the commit callback.
        this.DiscardAll();
        return;
      }

      // Snapshot the buffered content for the commit callback. After this
      // point the in-memory buffer is released; the consumer either reads
      // from the snapshot byte[] or re-opens the spill file.
      byte[]? memorySnapshot = null;
      var spillPathSnapshot = this._spillPath;
      if (this._spillStream != null) {
        // Flush + close the spill stream; the consumer's re-open will
        // create a new FileStream on the temp path.
        this._spillStream.Flush();
        this._spillStream.Dispose();
        this._spillStream = null;
      } else if (this._buffer != null) {
        memorySnapshot = this._buffer.ToArray();
        this._buffer.Dispose();
        this._buffer = null;
      }

      var bytesWritten = this._bytesWritten;
      // Forget our own reference so the finalizer never deletes a path the
      // consumer is responsible for.
      this._spillPath = null;
      this._onClose!(bytesWritten, () => this.OpenContentForReading(memorySnapshot, spillPathSnapshot));
    } finally {
      base.Dispose(disposing);
    }
  }

  private void DiscardAll() {
    if (this._spillStream != null) {
      this._spillStream.Dispose();
      this._spillStream = null;
    }
    if (this._buffer != null) {
      this._buffer.Dispose();
      this._buffer = null;
    }
    this.TryDeleteSpillBestEffort();
    this._spillPath = null;
  }

  private void TryDeleteSpillBestEffort() {
    var path = this._spillPath;
    if (string.IsNullOrEmpty(path)) return;
    try {
      if (File.Exists(path)) File.Delete(path);
    } catch {
      // Best-effort: a process crash + reboot will eventually clean
      // %TEMP% anyway. We never want a delete failure to crash the
      // caller.
    }
  }

  ~DeferredLengthWriteStream() {
    this.Dispose(disposing: false);
  }

  /// <summary>
  /// Wraps a spilled tempfile for reading and deletes it on dispose.
  /// The consumer of the deferred-length stream receives this via the
  /// commit callback's <c>Func&lt;Stream&gt;</c>.
  /// </summary>
  private sealed class SpillFileReadStream : Stream {

    private readonly string _path;
    private readonly FileStream _inner;
    private bool _disposed;

    public SpillFileReadStream(string path) {
      this._path = path;
      this._inner = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
    }

    public override bool CanRead => !this._disposed;
    public override bool CanWrite => false;
    public override bool CanSeek => !this._disposed;
    public override long Length => this._inner.Length;
    public override long Position { get => this._inner.Position; set => this._inner.Position = value; }
    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => this._inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => this._inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
      if (this._disposed) {
        base.Dispose(disposing);
        return;
      }
      this._disposed = true;
      try {
        if (disposing) this._inner.Dispose();
      } finally {
        try { if (File.Exists(this._path)) File.Delete(this._path); } catch { /* best-effort */ }
        base.Dispose(disposing);
      }
    }
  }
}
