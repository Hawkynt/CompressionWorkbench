namespace Compression.Core.DiskImage;

/// <summary>
/// Sparse, block-addressed image buffer. Only the blocks a writer actually
/// touches are allocated, so laying out a multi-gigabyte volume costs its
/// metadata rather than its size — and a volume too large for a byte[] can
/// still be written straight to a stream.
/// </summary>
/// <remarks>
/// Shared by the filesystem writers. <see cref="At" /> is the fast path for a
/// field that is known not to straddle a block boundary; <see cref="Write" /> and
/// <see cref="Read" /> handle arbitrary offsets and lengths.
/// </remarks>
public sealed class SparseBlockImage(int blockSize, long totalBytes) {

  private readonly Dictionary<int, byte[]> _blocks = [];

  /// <summary>Block size in bytes.</summary>
  public int BlockSize { get; } = blockSize;

  /// <summary>Declared size of the finished volume.</summary>
  public long TotalBytes { get; } = totalBytes;

  /// <summary>The whole of <paramref name="block" />, allocated on first touch.</summary>
  public Span<byte> Block(int block) {
    if (!this._blocks.TryGetValue(block, out var data))
      this._blocks[block] = data = new byte[this.BlockSize];
    return data;
  }

  /// <summary>
  /// <paramref name="length" /> bytes at <paramref name="offset" />. Every ext
  /// structure this writer emits — superblock, group descriptor, inode, block
  /// pointer — is aligned so that it never crosses a block boundary.
  /// </summary>
  public Span<byte> At(long offset, int length) {
    var within = (int)(offset % this.BlockSize);
    if (within + length > this.BlockSize)
      throw new ArgumentOutOfRangeException(nameof(length), "A field must not cross a block boundary.");
    return this.Block((int)(offset / this.BlockSize)).Slice(within, length);
  }

  /// <summary>Copies <paramref name="data" /> to <paramref name="offset" />, spanning blocks as needed.</summary>
  public void Write(long offset, ReadOnlySpan<byte> data) {
    while (!data.IsEmpty) {
      var block = (int)(offset / this.BlockSize);
      var within = (int)(offset % this.BlockSize);
      var take = Math.Min(data.Length, this.BlockSize - within);
      data[..take].CopyTo(this.Block(block).Slice(within, take));
      data = data[take..];
      offset += take;
    }
  }

  /// <summary>Fills <paramref name="count" /> bytes at <paramref name="offset" /> with <paramref name="value" />.</summary>
  public void Fill(long offset, byte value, long count) {
    while (count > 0) {
      var block = (int)(offset / this.BlockSize);
      var within = (int)(offset % this.BlockSize);
      var take = (int)Math.Min(count, this.BlockSize - within);
      this.Block(block).Slice(within, take).Fill(value);
      count -= take;
      offset += take;
    }
  }

  /// <summary>Reads <paramref name="length" /> bytes from <paramref name="offset" />, spanning blocks as needed.</summary>
  public byte[] Read(long offset, int length) {
    var result = new byte[length];
    var written = 0;
    while (written < length) {
      var block = (int)((offset + written) / this.BlockSize);
      var within = (int)((offset + written) % this.BlockSize);
      var take = Math.Min(length - written, this.BlockSize - within);
      this.Block(block).Slice(within, take).CopyTo(result.AsSpan(written, take));
      written += take;
    }
    return result;
  }

  /// <summary>Single byte at <paramref name="offset" />.</summary>
  public byte this[long offset] {
    get => this.Block((int)(offset / this.BlockSize))[(int)(offset % this.BlockSize)];
    set => this.Block((int)(offset / this.BlockSize))[(int)(offset % this.BlockSize)] = value;
  }

  /// <summary>Materialises the whole volume.</summary>
  /// <exception cref="InvalidOperationException">The volume is larger than a byte[] can hold.</exception>
  public byte[] Materialise() {
    if (this.TotalBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"A {this.TotalBytes:N0}-byte volume exceeds the array limit; write it to a seekable stream instead.");

    var image = new byte[this.TotalBytes];
    foreach (var (block, data) in this._blocks) {
      var offset = (long)block * this.BlockSize;
      var take = (int)Math.Min(data.Length, this.TotalBytes - offset);
      if (take > 0) data.AsSpan(0, take).CopyTo(image.AsSpan((int)offset));
    }
    return image;
  }

  /// <summary>Writes the volume from the current position, extending the stream to its full size.</summary>
  public void WriteTo(Stream output) {
    var basePosition = output.Position;
    output.SetLength(basePosition + this.TotalBytes);
    foreach (var block in this._blocks.Keys.Order()) {
      var offset = (long)block * this.BlockSize;
      var take = (int)Math.Min(this.BlockSize, this.TotalBytes - offset);
      if (take <= 0) continue;
      output.Position = basePosition + offset;
      output.Write(this._blocks[block], 0, take);
    }
    output.Position = basePosition + this.TotalBytes;
  }
}
