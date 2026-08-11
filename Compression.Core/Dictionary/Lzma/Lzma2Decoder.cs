using System.Buffers;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA2 decoder that reads chunked LZMA2 format data.
/// </summary>
/// <remarks>
/// <para>
/// LZMA2 wraps LZMA1 in chunks that each start with a control byte
/// (see the .xz file format specification, version 1.2.0, section 5.3.1 for the filter and
/// its dictionary-size property; the chunk layout is the LZMA2 encoding shared by xz Utils
/// and the LZMA SDK):
/// </para>
/// <list type="bullet">
///   <item><description>0x00 — end of the LZMA2 stream.</description></item>
///   <item><description>0x01 — uncompressed chunk, resets the dictionary first.</description></item>
///   <item><description>0x02 — uncompressed chunk, dictionary continues.</description></item>
///   <item><description>0x80..0xFF — LZMA chunk. Bits 5-6 hold the reset level, bits 0-4 the
///     high bits of the 21-bit unpacked size.</description></item>
/// </list>
/// <para>The reset level of an LZMA chunk is cumulative:</para>
/// <list type="bullet">
///   <item><description>0 — reset nothing: probabilities, state machine, rep distances,
///     properties and dictionary all continue from the previous chunk.</description></item>
///   <item><description>1 — reset the probability model, the state machine and the rep
///     distances; keep the properties and the dictionary.</description></item>
///   <item><description>2 — as level 1, and read a new properties byte.</description></item>
///   <item><description>3 — as level 2, and reset the dictionary.</description></item>
/// </list>
/// <para>
/// Regardless of the level, every chunk is an independent range-coded unit (which is what
/// makes its packed size known up front), so the range decoder always restarts, and the
/// dictionary always survives unless a dictionary reset is requested — matches reach back
/// into earlier chunks.
/// </para>
/// </remarks>
public sealed class Lzma2Decoder {
  private readonly Stream _input;
  private readonly int _dictionarySize;

  /// <summary>
  /// Gets whether the stream has been fully decoded.
  /// </summary>
  public bool IsFinished { get; private set; }

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

    // One LZMA decoder for the whole stream: it owns the dictionary, the probability model,
    // the state machine, the rep distances and the uncompressed position counter, and each
    // chunk resets exactly the parts its control byte asks for.
    var lzma = new LzmaDecoder(this._dictionarySize);

    var hasProperties = false;
    var needDictionaryReset = true;
    var needStateReset = true;

    while (!this.IsFinished) {
      var controlByte = this._input.ReadByte();
      if (controlByte < 0)
        throw new EndOfStreamException("Unexpected end of LZMA2 stream.");

      if (controlByte == 0x00) {
        // End marker
        this.IsFinished = true;
        break;
      }

      if (controlByte <= 0x02) {
        // Uncompressed chunk — 0x01 resets the dictionary, 0x02 continues it
        if (controlByte == 0x01) {
          lzma.ResetDictionary();
          needDictionaryReset = false;
        } else if (needDictionaryReset)
          throw new InvalidDataException("LZMA2: The first chunk must reset the dictionary.");

        var size = (this.ReadByte() << 8) | this.ReadByte();
        ++size; // 0-based to actual size

        var uncompressed = ArrayPool<byte>.Shared.Rent(size);
        try {
          this.ReadExact(uncompressed, 0, size);
          lzma.WriteUncompressed(output, uncompressed.AsSpan(0, size));
        } finally {
          ArrayPool<byte>.Shared.Return(uncompressed);
        }

        // Raw bytes carry no coder state, so the next LZMA chunk has to reset it.
        needStateReset = true;
        continue;
      }

      if ((controlByte & 0x80) == 0)
        throw new InvalidDataException($"Invalid LZMA2 control byte: 0x{controlByte:X2}");

      // LZMA chunk
      var resetLevel = (controlByte >> 5) & 0x03;
      var unpackedSizeHigh = controlByte & 0x1F;

      var unpackedSize = (unpackedSizeHigh << 16) | (this.ReadByte() << 8) | this.ReadByte();
      ++unpackedSize; // 0-based to actual size

      var packedSize = (this.ReadByte() << 8) | this.ReadByte();
      ++packedSize; // 0-based to actual size

      if (resetLevel >= 2) {
        lzma.ApplyProperties((byte)this.ReadByte());
        hasProperties = true;
      } else if (!hasProperties)
        throw new InvalidDataException("LZMA2: No properties available for LZMA chunk.");

      if (resetLevel >= 1) {
        lzma.ResetState();
        needStateReset = false;
      } else if (needStateReset)
        throw new InvalidDataException("LZMA2: Chunk continues a coder state that was never initialised.");

      if (resetLevel >= 3) {
        lzma.ResetDictionary();
        needDictionaryReset = false;
      } else if (needDictionaryReset)
        throw new InvalidDataException("LZMA2: The first chunk must reset the dictionary.");

      // Read packed data — every chunk is its own range-coded unit
      var packed = ArrayPool<byte>.Shared.Rent(packedSize);
      try {
        this.ReadExact(packed, 0, packedSize);
        using var packedStream = new MemoryStream(packed, 0, packedSize);
        lzma.DecodeChunk(packedStream, output, unpackedSize);
      } finally {
        ArrayPool<byte>.Shared.Return(packed);
      }
    }

    return output.ToArray();
  }

  private int ReadByte() {
    var b = this._input.ReadByte();
    return b < 0 ? throw new EndOfStreamException("Unexpected end of LZMA2 stream.") : b;

  }

  private void ReadExact(byte[] buffer, int offset, int count) {
    var totalRead = 0;
    while (totalRead < count) {
      var read = this._input.Read(buffer, offset + totalRead, count - totalRead);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of LZMA2 stream.");

      totalRead += read;
    }
  }
}
