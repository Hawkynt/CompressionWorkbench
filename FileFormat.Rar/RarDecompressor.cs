using Compression.Core.Dictionary.Rar;

namespace FileFormat.Rar;

/// <summary>
/// RAR5 decompressor. Supports Store (method 0) and compressed (methods 1-5).
/// </summary>
internal sealed class RarDecompressor {
  private readonly Stream _input;
  private readonly int _method;
  private long _remaining;
  private byte[]? _decompressedData;
  private int _decompressedPos;
  private readonly int _dictionarySize;
  private readonly long _unpackedSize;
  private readonly long _compressedSize;
  private readonly Rar5Decoder? _existingDecoder;

  /// <summary>
  /// Initializes a new <see cref="RarDecompressor"/>.
  /// </summary>
  /// <param name="input">The input stream positioned at the compressed data.</param>
  /// <param name="method">Compression method (0=Store, 1-5=compressed).</param>
  /// <param name="unpackedSize">Expected uncompressed size.</param>
  /// <param name="dictionarySize">Dictionary size for compressed methods.</param>
  /// <param name="compressedSize">Size of the compressed data in the stream.</param>
  /// <param name="existingDecoder">An existing decoder to reuse for solid archive continuations, or <see langword="null"/> to create a fresh decoder.</param>
  public RarDecompressor(Stream input, int method, long unpackedSize, int dictionarySize,
    long compressedSize, Rar5Decoder? existingDecoder = null) {
    this._input = input;
    this._method = method;
    this._remaining = unpackedSize;
    this._dictionarySize = dictionarySize;
    this._unpackedSize = unpackedSize;
    this._compressedSize = compressedSize;
    this._existingDecoder = existingDecoder;

    if (method != RarConstants.MethodStore && (method < 1 || method > 5))
      throw new NotSupportedException(
        $"RAR compression method {method} is not supported.");
  }

  /// <summary>
  /// Gets the <see cref="Rar5Decoder"/> used during decompression, if any.
  /// This decoder retains sliding window and repeat offset state for solid archive continuation.
  /// </summary>
  public Rar5Decoder? Decoder { get; private set; }

  /// <summary>
  /// Reads decompressed data into the buffer.
  /// </summary>
  public int Read(byte[] buffer, int offset, int count) {
    if (this._remaining <= 0)
      return 0;

    if (this._method == RarConstants.MethodStore) {
      int toRead = (int)Math.Min(count, this._remaining);
      int read = this._input.Read(buffer, offset, toRead);
      this._remaining -= read;
      return read;
    }

    // Compressed methods: decompress all at once on first read
    if (this._decompressedData == null) {
      // Read only the compressed data for this entry, not the rest of the stream
      byte[] compressed = new byte[this._compressedSize];
      int totalRead = 0;
      while (totalRead < compressed.Length) {
        int read = this._input.Read(compressed, totalRead, compressed.Length - totalRead);
        if (read == 0)
          break;
        totalRead += read;
      }

      var decoder = this._existingDecoder ?? new Rar5Decoder(this._dictionarySize);
      this._decompressedData = decoder.Decompress(compressed, (int)this._unpackedSize);
      this._decompressedPos = 0;
      this.Decoder = decoder;
    }

    int available = this._decompressedData.Length - this._decompressedPos;
    int toCopy = (int)Math.Min(Math.Min(count, available), this._remaining);
    if (toCopy <= 0) {
      this._remaining = 0;
      return 0;
    }

    this._decompressedData.AsSpan(this._decompressedPos, toCopy).CopyTo(buffer.AsSpan(offset));
    this._decompressedPos += toCopy;
    this._remaining -= toCopy;
    return toCopy;
  }

  /// <summary>
  /// Gets a value indicating whether all expected data has been read.
  /// </summary>
  public bool IsFinished => this._remaining <= 0;
}
