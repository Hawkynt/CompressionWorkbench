namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// Writes bits in LZX's unique format: bits are accumulated MSB-first and flushed
/// as 16-bit little-endian words, mirroring the format that the LZX decompressor reads.
/// </summary>
internal sealed class LzxBitWriter {
  private readonly Stream _stream;
  private uint _buffer;
  private int _bitsUsed;

  /// <summary>
  /// Initializes a new <see cref="LzxBitWriter"/>.
  /// </summary>
  /// <param name="stream">The output stream to write to.</param>
  public LzxBitWriter(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
  }

  /// <summary>
  /// Writes the <paramref name="count"/> least-significant bits of <paramref name="value"/>
  /// to the bit stream, MSB first.
  /// </summary>
  /// <param name="value">The value to write.</param>
  /// <param name="count">Number of bits to write (0–32).</param>
  public void WriteBits(uint value, int count) {
    if (count == 0)
      return;

    // Mask to exactly 'count' bits
    var mask = count == 32 ? 0xFFFFFFFFu : (1u << count) - 1u;
    value &= mask;

    // Shift existing bits up and OR in the new ones (MSB-first accumulation)
    this._buffer = (this._buffer << count) | value;
    this._bitsUsed += count;

    // Flush complete 16-bit words as LE bytes
    while (this._bitsUsed >= 16) {
      this._bitsUsed -= 16;
      var word = (ushort)(this._buffer >> this._bitsUsed);
      this._stream.WriteByte((byte)(word & 0xFF));        // LE: low byte first
      this._stream.WriteByte((byte)((word >> 8) & 0xFF)); // then high byte
    }
  }

  /// <summary>
  /// Flushes any remaining buffered bits to the stream, padding to the next
  /// 16-bit word boundary with zero bits, then writes one additional zero word
  /// so the decompressor's lookahead never straddles the end of the byte stream.
  /// </summary>
  public void Flush() {
    if (this._bitsUsed > 0) {
      // Shift remaining bits to the top of a 16-bit word and write it
      var word = (ushort)(this._buffer << (16 - this._bitsUsed));
      this._stream.WriteByte((byte)(word & 0xFF));
      this._stream.WriteByte((byte)((word >> 8) & 0xFF));
      this._bitsUsed = 0;
      this._buffer = 0;
    }

    // Extra padding word ensures the decompressor can always fill its bit buffer
    // without hitting EOF during a symbol's table lookup.
    this._stream.WriteByte(0);
    this._stream.WriteByte(0);
  }
}
