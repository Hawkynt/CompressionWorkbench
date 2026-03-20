namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA2 encoder that wraps LZMA data in chunked format with control bytes.
/// Passes historical context to the LZMA encoder for cross-chunk back-references.
/// </summary>
public sealed class Lzma2Encoder {
  private const int MaxLzmaInputChunkSize = 1 << 21; // 2 MB (21-bit unpacked size field)
  private const int MaxPackedSize = 1 << 16;          // 64 KB (16-bit packed size field)
  private const int MaxUncompressedChunkSize = 1 << 16; // 64 KB (16-bit size field for uncompressed chunks)
  private readonly int _dictionarySize;
  private readonly LzmaCompressionLevel _level;

  /// <summary>
  /// Gets the encoded dictionary size byte for XZ headers.
  /// </summary>
  public byte DictionarySizeByte { get; }

  /// <summary>
  /// Initializes a new LZMA2 encoder.
  /// </summary>
  /// <param name="dictionarySize">The dictionary size in bytes.</param>
  /// <param name="level">The compression level.</param>
  public Lzma2Encoder(int dictionarySize = 1 << 23,
      LzmaCompressionLevel level = LzmaCompressionLevel.Normal) {
    this._dictionarySize = dictionarySize;
    this._level = level;
    this.DictionarySizeByte = EncodeDictionarySize(dictionarySize);
  }

  /// <summary>
  /// Encodes data in LZMA2 format to the output stream.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="data">The data to compress.</param>
  public void Encode(Stream output, ReadOnlySpan<byte> data) {
    if (data.Length == 0) {
      output.WriteByte(0x00); // End marker
      return;
    }

    var offset = 0;
    var needFullReset = true;

    while (offset < data.Length) {
      var chunkSize = Math.Min(Lzma2Encoder.MaxLzmaInputChunkSize, data.Length - offset);

      // Provide the encoder with historical context (up to dictionary size)
      var historyStart = Math.Max(0, offset - this._dictionarySize);
      var contextAndChunk = data.Slice(historyStart, offset - historyStart + chunkSize);
      var chunkOffset = offset - historyStart;

      // Try LZMA compression with context
      using var lzmaData = new MemoryStream();
      var encoder = new LzmaEncoder(this._dictionarySize, level: this._level);
      encoder.Encode(lzmaData, contextAndChunk, chunkOffset, writeEndMarker: false);
      var compressed = lzmaData.ToArray();

      if (compressed.Length < chunkSize && compressed.Length <= Lzma2Encoder.MaxPackedSize) {
        // LZMA chunk — fits in the 16-bit packed size field
        WriteLzmaChunk(output, data.Slice(offset, chunkSize), compressed, encoder.Properties,
          needFullReset);
        needFullReset = false;
        offset += chunkSize;
      } else {
        // Emit as uncompressed 64KB blocks
        var remaining = chunkSize;
        while (remaining > 0) {
          var blockSize = Math.Min(Lzma2Encoder.MaxUncompressedChunkSize, remaining);
          WriteUncompressedChunk(output, data.Slice(offset, blockSize), needFullReset);
          needFullReset = false;
          offset += blockSize;
          remaining -= blockSize;
        }
      }
    }

    output.WriteByte(0x00); // End marker
  }

  /// <summary>
  /// Encodes data from a stream in LZMA2 format.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="input">The input stream.</param>
  /// <param name="length">The length to read, or -1 to read to end.</param>
  public void Encode(Stream output, Stream input, long length = -1) {
    byte[] data;
    if (length >= 0) {
      data = new byte[length];
      var totalRead = 0;
      while (totalRead < length) {
        var read = input.Read(data, totalRead, (int)(length - totalRead));
        if (read == 0) break;
        totalRead += read;
      }
      if (totalRead < length)
        Array.Resize(ref data, totalRead);
    } else {
      using var ms = new MemoryStream();
      input.CopyTo(ms);
      data = ms.ToArray();
    }

    this.Encode(output, data);
  }

  private static void WriteLzmaChunk(Stream output, ReadOnlySpan<byte> uncompressed,
    byte[] compressed, byte[] properties, bool needFullReset) {
    var unpackedSize = uncompressed.Length - 1; // 0-based
    var packedSize = compressed.Length - 1;     // 0-based

    // Control byte: 0x80 + reset bits
    // Bit 5-6: reset level (3=full reset with props)
    byte control;
    if (needFullReset)
      control = (byte)(0x80 | (3 << 5) | ((unpackedSize >> 16) & 0x1F));
    else
      // State reset (level 2 with properties for simplicity)
      control = (byte)(0x80 | (2 << 5) | ((unpackedSize >> 16) & 0x1F));

    output.WriteByte(control);
    output.WriteByte((byte)(unpackedSize >> 8));
    output.WriteByte((byte)unpackedSize);
    output.WriteByte((byte)(packedSize >> 8));
    output.WriteByte((byte)packedSize);

    // Write properties byte for reset level >= 2
    output.WriteByte(properties[0]);

    output.Write(compressed);
  }

  private static void WriteUncompressedChunk(Stream output, ReadOnlySpan<byte> data,
    bool needReset) {
    var size = data.Length - 1; // 0-based

    var control = needReset ? (byte)0x01 : (byte)0x02;
    output.WriteByte(control);
    output.WriteByte((byte)(size >> 8));
    output.WriteByte((byte)size);
    output.Write(data);
  }

  private static byte EncodeDictionarySize(int size) {
    if (size <= 4096)
      return 0;

    var bits = 31 - int.LeadingZeroCount(size);
    if (size == (1 << bits))
      return (byte)((bits - 12) * 2 + 1); // Use odd for exact powers of 2 offset

    // For exact powers of 2: result = (bits * 2) - 24 + 1
    // General formula
    var result = (byte)(bits * 2 - 24);
    if (size >= (3 << (bits - 1)))
      ++result;

    return result;
  }
}
