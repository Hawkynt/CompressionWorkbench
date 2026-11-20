namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of <see cref="UharcLz78Decoder"/> (docs/UHARC-ON-DISK.md, "LZ78"): it keeps
/// the decoder's <see cref="UharcLz78Dictionary"/> step for step and codes, at each position, the
/// longest phrase the decoder already knows. It writes no runs and no resets; a full dictionary
/// is pruned exactly as the decoder prunes it.
/// </summary>
internal sealed class UharcLz78Encoder {

  private readonly MemoryStream _output = new();
  private ulong _bitBuffer;
  private int _bitCount;
  private uint _literalCredit;
  private readonly UharcLz78Dictionary _dictionary;

  // Child phrase by (parent code << 8 | byte), for the phrases the decoder knows.
  private readonly Dictionary<int, int> _children = [];

  private UharcLz78Encoder(int method) => this._dictionary = new(method);

  /// <summary>Packs <paramref name="data"/> with a <c>2^(method+8)</c>-phrase dictionary.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int method) {
    var encoder = new UharcLz78Encoder(method);
    encoder.Run(data);
    return encoder.Finish();
  }

  private void Run(ReadOnlySpan<byte> data) {
    var dictionary = this._dictionary;
    var position = 0;
    while (position < data.Length) {
      var code = (int)data[position];
      var length = 1;
      while (position + length < data.Length && this._children.TryGetValue(code << 8 | data[position + length], out var child)) {
        code = child;
        ++length;
      }

      this.WriteCode(code);
      var previous = dictionary.Previous;
      var next = dictionary.Next;
      var first = data[position];
      if (dictionary.Advance(code, first))
        this.RebuildChildren();
      else if (previous != UharcLz78Dictionary.None && next < dictionary.Capacity)
        this._children[previous << 8 | first] = next;

      position += length;
    }
  }

  private void RebuildChildren() {
    this._children.Clear();
    var dictionary = this._dictionary;
    for (var code = UharcLz78Dictionary.Roots; code <= dictionary.Next && code < dictionary.Capacity; ++code)
      this._children[dictionary.Parent(code) << 8 | dictionary.LastByte(code)] = code;
  }

  private void WriteCode(int code) {
    var width = this._dictionary.Width;
    if (this._literalCredit != 0 && width != 9) {
      if (code < 256) {
        this.Bits(0, 1);
        this.Bits((uint)code, 8);
        this._literalCredit += 15;
      } else {
        this.Bits(1, 1);
        this.Truncated(code - 256);
        this._literalCredit >>= 1;
      }

      return;
    }

    this.Truncated(code);
    if (code < 256 && width != 9)
      this._literalCredit = 1;
  }

  /// <summary>
  /// <paramref name="value"/> in truncated binary over the codes up to <see cref="UharcLz78Dictionary.Next"/>:
  /// <c>width − 1</c> bits, and for the values the decoder doubles up one more bit choosing the mirror.
  /// </summary>
  private void Truncated(int value) {
    var width = this._dictionary.Width;
    var span = 1 << width;
    var threshold = span - (this._dictionary.Next + 1);
    var half = span >> 1;
    if (value < half) {
      this.Bits((uint)value, width - 1);
      if (value + 1 >= threshold)
        this.Bits(0, 1);
    } else {
      this.Bits((uint)(span - 1 - value), width - 1);
      this.Bits(1, 1);
    }
  }

  private void Bits(uint value, int count) {
    this._bitBuffer = (this._bitBuffer << count) | (value & (uint)((1L << count) - 1));
    this._bitCount += count;
    while (this._bitCount >= 8) {
      this._bitCount -= 8;
      this._output.WriteByte((byte)(this._bitBuffer >> this._bitCount));
    }
  }

  private byte[] Finish() {
    if (this._bitCount > 0)
      this.Bits(0, 8 - this._bitCount);
    return this._output.ToArray();
  }
}
