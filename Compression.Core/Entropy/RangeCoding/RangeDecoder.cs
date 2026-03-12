using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Compression.Core.Entropy.RangeCoding;

/// <summary>
/// LZMA-style byte-aligned range decoder with adaptive binary probabilities.
/// </summary>
public sealed class RangeDecoder {
  private const int NumBitModelTotalBits = 11;
  private const int BitModelTotal = 1 << RangeDecoder.NumBitModelTotalBits;
  private const int NumMoveBits = 5;
  private const uint TopValue = 1u << 24;

  private readonly Stream _input;
  private uint _code;
  private uint _range;

  /// <summary>
  /// Initializes a new <see cref="RangeDecoder"/> reading from the specified stream.
  /// </summary>
  /// <param name="input">The input stream.</param>
  public RangeDecoder(Stream input) {
    this._input = input ?? throw new ArgumentNullException(nameof(input));
    this._range = 0xFFFFFFFF;

    // Read initial 5 bytes: first byte should be 0x00, next 4 bytes form the initial code
    var firstByte = this._input.ReadByte();
    if (firstByte < 0)
      ThrowUnexpectedEof();

    for (var i = 0; i < 4; ++i) {
      var readByte = this._input.ReadByte();
      if (readByte < 0)
        ThrowUnexpectedEof();

      this._code = (this._code << 8) | (uint)readByte;
    }
  }

  /// <summary>
  /// Gets whether the stream is finished (no more data available).
  /// </summary>
  public bool IsFinished { get; private set; }

  /// <summary>
  /// Decodes a single bit using an adaptive probability model.
  /// </summary>
  /// <param name="prob">The probability variable (11-bit, 0..2047). Updated after decoding.</param>
  /// <returns>The decoded bit (0 or 1).</returns>
  public int DecodeBit(ref int prob) {
    var bound = (this._range >> RangeDecoder.NumBitModelTotalBits) * (uint)prob;

    if (this._code < bound) {
      this._range = bound;
      prob += (RangeDecoder.BitModelTotal - prob) >> RangeDecoder.NumMoveBits;
      this.Normalize();
      return 0;
    }
    else {
      this._code -= bound;
      this._range -= bound;
      prob -= prob >> RangeDecoder.NumMoveBits;
      this.Normalize();
      return 1;
    }
  }

  /// <summary>
  /// Decodes bits without probability adaptation (fixed 50/50 split).
  /// </summary>
  /// <param name="count">The number of bits to decode (MSB first).</param>
  /// <returns>The decoded value.</returns>
  public int DecodeDirectBits(int count) {
    var result = 0;
    for (var i = count - 1; i >= 0; --i) {
      this._range >>= 1;
      var threshold = (this._code - this._range) >> 31;
      this._code -= this._range & (threshold - 1);
      result = (result << 1) | (int)(1 - threshold);

      this.Normalize();
    }

    return result;
  }

  private void Normalize() {
    if (this._range >= RangeDecoder.TopValue)
      return;

    this._range <<= 8;
    var readByte = this._input.ReadByte();
    if (readByte < 0) {
      this.IsFinished = true;
      readByte = 0;
    }
    this._code = (this._code << 8) | (uint)readByte;
  }

  [DoesNotReturn][StackTraceHidden][MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowUnexpectedEof() => throw new EndOfStreamException("Unexpected end of range-coded stream.");
}
