namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LHA <c>-lh2-</c>: LZSS over an 8 KiB window with two adaptive Huffman trees.
/// </summary>
/// <remarks>
/// <para>A short-lived LHarc 2.x method. Unlike <c>-lh1-</c>, whose single tree is the classic
/// LZHUF one, <c>-lh2-</c> keeps two trees with "block" bookkeeping (a list of runs of equal
/// frequency) so a symbol's increment is one swap with its run leader:</para>
/// <list type="bullet">
///   <item><description>a character/length tree of 286 leaves: 0-255 literals, 256-284 match lengths 3-31,
///   and 285 followed by 8 raw bits for the longer lengths;</description></item>
///   <item><description>a position tree that starts with one leaf and gains a leaf for every 64 bytes of
///   output, until it covers the 8 KiB window (128 leaves). Its symbol is the high bits of the distance;
///   6 raw bits follow.</description></item>
/// </list>
/// <para>Both trees halve their frequencies when the root count reaches 0x8000.</para>
/// <para>Sourcing: converted from the <c>-lh2-</c> path of The Unarchiver's XADMaster
/// (<c>XADLZHOldHandles.m</c>, LGPL-2.1-or-later), itself descended from LHa for UNIX's
/// <c>dhuf.c</c>; checked against <c>lh2.lzh</c> from the lhasa test suite (ISC), written by
/// UNLHA32.DLL 2.67a, whose CRC-16 the output must match. See THIRD-PARTY-NOTICE.XADMaster.txt beside this file.</para>
/// </remarks>
public static class Lh2Decoder {
  private const int DicBit = 13;
  private const int DicSize = 1 << DicBit;
  private const int Threshold = 3;
  private const int NMax = 286;
  private const int NChar = 256 + 60 - Threshold + 1;     // 314: sizes the shared node arrays
  private const int TreeSizeC = NChar * 2;                // 628
  private const int TreeSizeP = 128 * 2;
  private const int TreeSize = TreeSizeC + TreeSizeP;
  private const int RootC = 0;
  private const int RootP = TreeSizeC;
  private const int N1 = NMax - 1;                        // the escape symbol (maxmatch 256 < 510)

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-lh2-</c> data.</summary>
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
        ++state.Count;
        continue;
      }

      var length = c - (256 - Threshold);
      var from = loc - state.DecodeP() - 1;
      state.Count += (uint)length;
      while (length-- > 0 && outPos < originalSize) {
        output[outPos++] = text[loc] = text[from++ & (DicSize - 1)];
        loc = (loc + 1) & (DicSize - 1);
      }
    }

    return output;
  }

  private sealed class State {
    private readonly LhaLegacyBitReader _bits;
    private readonly ushort[] _freq = new ushort[TreeSize];
    private readonly short[] _child = new short[TreeSize];
    private readonly short[] _stock = new short[TreeSize];
    private readonly short[] _sNode = new short[TreeSize / 2];
    private readonly short[] _block = new short[TreeSize];
    private readonly short[] _parent = new short[TreeSize];
    private readonly short[] _edge = new short[TreeSize];
    private int _avail;
    private int _mostP;
    private ushort _totalP;
    private uint _nextCount;

    /// <summary>Bytes produced so far; the position tree grows with it.</summary>
    public uint Count;

    public State(LhaLegacyBitReader bits) {
      this._bits = bits;
      this.StartC();
      this.StartP();
    }

    private void StartC() {
      for (var i = 0; i < TreeSizeC; ++i) {
        this._stock[i] = (short)i;
        this._block[i] = 0;
      }

      int j;
      int k;
      for (k = 0, j = NMax * 2 - 2; k < NMax; ++k, --j) {
        this._freq[j] = 1;
        this._child[j] = (short)~k;
        this._sNode[k] = (short)j;
        this._block[j] = 1;
      }

      this._avail = 2;
      this._edge[1] = NMax - 1;
      var i2 = NMax * 2 - 2;
      while (j >= 0) {
        var f = this._freq[j] = (ushort)(this._freq[i2] + this._freq[i2 - 1]);
        this._child[j] = (short)i2;
        this._parent[i2] = this._parent[i2 - 1] = (short)j;
        if (f == this._freq[j + 1])
          this._edge[this._block[j] = this._block[j + 1]] = (short)j;
        else
          this._edge[this._block[j] = this._stock[this._avail++]] = (short)j;
        i2 -= 2;
        --j;
      }
    }

    private void StartP() {
      this._freq[RootP] = 1;
      this._child[RootP] = ~NChar;
      this._sNode[NChar] = RootP;
      this._edge[this._block[RootP] = this._stock[this._avail++]] = RootP;
      this._mostP = RootP;
      this._totalP = 0;
      this._nextCount = 64;
    }

    /// <summary>Halves the leaf counts in [start, end) and rebuilds that tree and its runs.</summary>
    private void Reconstruct(int start, int end) {
      int i, j, k, l, b = 0;
      uint f, g;

      for (i = j = start; i < end; ++i) {
        if ((k = this._child[i]) < 0) {
          this._freq[j] = (ushort)((this._freq[i] + 1) / 2);
          this._child[j] = (short)k;
          ++j;
        }
        if (this._edge[b = this._block[i]] == i)
          this._stock[--this._avail] = (short)b;
      }

      --j;
      i = end - 1;
      l = end - 2;
      while (i >= start) {
        while (i >= l) {
          this._freq[i] = this._freq[j];
          this._child[i] = this._child[j];
          --i;
          --j;
        }
        f = (uint)(this._freq[l] + this._freq[l + 1]);
        for (k = start; f < this._freq[k]; ++k) { }
        while (j >= k) {
          this._freq[i] = this._freq[j];
          this._child[i] = this._child[j];
          --i;
          --j;
        }
        this._freq[i] = (ushort)f;
        this._child[i] = (short)(l + 1);
        --i;
        l -= 2;
      }

      f = 0;
      for (i = start; i < end; ++i) {
        if ((j = this._child[i]) < 0)
          this._sNode[~j] = (short)i;
        else
          this._parent[j] = this._parent[j - 1] = (short)i;
        if ((g = this._freq[i]) == f)
          this._block[i] = (short)b;
        else {
          this._edge[b = this._block[i] = this._stock[this._avail++]] = (short)i;
          f = g;
        }
      }
    }

    /// <summary>Increments node <paramref name="p"/>, first swapping it with its run leader; returns the parent.</summary>
    private int SwapInc(int p) {
      int b = this._block[p], q, r, s;
      if ((q = this._edge[b]) != p) {
        r = this._child[p];
        s = this._child[q];
        this._child[p] = (short)s;
        this._child[q] = (short)r;
        if (r >= 0)
          this._parent[r] = this._parent[r - 1] = (short)q;
        else
          this._sNode[~r] = (short)q;
        if (s >= 0)
          this._parent[s] = this._parent[s - 1] = (short)p;
        else
          this._sNode[~s] = (short)p;
        p = q;
        ++this._edge[b];
        if (++this._freq[p] == this._freq[p - 1])
          this._block[p] = this._block[p - 1];
        else
          this._edge[this._block[p] = this._stock[this._avail++]] = (short)p;
      } else if (b == this._block[p + 1]) {
        ++this._edge[b];
        if (++this._freq[p] == this._freq[p - 1])
          this._block[p] = this._block[p - 1];
        else
          this._edge[this._block[p] = this._stock[this._avail++]] = (short)p;
      } else if (++this._freq[p] == this._freq[p - 1]) {
        this._stock[--this._avail] = (short)b;
        this._block[p] = this._block[p - 1];
      }
      return this._parent[p];
    }

    private void UpdateC(int p) {
      if (this._freq[RootC] == 0x8000)
        this.Reconstruct(0, NMax * 2 - 1);
      ++this._freq[RootC];
      var q = (int)this._sNode[p];
      do
        q = this.SwapInc(q);
      while (q != RootC);
    }

    private void UpdateP(int p) {
      if (this._totalP == 0x8000) {
        this.Reconstruct(RootP, this._mostP + 1);
        this._totalP = this._freq[RootP];
        this._freq[RootP] = 0xffff;
      }
      var q = (int)this._sNode[p + NChar];
      while (q != RootP)
        q = this.SwapInc(q);
      ++this._totalP;
    }

    /// <summary>Splits the last position leaf to add position symbol <paramref name="p"/>.</summary>
    private void MakeNewNode(int p) {
      var r = this._mostP + 1;
      var q = r + 1;
      this._sNode[~(this._child[r] = this._child[this._mostP])] = (short)r;
      this._child[q] = (short)~(p + NChar);
      this._child[this._mostP] = (short)q;
      this._freq[r] = this._freq[this._mostP];
      this._freq[q] = 0;
      this._block[r] = this._block[this._mostP];
      if (this._mostP == RootP) {
        this._freq[RootP] = 0xffff;
        ++this._edge[this._block[RootP]];
      }
      this._parent[r] = this._parent[q] = (short)this._mostP;
      this._edge[this._block[q] = this._stock[this._avail++]] =
        this._sNode[p + NChar] = (short)(this._mostP = q);
      this.UpdateP(p);
    }

    /// <summary>Walks a tree from the child pointer <paramref name="c"/> to a leaf; returns the leaf (~symbol).</summary>
    private int Walk(int c, bool atLeastOne) {
      var buf = (short)this._bits.BitBuf;
      var cnt = 0;
      if (!atLeastOne && c <= 0) {
        this._bits.FillBuf(0);
        return c;
      }
      do {
        c = this._child[c - (buf < 0 ? 1 : 0)];
        buf <<= 1;
        if (++cnt == 16) {
          this._bits.FillBuf(16);
          buf = (short)this._bits.BitBuf;
          cnt = 0;
        }
      } while (c > 0);
      this._bits.FillBuf(cnt);
      return c;
    }

    public int DecodeC() {
      var c = ~this.Walk(this._child[RootC], atLeastOne: true);
      this.UpdateC(c);
      if (c == N1)
        c += this._bits.GetBits(8);
      return c;
    }

    public int DecodeP() {
      while (this.Count > this._nextCount) {
        this.MakeNewNode((int)(this._nextCount / 64));
        if ((this._nextCount += 64) >= DicSize)
          this._nextCount = uint.MaxValue;
      }
      var c = ~this.Walk(this._child[RootP], atLeastOne: false) - NChar;
      this.UpdateP(c);
      return (c << 6) + this._bits.GetBits(6);
    }
  }
}
