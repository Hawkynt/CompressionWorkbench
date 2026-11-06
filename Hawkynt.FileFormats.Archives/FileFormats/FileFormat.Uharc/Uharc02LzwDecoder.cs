namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.2's LZW coder (methods 2..7; the directory tables use 2 and 7), docs/UHARC-ON-DISK.md,
/// "UHARC 0.2". Plain LZW over a parent/byte dictionary of 2^(method+8) codes with
/// least-significant-bit-first truncated binary codes; code 256 restarts, code 257 is a run.
/// </summary>
internal sealed class Uharc02LzwDecoder {

  private const int Reset = 0x100;
  private const int RunCode = 0x101;
  private const int FirstPhrase = 0x102;

  private readonly byte[] _source;
  private readonly int _end;
  private int _position;
  private ulong _bits;
  private int _bitCount;

  private readonly ushort[] _parent;
  private readonly byte[] _byte;
  private readonly byte[] _scratch;
  private readonly int _capacity;
  private readonly int _maxWidth;

  private readonly byte[] _output;
  private int _written;

  private Uharc02LzwDecoder(byte[] source, int offset, int length, int method, int unpackedSize) {
    this._source = source;
    this._position = offset;
    this._end = offset + length;
    this._maxWidth = method + 8;
    this._capacity = 1 << this._maxWidth;
    this._parent = new ushort[this._capacity];
    this._byte = new byte[this._capacity];
    this._scratch = new byte[this._capacity + 1];
    for (var i = 0; i < 256; ++i)
      this._byte[i] = (byte)i;
    this._output = new byte[unpackedSize];
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes.</summary>
  public static byte[] Decode(byte[] source, int offset, int length, int method, int unpackedSize) {
    if (method is < 2 or > 7)
      throw new ArgumentOutOfRangeException(nameof(method));

    var decoder = new Uharc02LzwDecoder(source, offset, length, method, unpackedSize);
    if (unpackedSize > 0)
      decoder.Run();
    return decoder._output;
  }

  private uint Bits(int count) {
    while (this._bitCount < count) {
      if (this._position >= this._end)
        throw new InvalidDataException("UHARC: LZW stream ends early.");
      this._bits |= (ulong)this._source[this._position++] << this._bitCount;
      this._bitCount += 8;
    }

    var value = (uint)(this._bits & ((1UL << count) - 1));
    this._bits >>= count;
    this._bitCount -= count;
    return value;
  }

  /// <summary>A truncated binary code: width-1 bits, and near the top one more bit that mirrors the value.</summary>
  private int Code(int width, int issued) {
    var span = 1 << width;
    var value = (int)this.Bits(width - 1);
    var threshold = issued < span ? span - issued : 0;
    if (value + 1 >= threshold && this.Bits(1) != 0)
      value = span - 1 - value;
    return value;
  }

  private void Put(byte value) {
    if (this._written < this._output.Length)
      this._output[this._written++] = value;
  }

  private void Run() {
    var maxCode = this._capacity - 1;
    var width = 9;
    var next = FirstPhrase;
    var issued = FirstPhrase;
    var runPending = false;

    var previous = this.Code(width, issued);
    ++issued;
    this.Put((byte)previous);

    while (this._written < this._output.Length) {
      var code = this.Code(width, issued);
      if (code == Reset) {
        width = 9;
        next = FirstPhrase;
        issued = FirstPhrase;
        previous = this.Code(width, issued);
        ++issued;
        this.Put((byte)previous);
        continue;
      }

      if (code == RunCode) {
        var count = (int)(this.Bits(8) << 8);
        count |= (int)this.Bits(8);
        var value = (byte)this.Bits(8);
        if (!runPending) {
          this.Put((byte)this.Bits(8));
          runPending = true;
        }

        for (var i = 0; i < count && this._written < this._output.Length; ++i)
          this.Put(value);
        continue;
      }

      var current = code;
      var repeat = code >= next;
      if (repeat)
        code = previous;

      var length = 0;
      while (code >= FirstPhrase) {
        if (code >= this._capacity || length >= this._capacity)
          throw new InvalidDataException("UHARC: LZW code outside the dictionary.");
        this._scratch[length++] = this._byte[code];
        code = this._parent[code];
      }

      var first = (byte)code;
      this._scratch[length++] = first;
      var skip = runPending ? 1 : 0;
      runPending = false;
      for (var i = length - 1 - skip; i >= 0; --i)
        this.Put(this._scratch[i]);
      if (repeat)
        this.Put(first);

      if (next < this._capacity) {
        this._parent[next] = (ushort)previous;
        this._byte[next] = first;
      }

      ++next;
      if (next == 1 << width && width < this._maxWidth)
        ++width;
      previous = current;
      if (issued <= maxCode)
        ++issued;
    }
  }
}
