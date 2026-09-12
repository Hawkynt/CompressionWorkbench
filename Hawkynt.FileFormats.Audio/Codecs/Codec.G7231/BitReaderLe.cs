#pragma warning disable CS1591
namespace Codec.G7231;

/// <summary>
/// Little-endian bit reader matching FFmpeg's <c>get_bits.h</c> with <c>BITSTREAM_READER_LE</c>
/// (the mode the G.723.1 decoder compiles under): bits are consumed LSB-first within each byte and
/// bytes are consumed in stream order, so <c>get_bits(n)</c> yields the next <c>n</c> bits with the
/// first-read bit as the least-significant bit of the result. Reads past the end return zero bits,
/// matching the reference's tolerance of short buffers.
/// </summary>
internal sealed class BitReaderLe {

  private readonly ReadOnlyMemory<byte> _data;
  private int _bitPos;

  public BitReaderLe(ReadOnlySpan<byte> data) => this._data = data.ToArray();

  /// <summary>Reads <paramref name="count"/> bits (0..25 safe) LSB-first; missing bits read as 0.</summary>
  public int Get(int count) {
    var result = 0;
    var span = this._data.Span;
    for (var i = 0; i < count; ++i) {
      var byteIndex = this._bitPos >> 3;
      var bit = 0;
      if (byteIndex < span.Length)
        bit = (span[byteIndex] >> (this._bitPos & 7)) & 1;
      result |= bit << i;
      ++this._bitPos;
    }
    return result;
  }

  /// <summary>Skips <paramref name="count"/> bits.</summary>
  public void Skip(int count) => this._bitPos += count;
}
