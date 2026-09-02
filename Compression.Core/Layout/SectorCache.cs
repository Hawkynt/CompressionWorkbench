#pragma warning disable CS1591
namespace Compression.Core.Layout;

/// <summary>
/// Chunked LRU read cache over a <see cref="Stream"/>. Lets extent walkers
/// and block movers read FAT entries, allocation bitmaps, MFT records, etc.
/// at arbitrary offsets without loading the whole image into memory.
/// <para>
/// Designed for multi-TB filesystem images where the FAT alone (50 GB for a
/// 50 TB exFAT volume) does not fit in RAM. Reads happen in fixed-size chunks
/// (default 64 KB); recently-accessed chunks stay resident up to a configurable
/// memory cap (default 256 MB ≈ 4096 chunks). LRU eviction beyond the cap.
/// </para>
/// <para>
/// Sequential access (the common case — walking a defragmented file's chain)
/// hits the same chunk repeatedly; random access (heavily fragmented FS) may
/// miss once per cluster but the OS page cache absorbs much of that cost.
/// </para>
/// <para><b>Write coherence:</b> if the caller writes to the underlying stream
/// outside this cache, call <see cref="Invalidate(long, int)"/> for the affected
/// range so subsequent reads observe the new bytes.</para>
/// </summary>
public sealed class SectorCache : IDisposable {

  private readonly Stream _stream;
  private readonly int _chunkSize;
  private readonly int _maxChunks;
  private readonly Dictionary<long, LinkedListNode<CacheEntry>> _chunks;
  private readonly LinkedList<CacheEntry> _lru;

  /// <summary>Default chunk size (64 KB).</summary>
  public const int DefaultChunkSize = 64 * 1024;
  /// <summary>Default chunk count (4096 × 64 KB ≈ 256 MB cap).</summary>
  public const int DefaultMaxChunks = 4096;

  /// <param name="stream">Underlying readable+seekable stream. Not owned —
  /// caller is responsible for disposal.</param>
  /// <param name="chunkSize">Chunk size in bytes. Should be a power of two and
  /// at least the largest single read (e.g. one cluster) to avoid double-
  /// fetching a single read. Default 64 KB.</param>
  /// <param name="maxChunks">Maximum number of chunks kept resident. Memory
  /// cap = chunkSize × maxChunks. Default 4096 → 256 MB.</param>
  /// <summary>
  /// Initializes a new instance of <see cref="SectorCache"/>.
  /// </summary>
public SectorCache(Stream stream, int chunkSize = DefaultChunkSize, int maxChunks = DefaultMaxChunks) {
    ArgumentNullException.ThrowIfNull(stream);
    if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
    if (maxChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunks));
    if ((chunkSize & (chunkSize - 1)) != 0)
      throw new ArgumentException("chunkSize must be a power of two.", nameof(chunkSize));
    _stream = stream;
    _chunkSize = chunkSize;
    _maxChunks = maxChunks;
    _chunks = new Dictionary<long, LinkedListNode<CacheEntry>>(maxChunks);
    _lru = new LinkedList<CacheEntry>();
  }

  /// <summary>Stream length passthrough.</summary>
  public long Length => _stream.Length;

  /// <summary>
  /// Reads <paramref name="dest"/>.Length bytes starting at <paramref name="offset"/>
  /// into <paramref name="dest"/>. Spans multiple chunks transparently.
  /// </summary>
  public void Read(long offset, Span<byte> dest) {
    var remaining = dest.Length;
    var srcOff = offset;
    var dstOff = 0;
    while (remaining > 0) {
      var chunkStart = srcOff & ~(long)(_chunkSize - 1); // power-of-two floor
      var chunk = GetChunk(chunkStart);
      var inChunkOff = (int)(srcOff - chunkStart);
      var copyLen = Math.Min(remaining, _chunkSize - inChunkOff);
      chunk.AsSpan(inChunkOff, copyLen).CopyTo(dest.Slice(dstOff, copyLen));
      remaining -= copyLen;
      srcOff += copyLen;
      dstOff += copyLen;
    }
  }

  /// <summary>Convenience wrapper that allocates the destination buffer.</summary>
  public byte[] Read(long offset, int length) {
    var buf = new byte[length];
    Read(offset, buf);
    return buf;
  }

  /// <summary>
  /// Invalidates cached chunks overlapping [offset, offset+length). Call after
  /// any direct write to the underlying stream so the next Read returns the
  /// fresh bytes from disk.
  /// </summary>
  public void Invalidate(long offset, int length) {
    if (length <= 0) return;
    var start = offset & ~(long)(_chunkSize - 1);
    var endExclusive = (offset + length + _chunkSize - 1) & ~(long)(_chunkSize - 1);
    for (var pos = start; pos < endExclusive; pos += _chunkSize) {
      if (_chunks.TryGetValue(pos, out var node)) {
        _lru.Remove(node);
        _chunks.Remove(pos);
      }
    }
  }

  /// <summary>Drops all cached chunks (e.g. after a full image rewrite).</summary>
  public void InvalidateAll() {
    _chunks.Clear();
    _lru.Clear();
  }

  private byte[] GetChunk(long chunkStart) {
    if (_chunks.TryGetValue(chunkStart, out var existing)) {
      // LRU touch: move to head.
      _lru.Remove(existing);
      _lru.AddFirst(existing);
      return existing.Value.Data;
    }

    // Miss — read from stream. Tail-truncated read OK (e.g. last chunk in
    // a stream that isn't a multiple of chunkSize); we just zero-pad the
    // remainder.
    var data = new byte[_chunkSize];
    _stream.Position = chunkStart;
    var read = 0;
    while (read < _chunkSize) {
      var n = _stream.Read(data, read, _chunkSize - read);
      if (n <= 0) break;
      read += n;
    }
    // Unread tail stays as zeros — matches an extended-file read semantic.

    // Evict oldest if at capacity.
    if (_chunks.Count >= _maxChunks) {
      var oldest = _lru.Last!;
      _chunks.Remove(oldest.Value.ChunkStart);
      _lru.RemoveLast();
    }

    var entry = new CacheEntry(chunkStart, data);
    var node = new LinkedListNode<CacheEntry>(entry);
    _lru.AddFirst(node);
    _chunks[chunkStart] = node;
    return data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    _chunks.Clear();
    _lru.Clear();
  }

  private sealed record CacheEntry(long ChunkStart, byte[] Data);
}
