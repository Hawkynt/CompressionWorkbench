#pragma warning disable CS1591
namespace Codec.Siren;

/// <summary>
/// MSB-first bit reader matching FFmpeg's <c>get_bits</c> API as used by the Siren decoder:
/// <see cref="GetBit"/> ≙ <c>get_bits1</c>, <see cref="GetBits"/> ≙ <c>get_bits</c>,
/// <see cref="ShowBit"/> ≙ <c>show_bits1</c> (peek without consuming) and <see cref="BitsLeft"/>
/// ≙ <c>get_bits_left</c>. Reading past the end yields zero bits, mirroring the reference's
/// behaviour once the length checks in the decoder have been satisfied.
/// </summary>
internal ref struct SirenBitReader {
  private readonly ReadOnlySpan<byte> _data;
  private readonly int _sizeInBits;
  private int _position;

  public SirenBitReader(ReadOnlySpan<byte> data) {
    this._data = data;
    this._sizeInBits = data.Length * 8;
    this._position = 0;
  }

  /// <summary>Bits remaining (may go negative if the decoder over-reads, as in the reference).</summary>
  public readonly int BitsLeft => this._sizeInBits - this._position;

  /// <summary>Bits consumed so far (FFmpeg <c>get_bits_count</c>).</summary>
  public readonly int BitsCount => this._position;

  private readonly int PeekBit(int position) {
    var byteIndex = position >> 3;
    if ((uint)byteIndex >= (uint)this._data.Length)
      return 0;
    return (this._data[byteIndex] >> (7 - (position & 7))) & 1;
  }

  /// <summary>Reads a single bit (FFmpeg <c>get_bits1</c>).</summary>
  public int GetBit() {
    var bit = this.PeekBit(this._position);
    ++this._position;
    return bit;
  }

  /// <summary>Peeks the next bit without consuming it (FFmpeg <c>show_bits1</c>).</summary>
  public readonly int ShowBit() => this.PeekBit(this._position);

  /// <summary>Reads <paramref name="n"/> bits MSB-first (FFmpeg <c>get_bits</c>).</summary>
  public int GetBits(int n) {
    var value = 0;
    for (var i = 0; i < n; ++i)
      value = (value << 1) | this.GetBit();
    return value;
  }
}
