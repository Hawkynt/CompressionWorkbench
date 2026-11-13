namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt method 14 (written by StuffIt's installer maker): LZ77 over a 256 KiB window, in
/// blocks, each with two Huffman codes, read least-significant bit first.
/// </summary>
/// <remarks>
/// <para>
/// The stream starts with a 16-bit block count. Each block holds:
/// <list type="bullet">
///   <item>its packed size (32 bits, skipped) and its unpacked size (32 bits, low half first);</item>
///   <item>a 308-symbol literal/length code and a 75-symbol distance code;</item>
///   <item>the symbols, then padding to a byte boundary.</item>
/// </list>
/// Symbols below 256 are literals. Symbol 256 + s is a match whose length is base[s] + 4 plus
/// extra bits, with base and extra bits following a 4-per-class doubling. Its distance code
/// d gives base[d] plus extra bits the same way, starting from 1.
/// </para>
/// <para>
/// Each code is transmitted as a header (a flag, the bit width w − 2 of each length, the
/// minimum length − 1, and a 2-bit selector) followed by code lengths. The lengths are either
/// read w bits at a time or decoded with a nested code of 2^w symbols sent the same way. One
/// value marks a zero length, the largest value starts a repeat of the previous length, and every
/// other value is a length. Codes are assigned in order of length (a quicksort of the lengths
/// with their symbols decides ties) and are bit-reversed for LSB-first reading.
/// </para>
/// <para>Converted from XADMaster's XADStuffItOldHandles (libxad's StuffIt client by Dirk Stöcker;
/// LGPL-2.1-or-later).</para>
/// </remarks>
internal sealed class StuffIt14Decoder {
  private const int WindowSize = 0x40000;

  private readonly StuffItBitReader _reader;
  private readonly byte[] _code = new byte[308];
  private readonly byte[] _codeCopy = new byte[308];
  private readonly ushort[] _freq = new ushort[308];
  private readonly uint[] _buff = new uint[308];
  private readonly byte[] _lengthExtra = new byte[52];
  private readonly ushort[] _lengthBase = new ushort[52];
  private readonly ushort[] _distanceTree = new ushort[75 * 2];
  private readonly byte[] _distanceExtra = new byte[76];
  private readonly uint[] _distanceBase = new uint[75];
  private readonly ushort[] _symbolTree = new ushort[308 * 2];

  private StuffIt14Decoder(byte[] input) {
    this._reader = new StuffItBitReader(input);
    for (int i = 0, k = 0; i < 52; ++i) {
      this._lengthBase[i] = (ushort)k;
      this._lengthExtra[i] = (byte)(i >= 4 ? (i - 4) >> 2 : 0);
      k += 1 << this._lengthExtra[i];
    }
    for (int i = 0, k = 1; i < 75; ++i) {
      this._distanceBase[i] = (uint)k;
      this._distanceExtra[i] = (byte)(i >= 3 ? (i - 3) >> 2 : 0);
      k += 1 << this._distanceExtra[i];
    }
  }

  public static byte[] Decode(byte[] input, int outputLength) {
    var output = new byte[outputLength];
    if (outputLength == 0) return output;
    new StuffIt14Decoder(input).Run(output);
    return output;
  }

  private uint Bits(int count) => (uint)this._reader.ReadBitsLe(count);

  private void Run(byte[] output) {
    var window = new byte[WindowSize];
    var o = 0;
    var blocks = this.Bits(16);
    var w = 0;
    while (blocks-- > 0 && o < output.Length) {
      this.Bits(16); // packed block size, low and high halves
      this.Bits(16);
      var n = this.Bits(16);
      n |= this.Bits(16) << 16;
      this.ReadTree(308, this._symbolTree);
      this.ReadTree(75, this._distanceTree);

      while (n > 0 && o < output.Length) {
        uint i = 0;
        while (i < 616) i = this._symbolTree[i + this.Bits(1)];
        i -= 616;
        if (i < 0x100) {
          output[o++] = window[w++] = (byte)i;
          w &= WindowSize - 1;
          --n;
          continue;
        }
        i -= 0x100;
        if (i >= 52) throw new InvalidDataException("StuffIt method 14 length code is out of range.");
        var length = this._lengthBase[i] + 4u;
        if (this._lengthExtra[i] != 0) length += this.Bits(this._lengthExtra[i]);
        uint d = 0;
        while (d < 150) d = this._distanceTree[d + this.Bits(1)];
        d -= 150;
        if (d >= 75) throw new InvalidDataException("StuffIt method 14 distance code is out of range.");
        var distance = this._distanceBase[d];
        if (this._distanceExtra[d] != 0) distance += this.Bits(this._distanceExtra[d]);
        n -= length;
        var from = (uint)(w + WindowSize - distance);
        while (length-- > 0 && o < output.Length) {
          from &= WindowSize - 1;
          output[o++] = window[w++] = window[from++];
          w &= WindowSize - 1;
        }
      }
      this._reader.AlignToByte();
    }
    if (o != output.Length) throw new InvalidDataException("StuffIt method 14 data ended before the recorded size.");
  }

  /// <summary>Quicksort of code lengths with their symbols; the tie order decides the codes, so it is kept exactly.</summary>
  private static void Sort(int first, int last, byte[] code, ushort[] freq) {
    while (last - first > 1) {
      var i = first;
      var j = last;
      do {
        while (++i < last && code[first] > code[i]) { }
        while (--j > first && code[first] < code[j]) { }
        if (j > i) {
          (code[i], code[j]) = (code[j], code[i]);
          (freq[i], freq[j]) = (freq[j], freq[i]);
        }
      } while (j > i);

      if (first != j) {
        (code[first], code[j]) = (code[j], code[first]);
        (freq[first], freq[j]) = (freq[j], freq[first]);
        i = j + 1;
        if (last - i <= j - first) {
          Sort(i, last, code, freq);
          last = j;
        } else {
          Sort(first, j, code, freq);
          first = i;
        }
      } else ++first;
    }
  }

  private void ReadTree(int codeSize, ushort[] result) {
    var k = this.Bits(1);
    var width = (int)this.Bits(2) + 2;
    var minimum = this.Bits(3) + 1;
    var size = 1u << width;
    var repeat = size - 1;
    var zero = k != 0 ? repeat - 1 : uint.MaxValue;
    var code = this._code;
    if ((this.Bits(2) & 1) != 0) {
      // the lengths are themselves Huffman coded, with a nested code of 2^width symbols
      this.ReadTree((int)size, this._freq);
      for (var i = 0; i < codeSize;) {
        var l = this.NestedSymbol(size);
        if (l == zero) code[i++] = 0;
        else if (l == repeat) {
          var count = this.NestedSymbol(size) + 3;
          while (count-- > 0) {
            if (i == 0 || i >= codeSize) throw new InvalidDataException("StuffIt method 14 code-length repeat is out of range.");
            code[i] = code[i - 1];
            ++i;
          }
        } else code[i++] = (byte)(l + minimum);
      }
    } else {
      for (var i = 0; i < codeSize;) {
        var l = this.Bits(width);
        if (l == zero) code[i++] = 0;
        else if (l == repeat) {
          var count = this.Bits(width) + 3;
          while (count-- > 0) {
            if (i == 0 || i >= codeSize) throw new InvalidDataException("StuffIt method 14 code-length repeat is out of range.");
            code[i] = code[i - 1];
            ++i;
          }
        } else code[i++] = (byte)(l + minimum);
      }
    }

    for (var i = 0; i < codeSize; ++i) {
      this._codeCopy[i] = code[i];
      this._freq[i] = (ushort)i;
    }
    Sort(0, codeSize, this._codeCopy, this._freq);

    var s = 0;
    while (s < codeSize && this._codeCopy[s] == 0) ++s;
    for (uint j = 0; s < codeSize; ++s, ++j) {
      if (s > 0) j <<= this._codeCopy[s] - this._codeCopy[s - 1];
      uint reversed = 0;
      var l = j;
      for (var bits = this._codeCopy[s]; bits > 0; --bits, l >>= 1) reversed = (reversed << 1) | (l & 1);
      this._buff[this._freq[s]] = reversed;
    }

    Array.Clear(result, 0, codeSize * 2);
    var next = 2;
    for (var i = 0; i < codeSize; ++i) {
      uint node = 0;
      var m = this._buff[i];
      for (var bit = 0; bit < code[i]; ++bit) {
        node += m & 1;
        if (code[i] - 1 <= bit) result[node] = (ushort)(codeSize * 2 + i);
        else {
          if (result[node] == 0) {
            result[node] = (ushort)next;
            next += 2;
          }
          node = result[node];
        }
        m >>= 1;
      }
    }
    this._reader.AlignToByte();
  }

  /// <summary>One symbol of the nested code stored in <see cref="_freq"/>.</summary>
  private uint NestedSymbol(uint size) {
    uint l = 0, n;
    do {
      l = this._freq[l + this.Bits(1)];
      n = size << 1;
    } while (n > l);
    return l - n;
  }
}
