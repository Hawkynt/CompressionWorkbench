using System.Buffers.Binary;

namespace Compression.Core.DiskImage;

/// <summary>
/// Random-access view over a disk image that may be far larger than the ~2 GB a
/// <see cref="byte" /> array or <see cref="MemoryStream" /> can hold.
/// </summary>
/// <remarks>
/// <para>
/// Filesystem readers historically copied the whole image into a
/// <see cref="MemoryStream" /> and indexed the resulting array. That caps them at
/// the array limit, so a 4 GB FAT32 volume — or any real-world SD-card image —
/// throws "Stream was too long" before a single directory entry is parsed.
/// </para>
/// <para>
/// Reads here are served from a small block cache over the underlying seekable
/// stream, so touching a 128 GB volume costs only the blocks actually read. All
/// offsets are 64-bit; the 32-bit cluster arithmetic that used to accompany the
/// array indexing overflows silently on large volumes and must not be reintroduced.
/// </para>
/// <para>Instances are not thread-safe: the cache and the shared stream position are mutable.</para>
/// </remarks>
public sealed class ImageAccessor : IDisposable {

  /// <summary>Block granularity of the cache. Large enough to amortise seeks, small enough that a sparse read costs little.</summary>
  private const int BlockSize = 64 * 1024;

  /// <summary>Number of blocks retained. Directory walks hit the FAT and the dirent area repeatedly, so a handful covers the hot set.</summary>
  private const int MaxCachedBlocks = 64;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly Dictionary<long, byte[]> _cache = [];
  private readonly Queue<long> _order = new();
  private bool _disposed;

  /// <summary>Total length of the image in bytes.</summary>
  public long Length { get; }

  /// <summary>
  /// Wraps <paramref name="stream" />, which must be readable and seekable.
  /// </summary>
  /// <param name="stream">The image to read. Not disposed unless <paramref name="leaveOpen" /> is false.</param>
  /// <param name="leaveOpen">When false, disposing this accessor disposes <paramref name="stream" />.</param>
  /// <exception cref="ArgumentException">The stream cannot be read or seeked.</exception>
  public ImageAccessor(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("ImageAccessor requires a readable, seekable stream.", nameof(stream));

    this._stream = stream;
    this._leaveOpen = leaveOpen;
    this.Length = stream.Length;
  }

  /// <summary>
  /// Drops the cached copy of the range <paramref name="offset" />..<paramref name="offset" /> +
  /// <paramref name="length" />, so a caller that wrote to the underlying stream
  /// sees its own bytes on the next read.
  /// </summary>
  public void Invalidate(long offset, long length) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (length <= 0) return;
    var first = offset / BlockSize * BlockSize;
    var last = (offset + length - 1) / BlockSize * BlockSize;
    for (var blockStart = first; blockStart <= last; blockStart += BlockSize)
      this._cache.Remove(blockStart);
  }

  /// <summary>Materialises an in-memory image. Convenience for callers that already hold the bytes.</summary>
  public static ImageAccessor FromBytes(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    return new ImageAccessor(new MemoryStream(data, writable: false), leaveOpen: false);
  }

  /// <summary>
  /// Fills <paramref name="destination" /> from <paramref name="offset" />, returning the
  /// number of bytes actually available. Bytes past the end of the image are left untouched,
  /// so a short read yields zeros rather than throwing.
  /// </summary>
  public int Read(long offset, Span<byte> destination) {
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (offset < 0 || offset >= this.Length || destination.IsEmpty) return 0;

    var available = (int)Math.Min(destination.Length, this.Length - offset);
    var copied = 0;
    while (copied < available) {
      var blockStart = (offset + copied) / BlockSize * BlockSize;
      var block = this.GetBlock(blockStart);
      var withinBlock = (int)(offset + copied - blockStart);
      var take = Math.Min(available - copied, block.Length - withinBlock);
      if (take <= 0) break;
      block.AsSpan(withinBlock, take).CopyTo(destination[copied..]);
      copied += take;
    }
    return copied;
  }

  /// <summary>
  /// Reads <paramref name="count" /> bytes from <paramref name="offset" />. The result is always
  /// <paramref name="count" /> long; any part beyond the end of the image reads as zero.
  /// </summary>
  public byte[] Read(long offset, int count) {
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    var buffer = new byte[count];
    this.Read(offset, buffer.AsSpan());
    return buffer;
  }

  /// <summary>Reads a single byte, or 0 when <paramref name="offset" /> lies outside the image.</summary>
  public byte ReadByte(long offset) {
    Span<byte> one = stackalloc byte[1];
    return this.Read(offset, one) == 1 ? one[0] : (byte)0;
  }

  /// <summary>Reads a little-endian unsigned 16-bit value.</summary>
  public ushort ReadUInt16(long offset) {
    Span<byte> b = stackalloc byte[2];
    this.Read(offset, b);
    return BinaryPrimitives.ReadUInt16LittleEndian(b);
  }

  /// <summary>Reads a little-endian signed 32-bit value.</summary>
  public int ReadInt32(long offset) {
    Span<byte> b = stackalloc byte[4];
    this.Read(offset, b);
    return BinaryPrimitives.ReadInt32LittleEndian(b);
  }

  /// <summary>Reads a little-endian unsigned 32-bit value.</summary>
  public uint ReadUInt32(long offset) {
    Span<byte> b = stackalloc byte[4];
    this.Read(offset, b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  /// <summary>Reads a little-endian signed 64-bit value.</summary>
  public long ReadInt64(long offset) {
    Span<byte> b = stackalloc byte[8];
    this.Read(offset, b);
    return BinaryPrimitives.ReadInt64LittleEndian(b);
  }

  /// <summary>Reads a little-endian unsigned 64-bit value.</summary>
  public ulong ReadUInt64(long offset) {
    Span<byte> b = stackalloc byte[8];
    this.Read(offset, b);
    return BinaryPrimitives.ReadUInt64LittleEndian(b);
  }

  /// <summary>Copies <paramref name="count" /> bytes from <paramref name="offset" /> into <paramref name="destination" />.</summary>
  public void CopyTo(long offset, Stream destination, long count) {
    ArgumentNullException.ThrowIfNull(destination);
    var buffer = new byte[Math.Min(BlockSize, Math.Max(1, count))];
    long written = 0;
    while (written < count) {
      var want = (int)Math.Min(buffer.Length, count - written);
      var got = this.Read(offset + written, buffer.AsSpan(0, want));
      if (got <= 0) break;
      destination.Write(buffer, 0, got);
      written += got;
    }
  }

  private byte[] GetBlock(long blockStart) {
    if (this._cache.TryGetValue(blockStart, out var cached)) return cached;

    var size = (int)Math.Min(BlockSize, this.Length - blockStart);
    var block = new byte[Math.Max(size, 0)];
    if (size > 0) {
      this._stream.Position = blockStart;
      var read = 0;
      while (read < size) {
        var n = this._stream.Read(block, read, size - read);
        if (n <= 0) break;
        read += n;
      }
    }

    this._cache[blockStart] = block;
    this._order.Enqueue(blockStart);
    while (this._order.Count > MaxCachedBlocks)
      this._cache.Remove(this._order.Dequeue());

    return block;
  }

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    this._cache.Clear();
    this._order.Clear();
    if (!this._leaveOpen) this._stream.Dispose();
  }
}
