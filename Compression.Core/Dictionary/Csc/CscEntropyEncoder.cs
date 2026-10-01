using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// The two coders of a libcsc segment: an LZMA-style binary range coder with 12-bit probabilities
/// (shift 5 adaptation, carry propagation through a cache byte) and a plain MSB-first bit packer for
/// bits that are not worth modelling. Each fills its own buffer, which goes out as a block whenever
/// it is full and at every <see cref="Flush"/>.
/// </summary>
internal sealed class CscEntropyEncoder {
  private const uint TopValue = 1u << 24;

  private readonly CscBlockWriter _writer;
  private readonly byte[] _rangeBuffer;
  private readonly byte[] _bitBuffer;

  private ulong _low;
  private uint _range;
  private ulong _cacheSize;
  private byte _cache;
  private int _rangeCount;

  private uint _bitValue;
  private int _bitCount;
  private int _bitBufferCount;

  public CscEntropyEncoder(CscBlockWriter writer) {
    this._writer = writer;
    this._rangeBuffer = new byte[writer.BlockSize];
    this._bitBuffer = new byte[writer.BlockSize];
    this.Reset();
  }

  /// <summary>Total bytes written to the stream so far, including the buffered ones.</summary>
  public long CompressedSize { get; private set; }

  /// <summary>Codes <paramref name="bit"/> with probability-of-one <paramref name="probability"/> and adapts it.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void EncodeBit(uint bit, ref uint probability) {
    var bound = (this._range >> 12) * probability;
    if (bit != 0) {
      this._range = bound;
      probability += (0xFFF - probability) >> 5;
    } else {
      this._low += bound;
      this._range -= bound;
      probability -= probability >> 5;
    }

    if (this._range < TopValue) {
      this._range <<= 8;
      this.ShiftLow();
    }
  }

  /// <summary>Writes the low <paramref name="bits"/> bits of <paramref name="value"/> (up to 32) to the bit coder.</summary>
  public void EncodeDirect(uint value, int bits) {
    if (bits <= 16)
      this.EncodeDirect16(value, bits);
    else {
      this.EncodeDirect16(value >> 16, bits - 16);
      this.EncodeDirect16(value & 0xFFFF, 16);
    }
  }

  /// <summary>Ends the segment: drains both coders, writes their final blocks and resets them.</summary>
  public void Flush() {
    for (var i = 0; i < 5; ++i)
      this.ShiftLow();

    // One more byte keeps the decoder's look-ahead inside this segment. libcsc leaves whatever its
    // buffer held there; it is never decoded, so a zero is written.
    this.PutRangeByte(0, canFlushBlock: false);

    // The partial bit-coder byte, then a zero guard byte for the same reason.
    this.PutBitByte((byte)(this._bitValue << (8 - this._bitCount)));
    this.PutBitByte(0);

    this.CompressedSize += this._rangeCount + this._bitBufferCount;
    this._writer.WriteRangeCoderBlock(this._rangeBuffer.AsSpan(0, this._rangeCount));
    this._writer.WriteBitCoderBlock(this._bitBuffer.AsSpan(0, this._bitBufferCount));
    this.Reset();
  }

  private void Reset() {
    this._low = 0;
    this._range = 0xFFFFFFFF;
    this._cacheSize = 1;
    this._cache = 0;
    this._rangeCount = 0;
    this._bitValue = 0;
    this._bitCount = 0;
    this._bitBufferCount = 0;
  }

  private void EncodeDirect16(uint value, int bits) {
    this._bitValue = (this._bitValue << bits) | value;
    this._bitCount += bits;
    while (this._bitCount >= 8) {
      this._bitCount -= 8;
      this.PutBitByte((byte)(this._bitValue >> this._bitCount));
    }
  }

  private void PutBitByte(byte value) {
    this._bitBuffer[this._bitBufferCount] = value;
    if (++this._bitBufferCount < this._bitBuffer.Length)
      return;

    this.CompressedSize += this._bitBufferCount;
    this._writer.WriteBitCoderBlock(this._bitBuffer);
    this._bitBufferCount = 0;
  }

  private void ShiftLow() {
    // Bytes stay pending in (cache, cacheSize) until it is known whether a carry still reaches them.
    if ((uint)this._low < 0xFF000000u || (this._low >> 32) != 0) {
      var carry = (byte)(this._low >> 32);
      var pending = this._cache;
      do {
        this.PutRangeByte((byte)(pending + carry), canFlushBlock: true);
        pending = 0xFF;
      } while (--this._cacheSize != 0);
      this._cache = (byte)((uint)this._low >> 24);
    }

    ++this._cacheSize;
    this._low = (uint)this._low << 8;
  }

  private void PutRangeByte(byte value, bool canFlushBlock) {
    this._rangeBuffer[this._rangeCount] = value;
    if (++this._rangeCount < this._rangeBuffer.Length || !canFlushBlock)
      return;

    this.CompressedSize += this._rangeCount;
    this._writer.WriteRangeCoderBlock(this._rangeBuffer);
    this._rangeCount = 0;
  }
}
