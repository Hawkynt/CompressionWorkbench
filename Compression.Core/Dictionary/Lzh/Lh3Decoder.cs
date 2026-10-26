namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LHA <c>-lh3-</c>: LZSS over an 8 KiB window with static Huffman trees sent per block.
/// </summary>
/// <remarks>
/// <para>The forerunner of <c>-lh5-</c>, with simpler tree transmission:</para>
/// <list type="bullet">
///   <item><description>Each block starts with a 16-bit symbol count, then the character/length tree: for
///   each of 286 symbols a presence bit and, if set, a 4-bit length minus one. Three leading length-1
///   codes are the escape for a single-symbol tree, whose 9-bit value follows.</description></item>
///   <item><description>One bit selects the position tree: 1 sends 128 four-bit lengths (three leading
///   1s again escape to a single 7-bit value); 0 uses the fixed "ready made" table whose run starts
///   are <c>2; 1, 1, 3, 6, 13, 31, 78</c>. Its 128 symbols are the distance's high bits; 6 raw bits
///   follow.</description></item>
///   <item><description>Symbols 0-255 are literals, 256-284 match lengths 3-31, and 285 is followed by 8
///   raw bits for the longer lengths.</description></item>
/// </list>
/// <para>Sourcing: converted from the <c>-lh3-</c> path of The Unarchiver's XADMaster
/// (<c>XADLZHOldHandles.m</c>, LGPL-2.1-or-later), itself descended from LHa for UNIX's
/// <c>shuf.c</c>. XADMaster sizes the position alphabet as <c>1 &lt;&lt; (16 - 6)</c>; the
/// ready-made table only satisfies Kraft's equality with 128 symbols (8 KiB / 64), which is also what
/// <c>-lh3-</c>'s window needs, so 128 is used here. Checked against <c>lh3.lzh</c> from the lhasa
/// test suite (ISC), written by UNLHA32.DLL 2.67a, whose CRC-16 the output must match. See
/// THIRD-PARTY-NOTICE.XADMaster.txt beside this file.</para>
/// </remarks>
public static class Lh3Decoder {
  private const int DicBit = 13;
  private const int DicSize = 1 << DicBit;
  private const int Threshold = 3;
  private const int N1 = 286;                      // character/length alphabet
  private const int Np = DicSize / 64;             // position alphabet: 128
  private const int ExtraBits = 8;
  private const int LenField = 4;
  private const int CBit = 9;
  private const int MaxMatch = 256;
  private const int Nc = byte.MaxValue + MaxMatch + 2 - Threshold;  // 510: sizes the overflow tree

  private static readonly int[] ReadyMade = [2, 0x01, 0x01, 0x03, 0x06, 0x0D, 0x1F, 0x4E, 0];

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-lh3-</c> data.</summary>
  /// <exception cref="InvalidDataException">A block's code lengths do not describe a complete tree.</exception>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    ArgumentNullException.ThrowIfNull(compressed);
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    var output = new byte[originalSize];
    if (originalSize == 0)
      return output;

    var state = new State(new LhaLegacyBitReader(compressed));
    var text = new byte[DicSize];
    Array.Fill(text, (byte)' ');
    var loc = 0;
    var outPos = 0;

    while (outPos < originalSize) {
      var c = state.DecodeC();
      if (c <= byte.MaxValue) {
        output[outPos++] = text[loc] = (byte)c;
        loc = (loc + 1) & (DicSize - 1);
        continue;
      }

      var length = c - (256 - Threshold);
      var from = loc - state.DecodeP() - 1;
      while (length-- > 0 && outPos < originalSize) {
        output[outPos++] = text[loc] = text[from++ & (DicSize - 1)];
        loc = (loc + 1) & (DicSize - 1);
      }
    }

    return output;
  }

  private sealed class State(LhaLegacyBitReader bits) {
    private readonly ushort[] _cTable = new ushort[4096];
    private readonly ushort[] _ptTable = new ushort[256];
    // One overflow tree shared by both tables, as in the reference: the position tree's nodes start at
    // index 128 and the character tree's at 286, so neither reaches the other's.
    private readonly ushort[] _left = new ushort[2 * Nc - 1];
    private readonly ushort[] _right = new ushort[2 * Nc - 1];
    private readonly byte[] _cLen = new byte[N1];
    private readonly byte[] _ptLen = new byte[Np];
    private int _blockSize;

    public int DecodeC() {
      if (this._blockSize == 0) {
        this._blockSize = bits.GetBits(16);
        this.ReadTreeC();
        if (bits.GetBits(1) != 0)
          this.ReadTreeP();
        else
          this.ReadyMadeP();
        this.MakeTable(Np, this._ptLen, 8, this._ptTable);
      }
      --this._blockSize;

      int j = this._cTable[bits.BitBuf >> 4];
      if (j < N1)
        bits.FillBuf(this._cLen[j]);
      else {
        bits.FillBuf(12);
        var i = bits.BitBuf;
        do {
          j = (i & 0x8000) != 0 ? this._right[j] : this._left[j];
          i <<= 1;
        } while (j >= N1);
        bits.FillBuf(this._cLen[j] - 12);
      }

      if (j == N1 - 1)
        j += bits.GetBits(ExtraBits);
      return j;
    }

    public int DecodeP() {
      int j = this._ptTable[bits.BitBuf >> 8];
      if (j < Np)
        bits.FillBuf(this._ptLen[j]);
      else {
        bits.FillBuf(8);
        var i = bits.BitBuf;
        do {
          j = (i & 0x8000) != 0 ? this._right[j] : this._left[j];
          i <<= 1;
        } while (j >= Np);
        bits.FillBuf(this._ptLen[j] - 8);
      }
      return (j << 6) + bits.GetBits(6);
    }

    private void ReadTreeC() {
      var i = 0;
      while (i < N1) {
        this._cLen[i] = bits.GetBits(1) != 0 ? (byte)(bits.GetBits(LenField) + 1) : (byte)0;
        if (++i == 3 && this._cLen[0] == 1 && this._cLen[1] == 1 && this._cLen[2] == 1) {
          var c = (ushort)bits.GetBits(CBit);
          Array.Clear(this._cLen);
          Array.Fill(this._cTable, c);
          return;
        }
      }
      this.MakeTable(N1, this._cLen, 12, this._cTable);
    }

    private void ReadTreeP() {
      var i = 0;
      while (i < Np) {
        this._ptLen[i] = (byte)bits.GetBits(LenField);
        if (++i == 3 && this._ptLen[0] == 1 && this._ptLen[1] == 1 && this._ptLen[2] == 1) {
          var c = (ushort)bits.GetBits(DicBit - 6);
          Array.Clear(this._ptLen);
          Array.Fill(this._ptTable, c);
          return;
        }
      }
    }

    private void ReadyMadeP() {
      var t = 0;
      var j = ReadyMade[t++];
      for (var i = 0; i < Np; ++i) {
        while (ReadyMade[t] == i) {
          ++j;
          ++t;
        }
        this._ptLen[i] = (byte)j;
      }
    }

    /// <summary>
    /// Builds a <paramref name="tableBits"/>-bit lookup table for canonical codes of the given lengths,
    /// hanging longer codes off it in the shared overflow tree.
    /// </summary>
    private void MakeTable(int nChar, byte[] bitLen, int tableBits, ushort[] table) {
      Span<ushort> count = stackalloc ushort[17];
      Span<ushort> weight = stackalloc ushort[17];
      Span<ushort> start = stackalloc ushort[18];

      for (var i = 1; i <= 16; ++i)
        weight[i] = (ushort)(1 << (16 - i));
      for (var i = 0; i < nChar; ++i)
        ++count[bitLen[i]];

      ushort total = 0;
      for (var i = 1; i <= 16; ++i) {
        start[i] = total;
        total = (ushort)(total + weight[i] * count[i]);
      }
      if (total != 0)
        throw new InvalidDataException("LHA -lh3- block has code lengths that do not form a complete tree.");

      var m = 16 - tableBits;
      for (var i = 1; i <= tableBits; ++i) {
        start[i] >>= m;
        weight[i] >>= m;
      }

      var first = start[tableBits + 1] >> m;
      if (first != 0)
        for (var i = first; i < 1 << tableBits; ++i)
          table[i] = 0;

      var avail = nChar;
      for (var j = 0; j < nChar; ++j) {
        int k = bitLen[j];
        if (k == 0)
          continue;
        var l = (ushort)(start[k] + weight[k]);
        if (k <= tableBits) {
          for (int i = start[k]; i < l; ++i)
            table[i] = (ushort)j;
        } else {
          uint code = start[k];
          ref var p = ref table[code >> m];
          code <<= tableBits;
          for (var n = k - tableBits; --n >= 0;) {
            if (p == 0) {
              this._right[avail] = this._left[avail] = 0;
              p = (ushort)avail++;
            }
            if ((code & 0x8000) != 0)
              p = ref this._right[p];
            else
              p = ref this._left[p];
            code <<= 1;
          }
          p = (ushort)j;
        }
        start[k] = l;
      }
    }
  }
}
