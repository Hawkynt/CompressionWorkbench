namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA2 encoder that wraps LZMA data in chunked format with control bytes.
/// </summary>
public sealed class Lzma2Encoder {
  private const int MaxUncompressedChunkSize = 1 << 21; // 2 MB
  private readonly int _dictionarySize;

  /// <summary>
  /// Gets the encoded dictionary size byte for XZ headers.
  /// </summary>
  public byte DictionarySizeByte { get; }

  /// <summary>
  /// Initializes a new LZMA2 encoder.
  /// </summary>
  /// <param name="dictionarySize">The dictionary size in bytes.</param>
  public Lzma2Encoder(int dictionarySize = 1 << 23) {
    this._dictionarySize = dictionarySize;
    DictionarySizeByte = EncodeDictionarySize(dictionarySize);
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

    int offset = 0;
    bool needFullReset = true;

    while (offset < data.Length) {
      int chunkSize = Math.Min(MaxUncompressedChunkSize, data.Length - offset);
      ReadOnlySpan<byte> chunk = data.Slice(offset, chunkSize);

      // Try LZMA compression
      using var lzmaData = new MemoryStream();
      var encoder = new LzmaEncoder(this._dictionarySize);
      encoder.Encode(lzmaData, chunk, writeEndMarker: false);
      byte[] compressed = lzmaData.ToArray();

      if (compressed.Length < chunkSize) {
        // LZMA chunk
        WriteLzmaChunk(output, chunk, compressed, encoder.Properties,
          needFullReset);
      }
      else {
        // Uncompressed chunk (LZMA didn't help)
        WriteUncompressedChunk(output, chunk, needFullReset);
      }

      needFullReset = false;
      offset += chunkSize;
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
      int totalRead = 0;
      while (totalRead < length) {
        int read = input.Read(data, totalRead, (int)(length - totalRead));
        if (read == 0) break;
        totalRead += read;
      }
      if (totalRead < length)
        Array.Resize(ref data, totalRead);
    }
    else {
      using var ms = new MemoryStream();
      input.CopyTo(ms);
      data = ms.ToArray();
    }

    Encode(output, data);
  }

  private static void WriteLzmaChunk(Stream output, ReadOnlySpan<byte> uncompressed,
    byte[] compressed, byte[] properties, bool needFullReset) {
    int unpackedSize = uncompressed.Length - 1; // 0-based
    int packedSize = compressed.Length - 1;     // 0-based

    // Control byte: 0x80 + reset bits
    // Bit 5-6: reset level (3=full reset with props)
    byte control;
    if (needFullReset)
      control = (byte)(0x80 | (3 << 5) | ((unpackedSize >> 16) & 0x1F));
    else {
      // State reset (level 2 with properties for simplicity)
      control = (byte)(0x80 | (2 << 5) | ((unpackedSize >> 16) & 0x1F));
    }

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
    int size = data.Length - 1; // 0-based

    byte control = needReset ? (byte)0x01 : (byte)0x02;
    output.WriteByte(control);
    output.WriteByte((byte)(size >> 8));
    output.WriteByte((byte)size);
    output.Write(data);
  }

  private static byte EncodeDictionarySize(int size) {
    if (size <= 4096)
      return 0;

    int bits = 31 - int.LeadingZeroCount(size);
    if (size == (1 << bits))
      return (byte)((bits - 12) * 2 + 1); // Use odd for exact powers of 2 offset

    // For exact powers of 2: result = (bits * 2) - 24 + 1
    // General formula
    byte result = (byte)(bits * 2 - 24);
    if (size >= (3 << (bits - 1)))
      ++result;

    return result;
  }
}
