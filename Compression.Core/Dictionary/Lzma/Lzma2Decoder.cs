using System.Buffers;
using Compression.Core.DataStructures;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA2 decoder that reads chunked LZMA2 format data.
/// Maintains a shared sliding window across chunks for cross-chunk back-references.
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

    // Reusable properties buffer (5 bytes, filled on resetLevel >= 2)
    byte[] properties = new byte[5];
    bool hasProperties = false;
    // Pre-fill dictionary size bytes (constant across chunks)
    properties[1] = (byte)this._dictionarySize;
    properties[2] = (byte)(this._dictionarySize >> 8);
    properties[3] = (byte)(this._dictionarySize >> 16);
    properties[4] = (byte)(this._dictionarySize >> 24);

    // Shared window persists across chunks for cross-chunk dictionary references
    int winSize = Math.Max(this._dictionarySize, 4096);
    var window = new SlidingWindow(winSize);
    int[] reps = [0, 0, 0, 0];

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

        byte[] uncompressed = ArrayPool<byte>.Shared.Rent(size);
        try {
          ReadExact(uncompressed, 0, size);
          output.Write(uncompressed, 0, size);
          window.WriteBytes(uncompressed.AsSpan(0, size));
        } finally {
          ArrayPool<byte>.Shared.Return(uncompressed);
        }

        if (controlByte == 0x01) {
          // Dictionary reset — reset window and rep distances
          window = new SlidingWindow(winSize);
          reps[0] = reps[1] = reps[2] = reps[3] = 0;
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
          properties[0] = (byte)ReadByte();
          hasProperties = true;
        }

        if (resetLevel >= 3) {
          // Full reset: reset window and rep distances
          window = new SlidingWindow(winSize);
          reps[0] = reps[1] = reps[2] = reps[3] = 0;
        } else if (resetLevel == 1) {
          // State reset but keep dictionary
          reps[0] = reps[1] = reps[2] = reps[3] = 0;
        }
        // resetLevel 0: continue with existing state (dictionary + reps preserved)

        if (!hasProperties)
          throw new InvalidDataException("LZMA2: No properties available for LZMA chunk.");

        // Read packed data
        byte[] packed = ArrayPool<byte>.Shared.Rent(packedSize);
        try {
          ReadExact(packed, 0, packedSize);
          using var packedStream = new MemoryStream(packed, 0, packedSize);
          var decoder = new LzmaDecoder(packedStream, properties, unpackedSize);
          decoder.Decode(output, window, reps);
        } finally {
          ArrayPool<byte>.Shared.Return(packed);
        }
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
