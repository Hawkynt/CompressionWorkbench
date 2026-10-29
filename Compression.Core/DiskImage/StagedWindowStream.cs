namespace Compression.Core.DiskImage;

/// <summary>
/// A writable, fixed-length window over a range of a disk whose writes are held back until
/// <see cref="Commit"/>. Reads see the staged writes on top of the disk's bytes, so a
/// filesystem editor working through the window behaves exactly as it would on a standalone
/// image; but nothing reaches the disk unless the whole edit finishes and the caller commits.
/// An editor that throws half-way, or that tries to grow its volume past the partition, leaves
/// the disk byte-identical.
/// </summary>
/// <remarks>
/// Writes are staged in 4 KiB pages, so the memory held is proportional to what the edit
/// touches, not to the partition.
/// </remarks>
public sealed class StagedWindowStream : Stream {
  private const int PageSize = 4096;

  private readonly Stream _disk;
  private readonly long _offset;
  private readonly long _length;
  private readonly Dictionary<long, byte[]> _pages = [];
  private long _position;
  private bool _committed;

  /// <summary>Opens a staged window of <paramref name="length"/> bytes at <paramref name="offset"/> on <paramref name="disk"/>.</summary>
  public StagedWindowStream(Stream disk, long offset, long length) {
    ArgumentNullException.ThrowIfNull(disk);
    if (!disk.CanSeek || !disk.CanRead) throw new ArgumentException("The disk must be readable and seekable.", nameof(disk));
    ArgumentOutOfRangeException.ThrowIfNegative(offset);
    ArgumentOutOfRangeException.ThrowIfNegative(length);
    this._disk = disk;
    this._offset = offset;
    this._length = length;
  }

  /// <summary>Whether any write has been staged.</summary>
  public bool HasChanges => this._pages.Count > 0;

  /// <inheritdoc/>
  public override bool CanRead => true;
  /// <inheritdoc/>
  public override bool CanSeek => true;
  /// <inheritdoc/>
  public override bool CanWrite => !this._committed;
  /// <inheritdoc/>
  public override long Length => this._length;

  /// <inheritdoc/>
  public override long Position {
    get => this._position;
    set {
      ArgumentOutOfRangeException.ThrowIfNegative(value);
      this._position = value;
    }
  }

  /// <inheritdoc/>
  public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

  /// <inheritdoc/>
  public override int Read(Span<byte> buffer) {
    if (this._position >= this._length) return 0;
    var total = (int)Math.Min(buffer.Length, this._length - this._position);
    var done = 0;
    while (done < total) {
      var pos = this._position + done;
      var pageIndex = pos / PageSize;
      var inPage = (int)(pos % PageSize);
      var chunk = Math.Min(total - done, PageSize - inPage);
      if (this._pages.TryGetValue(pageIndex, out var page)) {
        page.AsSpan(inPage, chunk).CopyTo(buffer.Slice(done, chunk));
      } else {
        this._disk.Position = this._offset + pos;
        var read = this._disk.ReadAtLeast(buffer.Slice(done, chunk), chunk, throwOnEndOfStream: false);
        if (read < chunk) buffer.Slice(done + read, chunk - read).Clear();
      }
      done += chunk;
    }
    this._position += total;
    return total;
  }

  /// <inheritdoc/>
  public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

  /// <inheritdoc/>
  public override void Write(ReadOnlySpan<byte> buffer) {
    if (this._committed) throw new InvalidOperationException("The staged window has already been committed.");
    if (this._position + buffer.Length > this._length)
      throw new IOException(
        $"Write at {this._position} for {buffer.Length} bytes runs past the end of the {this._length}-byte partition.");
    var done = 0;
    while (done < buffer.Length) {
      var pos = this._position + done;
      var pageIndex = pos / PageSize;
      var inPage = (int)(pos % PageSize);
      var chunk = Math.Min(buffer.Length - done, PageSize - inPage);
      buffer.Slice(done, chunk).CopyTo(this.PageFor(pageIndex).AsSpan(inPage, chunk));
      done += chunk;
    }
    this._position += buffer.Length;
  }

  private byte[] PageFor(long pageIndex) {
    if (this._pages.TryGetValue(pageIndex, out var page)) return page;
    page = new byte[PageSize];
    var pageStart = pageIndex * PageSize;
    var valid = (int)Math.Min(PageSize, this._length - pageStart);
    this._disk.Position = this._offset + pageStart;
    _ = this._disk.ReadAtLeast(page.AsSpan(0, valid), valid, throwOnEndOfStream: false);
    this._pages[pageIndex] = page;
    return page;
  }

  /// <inheritdoc/>
  public override long Seek(long offset, SeekOrigin origin) {
    this.Position = origin switch {
      SeekOrigin.Begin => offset,
      SeekOrigin.Current => this._position + offset,
      SeekOrigin.End => this._length + offset,
      _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    return this._position;
  }

  /// <summary>
  /// A partition cannot grow from inside: asking for more than its length fails, and asking for
  /// less leaves the trailing bytes where they are, as unused space inside the partition.
  /// </summary>
  public override void SetLength(long value) {
    if (value > this._length)
      throw new IOException($"The filesystem asked for {value} bytes, but its partition holds {this._length}.");
  }

  /// <summary>Staged writes stay staged; only <see cref="Commit"/> writes them.</summary>
  public override void Flush() { }

  /// <summary>Writes every staged page to the disk, in disk order, then flushes it.</summary>
  public void Commit() {
    if (this._committed) return;
    if (this._pages.Count > 0 && !this._disk.CanWrite)
      throw new NotSupportedException("The disk is read-only.");
    foreach (var (pageIndex, page) in this._pages.OrderBy(p => p.Key)) {
      var pageStart = pageIndex * PageSize;
      var valid = (int)Math.Min(PageSize, this._length - pageStart);
      this._disk.Position = this._offset + pageStart;
      this._disk.Write(page, 0, valid);
    }
    this._disk.Flush();
    this._committed = true;
    this._pages.Clear();
  }
}
