namespace Compression.Core.Dictionary.Lzss;

/// <summary>
/// Encodes LZ77 tokens to a stream using LZSS flag-bit format.
/// Each group of 8 tokens is preceded by a flag byte where each bit indicates
/// whether the corresponding token is a literal (1) or a match (0).
/// </summary>
public sealed class LzssEncoder {
  private readonly Stream _output;
  private readonly int _distanceBits;
  private readonly int _lengthBits;
  private readonly int _minMatchLength;

  /// <summary>
  /// Initializes a new <see cref="LzssEncoder"/>.
  /// </summary>
  /// <param name="output">The stream to write encoded data to.</param>
  /// <param name="distanceBits">Number of bits for the distance field. Defaults to 12.</param>
  /// <param name="lengthBits">Number of bits for the length field. Defaults to 4.</param>
  /// <param name="minMatchLength">Minimum match length (subtracted from stored length). Defaults to 3.</param>
  public LzssEncoder(Stream output, int distanceBits = 12, int lengthBits = 4, int minMatchLength = 3) {
    this._output = output ?? throw new ArgumentNullException(nameof(output));
    this._distanceBits = distanceBits;
    this._lengthBits = lengthBits;
    this._minMatchLength = minMatchLength;
  }

  /// <summary>
  /// Gets the maximum match distance supported by the current configuration.
  /// </summary>
  public int MaxDistance => (1 << this._distanceBits);

  /// <summary>
  /// Gets the maximum match length supported by the current configuration.
  /// </summary>
  public int MaxLength => (1 << this._lengthBits) - 1 + this._minMatchLength;

  /// <summary>
  /// Encodes input data to the output stream.
  /// </summary>
  /// <param name="data">The input data to compress.</param>
  /// <param name="matchFinder">The match finder to use.</param>
  public void Encode(ReadOnlySpan<byte> data, MatchFinders.IMatchFinder matchFinder) {
    int position = 0;
    var flagBuffer = new byte[1 + 8 * 3]; // flag byte + up to 8 entries (max 3 bytes each)
    int flagBufPos;
    byte flags;
    int flagBit;

    while (position < data.Length) {
      flags = 0;
      flagBit = 0;
      flagBufPos = 1; // Leave room for flag byte at index 0

      while (flagBit < 8 && position < data.Length) {
        var match = matchFinder.FindMatch(data, position, MaxDistance, MaxLength, this._minMatchLength);

        if (match.Length >= this._minMatchLength) {
          // Match: flag bit = 0 (already 0)
          int encodedDistance = match.Distance - 1; // 0-based
          int encodedLength = match.Length - this._minMatchLength;

          // Write distance (high byte first) and length
          flagBuffer[flagBufPos++] = (byte)(encodedDistance >> (this._distanceBits - 8));
          flagBuffer[flagBufPos++] = (byte)((encodedDistance & ((1 << (this._distanceBits - 8)) - 1)) << this._lengthBits | (encodedLength & ((1 << this._lengthBits) - 1)));

          // Insert skipped positions
          if (matchFinder is MatchFinders.HashChainMatchFinder hcmf) {
            for (int i = 1; i < match.Length; ++i)
              hcmf.InsertPosition(data, position + i);
          }

          position += match.Length;
        }
        else {
          // Literal: flag bit = 1
          flags |= (byte)(1 << flagBit);
          flagBuffer[flagBufPos++] = data[position];
          ++position;
        }

        ++flagBit;
      }

      flagBuffer[0] = flags;
      this._output.Write(flagBuffer, 0, flagBufPos);
    }
  }
}
