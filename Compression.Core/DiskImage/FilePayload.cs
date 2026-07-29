namespace Compression.Core.DiskImage;

/// <summary>
/// A file's contents as a filesystem writer needs them: a length that is known up
/// front, plus either the bytes themselves or a factory that produces them.
/// </summary>
/// <remarks>
/// A writer only needs the length to lay a volume out. Holding the bytes as well
/// caps the volume at what a <see cref="byte" /> array can address, which is the
/// one thing a multi-gigabyte volume cannot afford.
/// </remarks>
public readonly record struct FilePayload(long Size, byte[]? Data, Func<Stream>? Opener) {

  /// <summary>An empty payload.</summary>
  public static FilePayload Empty => new(0, [], null);

  /// <summary>Wraps bytes already in hand.</summary>
  public static FilePayload FromBytes(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    return new FilePayload(data.LongLength, data, null);
  }

  /// <summary>Wraps a stream factory whose output is <paramref name="size" /> bytes long.</summary>
  public static FilePayload FromStream(long size, Func<Stream> opener) {
    ArgumentNullException.ThrowIfNull(opener);
    ArgumentOutOfRangeException.ThrowIfNegative(size);
    return new FilePayload(size, null, opener);
  }

  /// <summary>Opens the payload for reading.</summary>
  public Stream Open()
    => this.Data is { } bytes ? new MemoryStream(bytes, writable: false)
     : this.Opener is { } opener ? opener()
     : new MemoryStream([], writable: false);

  /// <summary>The bytes, materialised. Only valid below the array limit.</summary>
  public byte[] ToArray() {
    if (this.Data is { } bytes) return bytes;
    if (this.Size > Array.MaxLength)
      throw new InvalidOperationException(
        $"A {this.Size:N0}-byte payload exceeds the array limit; stream it instead.");

    using var src = this.Open();
    var result = new byte[this.Size];
    var read = 0;
    while (read < result.Length) {
      var n = src.Read(result, read, result.Length - read);
      if (n <= 0) break;
      read += n;
    }
    return result;
  }
}

/// <summary>
/// File payloads a writer has placed but not yet emitted, each pinned to the byte
/// offset it belongs at. Lets a writer lay out a volume without ever holding its
/// contents.
/// </summary>
public sealed class DeferredPayloads {

  private readonly List<(long Offset, FilePayload Payload)> _writes = [];

  /// <summary>Records that <paramref name="payload" /> belongs at <paramref name="offset" />.</summary>
  public void Add(long offset, FilePayload payload) {
    if (payload.Size > 0) this._writes.Add((offset, payload));
  }

  /// <summary>Records bytes at <paramref name="offset" />.</summary>
  public void Add(long offset, byte[] data) => this.Add(offset, FilePayload.FromBytes(data));

  /// <summary>Number of payloads recorded.</summary>
  public int Count => this._writes.Count;

  /// <summary>
  /// Copies every payload into <paramref name="output" /> at its offset, relative
  /// to <paramref name="basePosition" />. Nothing larger than the copy buffer is
  /// resident at any point.
  /// </summary>
  public void FlushTo(Stream output, long basePosition = 0) {
    ArgumentNullException.ThrowIfNull(output);
    if (this._writes.Count == 0) return;

    var buffer = new byte[64 * 1024];
    foreach (var (offset, payload) in this._writes) {
      output.Position = basePosition + offset;
      using var src = payload.Open();
      var remaining = payload.Size;
      while (remaining > 0) {
        var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
        if (n <= 0) break;
        output.Write(buffer, 0, n);
        remaining -= n;
      }
    }
    output.Flush();
  }

  /// <summary>
  /// Materialises <paramref name="image" /> and fills in every payload, for callers
  /// that still need the whole volume as an array.
  /// </summary>
  /// <exception cref="InvalidOperationException">The volume is larger than a byte[] can hold.</exception>
  public byte[] Materialise(SparseBlockImage image) {
    ArgumentNullException.ThrowIfNull(image);
    var full = image.Materialise();
    using var target = new MemoryStream(full, writable: true);
    this.FlushTo(target);
    return full;
  }
}
