namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA2 decoder that reads chunked LZMA2 format data.
/// </summary>
public sealed class Lzma2Decoder {
  private readonly Stream _input;
  private readonly int _dictionarySize;
  private bool _finished;

  /// <summary>
  /// Gets whether the stream has been fully decoded.
  /// </summary>
  public bool IsFinished => this._finished;

  /// <summary>
  /// Initializes a new LZMA2 decoder.
  /// </summary>
  /// <param name="input">The input stream containing LZMA2-encoded data.</param>
  /// <param name="dictionarySize">The dictionary size in bytes.</param>
  public Lzma2Decoder(Stream input, int dictionarySize) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._dictionarySize = dictionarySize;
  }

  /// <summary>
  /// Decodes the entire LZMA2 stream.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode() {
    using var output = new MemoryStream();

    byte[]? properties = null;

    while (!this._finished) {
      int controlByte = this._input.ReadByte();
      if (controlByte < 0)
        throw new EndOfStreamException("Unexpected end of LZMA2 stream.");

      if (controlByte == 0x00) {
        // End marker
        this._finished = true;
        break;
      }

      if (controlByte <= 0x02) {
        // Uncompressed chunk
        int size = (ReadByte() << 8) | ReadByte();
        ++size; // 0-based to actual size

        byte[] uncompressed = new byte[size];
        ReadExact(uncompressed, 0, size);
        output.Write(uncompressed, 0, size);

        if (controlByte == 0x01) {
          // Dictionary reset
          // (we don't maintain dictionary state across chunks in this implementation)
        }
      }
      else if ((controlByte & 0x80) != 0) {
        // LZMA chunk
        int resetLevel = (controlByte >> 5) & 0x03;
        int unpackedSizeHigh = controlByte & 0x1F;

        int unpackedSize = (unpackedSizeHigh << 16) | (ReadByte() << 8) | ReadByte();
        ++unpackedSize; // 0-based to actual size

        int packedSize = (ReadByte() << 8) | ReadByte();
        ++packedSize; // 0-based to actual size

        if (resetLevel >= 2) {
          // Read properties byte
          int propByte = ReadByte();
          properties = new byte[5];
          properties[0] = (byte)propByte;
          properties[1] = (byte)this._dictionarySize;
          properties[2] = (byte)(this._dictionarySize >> 8);
          properties[3] = (byte)(this._dictionarySize >> 16);
          properties[4] = (byte)(this._dictionarySize >> 24);
        }

        if (properties == null)
          throw new InvalidDataException("LZMA2: No properties available for LZMA chunk.");

        // Read packed data
        byte[] packed = new byte[packedSize];
        ReadExact(packed, 0, packedSize);

        using var packedStream = new MemoryStream(packed);
        var decoder = new LzmaDecoder(packedStream, properties, unpackedSize);
        byte[] decoded = decoder.Decode();
        output.Write(decoded, 0, decoded.Length);
      }
      else
        throw new InvalidDataException($"Invalid LZMA2 control byte: 0x{controlByte:X2}");
    }

    return output.ToArray();
  }

  private int ReadByte() {
    int b = this._input.ReadByte();
    if (b < 0)
      throw new EndOfStreamException("Unexpected end of LZMA2 stream.");

    return b;
  }

  private void ReadExact(byte[] buffer, int offset, int count) {
    int totalRead = 0;
    while (totalRead < count) {
      int read = this._input.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of LZMA2 stream.");

      totalRead += read;
    }
  }
}
