namespace FileFormat.Uharc;

/// <summary>
/// UHARC's LZ78 stream (methods 2..7, <c>-mw</c>), as described in docs/UHARC-ON-DISK.md section
/// "LZ78": an LZW dictionary of <c>2^(method+8)</c> phrases, MSB-first codes in truncated binary,
/// a literal/phrase flag once codes are wider than 9 bits, a code for long byte runs and a
/// pruning step when the dictionary fills.
/// </summary>
internal sealed class UharcLz78Decoder {

  private readonly byte[] _source;
  private readonly int _end;
  private int _position;
  private uint _bitBuffer;
  private int _bitCount;
  private readonly UharcLz78Dictionary _dictionary;

  private UharcLz78Decoder(byte[] source, int offset, int length, int method) {
    this._source = source;
    this._position = offset;
    this._end = offset + length;
    this._dictionary = new(method);
  }

  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize) {
    if (method is < UharcMethods.Lz78First or > UharcMethods.Lz78Last)
      throw new ArgumentOutOfRangeException(nameof(method));

    return new UharcLz78Decoder(packed, offset, length, method).Run(unpackedSize);
  }

  private uint Bits(int count) {
    if (count == 0)
      return 0;

    while (count > this._bitCount) {
      if (this._position >= this._end)
        throw new InvalidDataException("UHARC: LZ78 stream is truncated.");
      this._bitBuffer = (this._bitBuffer << 8) | this._source[this._position++];
      this._bitCount += 8;
    }

    this._bitCount -= count;
    return (this._bitBuffer >> this._bitCount) & (uint)((1L << count) - 1);
  }

  private byte[] Run(int unpackedSize) {
    var output = new byte[unpackedSize];
    var written = 0;
    var dictionary = this._dictionary;
    uint literalCredit = 0;

    while (written < unpackedSize) {
      // Codes are truncated binary over [0, next]: the top values carry one extra bit.
      int code;
      var width = dictionary.Width;
      var next = dictionary.Next;
      var span = 1 << width;
      var threshold = span - (next + 1);
      if (literalCredit != 0 && width != 9) {
        if (this.Bits(1) != 0) {
          var v = (int)this.Bits(width - 1);
          if (v + 1 >= threshold && this.Bits(1) != 0)
            v = span - 1 - v;
          literalCredit >>= 1;
          code = v + 256;
        } else {
          code = (int)this.Bits(8);
          literalCredit += 15;
        }
      } else {
        var v = (int)this.Bits(width - 1);
        if (v + 1 >= threshold && this.Bits(1) != 0)
          v = span - 1 - v;
        if (v < 256 && width != 9)
          literalCredit = 1;
        code = v;
      }

      if (code > next)
        continue;

      if (code == UharcLz78Dictionary.ResetCode) {
        dictionary.Reset();
        literalCredit = 0;
        continue;
      }

      if (code == UharcLz78Dictionary.RunCode) {
        long count;
        if (this.Bits(1) != 0)
          count = this.Bits(8);
        else if (this.Bits(1) != 0)
          count = this.Bits(12) + 0x100L;
        else if (this.Bits(1) != 0)
          count = this.Bits(16) + 0x1100L;
        else
          count = this.Bits(16) + ((long)this.Bits(16) << 16) + 0x1100L;
        count += 0x100;
        var value = (byte)this.Bits(8);
        if (count > unpackedSize - written)
          throw new InvalidDataException("UHARC: LZ78 run passes the end of the stream.");
        output.AsSpan(written, (int)count).Fill(value);
        written += (int)count;
        continue;
      }

      var phrase = dictionary.Expand(code);
      if (phrase.Length > unpackedSize - written)
        throw new InvalidDataException("UHARC: LZ78 phrase passes the end of the stream.");
      phrase.CopyTo(output.AsSpan(written));
      written += phrase.Length;
      dictionary.Advance(code, phrase[0]);
    }

    return output;
  }
}
