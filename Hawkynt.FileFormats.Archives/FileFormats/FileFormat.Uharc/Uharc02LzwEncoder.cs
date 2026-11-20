namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of <see cref="Uharc02LzwDecoder"/>: plain LZW over the decoder's dictionary,
/// coding at each position the longest phrase the decoder already knows, in the decoder's
/// least-significant-bit-first truncated binary codes. It writes no runs. A full dictionary is
/// restarted with the reset code: UHARC 0.4's own extractor fails when coding goes on past it.
/// </summary>
internal sealed class Uharc02LzwEncoder {

  private const int FirstPhrase = 0x102;
  private const int Reset = 0x100;

  private readonly MemoryStream _output = new();
  private ulong _bits;
  private int _bitCount;

  private Uharc02LzwEncoder() { }

  /// <summary>Packs <paramref name="data"/> with a <c>2^(method+8)</c>-code dictionary (methods 2..7).</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int method) {
    if (method is < 2 or > 7)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.IsEmpty)
      return [];

    var encoder = new Uharc02LzwEncoder();
    var maxWidth = method + 8;
    var capacity = 1 << maxWidth;
    var children = new Dictionary<int, int>();
    var width = 9;
    var next = FirstPhrase;
    var issued = FirstPhrase;

    // The first code is a plain byte; it extends nothing.
    encoder.Code(data[0], width, issued);
    ++issued;
    var previous = (int)data[0];
    var position = 1;
    while (position < data.Length) {
      var code = (int)data[position];
      var length = 1;
      while (position + length < data.Length && children.TryGetValue(code << 8 | data[position + length], out var child)) {
        code = child;
        ++length;
      }

      encoder.Code(code, width, issued);
      if (next < capacity)
        children[previous << 8 | data[position]] = next;

      ++next;
      if (next == 1 << width && width < maxWidth)
        ++width;
      previous = code;
      if (issued <= capacity - 1)
        ++issued;
      position += length;

      // A full dictionary restarts: the reset code, then a plain byte.
      if (next >= capacity && position < data.Length) {
        encoder.Code(Reset, width, issued);
        children.Clear();
        width = 9;
        next = FirstPhrase;
        issued = FirstPhrase;
        encoder.Code(data[position], width, issued);
        ++issued;
        previous = data[position];
        ++position;
      }
    }

    return encoder.Finish();
  }

  /// <summary><paramref name="value"/> as the decoder reads it: width-1 bits, and near the top one more bit choosing the mirror.</summary>
  private void Code(int value, int width, int issued) {
    var span = 1 << width;
    var threshold = issued < span ? span - issued : 0;
    var half = span >> 1;
    if (value < half) {
      this.Bits((uint)value, width - 1);
      if (value + 1 >= threshold)
        this.Bits(0, 1);
    } else {
      var mirror = span - 1 - value;
      if (mirror + 1 < threshold)
        throw new InvalidOperationException("UHARC: LZW code outside the codes issued.");
      this.Bits((uint)mirror, width - 1);
      this.Bits(1, 1);
    }
  }

  private void Bits(uint value, int count) {
    this._bits |= (ulong)value << this._bitCount;
    this._bitCount += count;
    while (this._bitCount >= 8) {
      this._output.WriteByte((byte)this._bits);
      this._bits >>= 8;
      this._bitCount -= 8;
    }
  }

  private byte[] Finish() {
    if (this._bitCount > 0)
      this._output.WriteByte((byte)this._bits);
    return this._output.ToArray();
  }
}
