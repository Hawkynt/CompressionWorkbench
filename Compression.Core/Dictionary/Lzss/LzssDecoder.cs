using Compression.Core.DataStructures;

namespace Compression.Core.Dictionary.Lzss;

/// <summary>
/// Decodes LZSS flag-bit encoded data from a stream.
/// </summary>
public sealed class LzssDecoder {
  private readonly Stream _input;
  private readonly int _distanceBits;
  private readonly int _lengthBits;
  private readonly int _minMatchLength;
  private readonly int _windowSize;

  /// <summary>
  /// Initializes a new <see cref="LzssDecoder"/>.
  /// </summary>
  /// <param name="input">The stream to read encoded data from.</param>
  /// <param name="distanceBits">Number of bits for the distance field. Defaults to 12.</param>
  /// <param name="lengthBits">Number of bits for the length field. Defaults to 4.</param>
  /// <param name="minMatchLength">Minimum match length (added to stored length). Defaults to 3.</param>
  public LzssDecoder(Stream input, int distanceBits = 12, int lengthBits = 4, int minMatchLength = 3) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._distanceBits = distanceBits;
    this._lengthBits = lengthBits;
    this._minMatchLength = minMatchLength;
    this._windowSize = 1 << distanceBits;
  }

  /// <summary>
  /// Decodes data from the input stream.
  /// </summary>
  /// <param name="expectedLength">The expected number of decompressed bytes, or -1 to read until end of stream.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode(int expectedLength = -1) {
    var window = new SlidingWindow(this._windowSize);
    var output = new List<byte>();

    while (expectedLength < 0 || output.Count < expectedLength) {
      var flagByte = this._input.ReadByte();
      if (flagByte < 0)
        break;

      for (var bit = 0; bit < 8; ++bit) {
        if (expectedLength >= 0 && output.Count >= expectedLength)
          break;

        if ((flagByte & (1 << bit)) != 0) {
          // Literal
          var readByte = this._input.ReadByte();
          if (readByte < 0)
            return [.. output];

          output.Add((byte)readByte);
          window.WriteByte((byte)readByte);
        } else {
          // Match
          var b1 = this._input.ReadByte();
          var b2 = this._input.ReadByte();
          if (b1 < 0 || b2 < 0)
            return [.. output];

          var encodedDistance = (b1 << (this._distanceBits - 8)) | (b2 >> this._lengthBits);
          var encodedLength = b2 & ((1 << this._lengthBits) - 1);

          var distance = encodedDistance + 1; // Convert from 0-based to 1-based
          var length = encodedLength + this._minMatchLength;

          if (distance > window.Count)
            // If distance exceeds available data, emit zeros
            for (var i = 0; i < length; ++i) {
              output.Add(0);
              window.WriteByte(0);
            }
          else {
            var copyBuf = new byte[length];
            window.CopyFromWindow(distance, length, copyBuf);
            output.AddRange(copyBuf);
          }
        }
      }
    }

    return [.. output];
  }
}
