namespace FileFormat.Zpaq;

/// <summary>
/// Bit predictor of a ZPAQ block: runs the components declared in COMP over the contexts
/// that HCOMP computes, and learns from every coded bit.
/// </summary>
/// <remarks>
/// Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>Predictor</c>,
/// interpreted path (<c>predict0</c>/<c>update0</c>). Integer behaviour, including the
/// wrap-around of the 32-bit products, follows the reference exactly because the coded
/// stream depends on every bit of every prediction.
/// </remarks>
internal sealed class ZpaqPredictor {
  private readonly ZpaqlMachine _z;
  private readonly Component[] _comp = new Component[256];
  private readonly int[] _p = new int[256];
  private readonly uint[] _h = new uint[256];
  private int _c8 = 1;
  private int _hmap4 = 1;

  private static readonly ushort[] SquashTable = BuildSquash();
  private static readonly short[] StretchTable = BuildStretch();

  /// <summary>Per-component model state, the reference's <c>Component</c>.</summary>
  private sealed class Component {
    public uint Limit;
    public uint Cxt;
    public uint A, B, C;
    public uint[] Cm = [];
    public byte[] Ht = [];
    public ushort[] A16 = [];
  }

  /// <summary>Creates a predictor driven by the HCOMP machine <paramref name="z"/>.</summary>
  public ZpaqPredictor(ZpaqlMachine z) {
    this._z = z;
    for (var i = 0; i < this._comp.Length; ++i)
      this._comp[i] = new();
  }

  /// <summary><see langword="true"/> when the block declares at least one component.</summary>
  public bool IsModeled => this._z.Header[6] != 0;

  /// <summary>Builds the model described by the block header.</summary>
  public void Init() {
    this._z.InitHcomp();
    Array.Clear(this._h);
    Array.Clear(this._p);
    this._c8 = 1;
    this._hmap4 = 1;
    for (var i = 0; i < 256; ++i)
      this._comp[i] = new();

    var header = this._z.Header;
    int n = header[6];
    var cp = 7;
    for (var i = 0; i < n; ++i) {
      var cr = this._comp[i];
      switch (header[cp]) {
        case ZpaqlMachine.Cons:
          this._p[i] = (header[cp + 1] - 128) * 4;
          break;
        case ZpaqlMachine.Cm:
          if (header[cp + 1] > 32)
            ZpaqError.Throw("max size for CM is 32");
          cr.Cm = ZpaqlMachine.AllocatePow2<uint>(header[cp + 1]);
          cr.Limit = (uint)(header[cp + 2] * 4);
          Array.Fill(cr.Cm, 0x80000000u);
          break;
        case ZpaqlMachine.Icm:
          if (header[cp + 1] > 26)
            ZpaqError.Throw("max size for ICM is 26");
          cr.Limit = 1023;
          cr.Cm = new uint[256];
          cr.Ht = ZpaqlMachine.AllocatePow2<byte>(header[cp + 1], 6);
          for (var j = 0; j < cr.Cm.Length; ++j)
            cr.Cm[j] = (uint)CmInit(j);
          break;
        case ZpaqlMachine.Match:
          if (header[cp + 1] > 32 || header[cp + 2] > 32)
            ZpaqError.Throw("max size for MATCH is 32 32");
          cr.Cm = ZpaqlMachine.AllocatePow2<uint>(header[cp + 1]);
          cr.Ht = ZpaqlMachine.AllocatePow2<byte>(header[cp + 2]);
          cr.Ht[0] = 1;
          break;
        case ZpaqlMachine.Avg:
          if (header[cp + 1] >= i)
            ZpaqError.Throw("AVG j >= i");
          if (header[cp + 2] >= i)
            ZpaqError.Throw("AVG k >= i");
          break;
        case ZpaqlMachine.Mix2:
          if (header[cp + 1] > 32)
            ZpaqError.Throw("max size for MIX2 is 32");
          if (header[cp + 3] >= i)
            ZpaqError.Throw("MIX2 k >= i");
          if (header[cp + 2] >= i)
            ZpaqError.Throw("MIX2 j >= i");
          cr.C = 1u << header[cp + 1];
          cr.A16 = ZpaqlMachine.AllocatePow2<ushort>(header[cp + 1]);
          Array.Fill(cr.A16, (ushort)32768);
          break;
        case ZpaqlMachine.Mix: {
            if (header[cp + 1] > 32)
              ZpaqError.Throw("max size for MIX is 32");
            if (header[cp + 2] >= i)
              ZpaqError.Throw("MIX j >= i");
            if (header[cp + 3] < 1 || header[cp + 3] > i - header[cp + 2])
              ZpaqError.Throw("MIX m not in 1..i-j");
            int m = header[cp + 3];
            cr.C = 1u << header[cp + 1];
            var size = (1L << header[cp + 1]) * m;
            if (size > Array.MaxLength)
              throw new NotSupportedException("ZPAQ: MIX weight table exceeds what this runtime can allocate.");
            cr.Cm = new uint[size];
            Array.Fill(cr.Cm, (uint)(65536 / m));
            break;
          }
        case ZpaqlMachine.Isse:
          if (header[cp + 1] > 32)
            ZpaqError.Throw("max size for ISSE is 32");
          if (header[cp + 2] >= i)
            ZpaqError.Throw("ISSE j >= i");
          cr.Ht = ZpaqlMachine.AllocatePow2<byte>(header[cp + 1], 6);
          cr.Cm = new uint[512];
          for (var j = 0; j < 256; ++j) {
            cr.Cm[j * 2] = 1 << 15;
            cr.Cm[j * 2 + 1] = (uint)Clamp512K(Stretch(CmInit(j) >> 8) * 1024);
          }
          break;
        case ZpaqlMachine.Sse:
          if (header[cp + 1] > 32)
            ZpaqError.Throw("max size for SSE is 32");
          if (header[cp + 2] >= i)
            ZpaqError.Throw("SSE j >= i");
          if (header[cp + 3] > header[cp + 4] * 4)
            ZpaqError.Throw("SSE start > limit*4");
          cr.Cm = ZpaqlMachine.AllocatePow2<uint>(header[cp + 1], 5);
          cr.Limit = (uint)(header[cp + 4] * 4);
          for (var j = 0; j < cr.Cm.Length; ++j)
            cr.Cm[j] = (uint)(Squash((j & 31) * 64 - 992) << 17 | header[cp + 3]);
          break;
        default:
          ZpaqError.Throw("unknown component type");
          break;
      }
      cp += ZpaqlMachine.ComponentSize[header[cp]];
    }
  }

  /// <summary>Probability (0..32767) that the next bit is a 1.</summary>
  public int Predict() {
    var header = this._z.Header;
    var p = this._p;
    int n = header[6];
    var cp = 7;
    for (var i = 0; i < n; ++i) {
      var cr = this._comp[i];
      switch (header[cp]) {
        case ZpaqlMachine.Cons:
          break;
        case ZpaqlMachine.Cm:
          cr.Cxt = this._h[i] ^ (uint)this._hmap4;
          p[i] = Stretch((int)(cr.Cm[cr.Cxt & (uint)(cr.Cm.Length - 1)] >> 17));
          break;
        case ZpaqlMachine.Icm:
          if (this._c8 == 1 || (this._c8 & 0xf0) == 16)
            cr.C = Find(cr.Ht, header[cp + 1] + 2, this._h[i] + 16 * (uint)this._c8);
          cr.Cxt = cr.Ht[cr.C + (this._hmap4 & 15)];
          p[i] = Stretch((int)(cr.Cm[cr.Cxt & (uint)(cr.Cm.Length - 1)] >> 8));
          break;
        case ZpaqlMachine.Match:
          if (cr.A == 0)
            p[i] = 0;
          else {
            cr.C = (uint)(cr.Ht[(cr.Limit - cr.B) & (uint)(cr.Ht.Length - 1)] >> (int)(7 - cr.Cxt)) & 1;
            p[i] = Stretch(ZpaqTables.MatchDivisor[cr.A] * (cr.C == 1 ? -1 : 1) & 32767);
          }
          break;
        case ZpaqlMachine.Avg:
          p[i] = (p[header[cp + 1]] * header[cp + 3] + p[header[cp + 2]] * (256 - header[cp + 3])) >> 8;
          break;
        case ZpaqlMachine.Mix2: {
            cr.Cxt = (this._h[i] + (uint)(this._c8 & header[cp + 5])) & (cr.C - 1);
            int w = cr.A16[cr.Cxt];
            p[i] = (w * p[header[cp + 2]] + (65536 - w) * p[header[cp + 3]]) >> 16;
            break;
          }
        case ZpaqlMachine.Mix: {
            int m = header[cp + 3];
            cr.Cxt = this._h[i] + (uint)(this._c8 & header[cp + 5]);
            cr.Cxt = (cr.Cxt & (cr.C - 1)) * (uint)m;
            var wt = cr.Cm.AsSpan((int)cr.Cxt, m);
            var sum = 0;
            int j0 = header[cp + 2];
            for (var j = 0; j < m; ++j)
              sum += ((int)wt[j] >> 8) * p[j0 + j];
            p[i] = Clamp2K(sum >> 8);
            break;
          }
        case ZpaqlMachine.Isse: {
            if (this._c8 == 1 || (this._c8 & 0xf0) == 16)
              cr.C = Find(cr.Ht, header[cp + 1] + 2, this._h[i] + 16 * (uint)this._c8);
            cr.Cxt = cr.Ht[cr.C + (this._hmap4 & 15)];
            var w0 = (int)cr.Cm[cr.Cxt * 2];
            var w1 = (int)cr.Cm[cr.Cxt * 2 + 1];
            p[i] = Clamp2K((w0 * p[header[cp + 2]] + w1 * 64) >> 16);
            break;
          }
        case ZpaqlMachine.Sse: {
            cr.Cxt = (this._h[i] + (uint)this._c8) * 32;
            var pq = p[header[cp + 2]] + 992;
            if (pq < 0)
              pq = 0;
            if (pq > 1983)
              pq = 1983;
            var wt = pq & 63;
            pq >>= 6;
            cr.Cxt += (uint)pq;
            var mask = (uint)(cr.Cm.Length - 1);
            p[i] = Stretch((int)(((cr.Cm[cr.Cxt & mask] >> 10) * (uint)(64 - wt) + (cr.Cm[(cr.Cxt + 1) & mask] >> 10) * (uint)wt) >> 13));
            cr.Cxt += (uint)(wt >> 5);
            break;
          }
        default:
          ZpaqError.Throw("component predict not implemented");
          break;
      }
      cp += ZpaqlMachine.ComponentSize[header[cp]];
    }
    return Squash(p[n - 1]);
  }

  /// <summary>Trains every component on the coded bit <paramref name="y"/> and advances the contexts.</summary>
  public void Update(int y) {
    var header = this._z.Header;
    var p = this._p;
    int n = header[6];
    var cp = 7;
    for (var i = 0; i < n; ++i) {
      var cr = this._comp[i];
      switch (header[cp]) {
        case ZpaqlMachine.Cons:
          break;
        case ZpaqlMachine.Cm:
          Train(cr, y);
          break;
        case ZpaqlMachine.Icm: {
            var idx = cr.C + (this._hmap4 & 15);
            cr.Ht[idx] = (byte)Next(cr.Ht[idx], y);
            ref var pn = ref cr.Cm[cr.Cxt & (uint)(cr.Cm.Length - 1)];
            pn += (uint)((int)((uint)(y * 32767) - (pn >> 8)) >> 2);
            break;
          }
        case ZpaqlMachine.Match: {
            var htMask = (uint)(cr.Ht.Length - 1);
            if ((int)cr.C != y)
              cr.A = 0;
            ref var cur = ref cr.Ht[cr.Limit & htMask];
            cur = (byte)(cur + cur + y);
            if (++cr.Cxt == 8) {
              cr.Cxt = 0;
              ++cr.Limit;
              cr.Limit &= (uint)((1L << header[cp + 2]) - 1);
              var cmMask = (uint)(cr.Cm.Length - 1);
              if (cr.A == 0) {
                cr.B = cr.Limit - cr.Cm[this._h[i] & cmMask];
                if ((cr.B & htMask) != 0)
                  while (cr.A < 255 && cr.Ht[(cr.Limit - cr.A - 1) & htMask] == cr.Ht[(cr.Limit - cr.A - cr.B - 1) & htMask])
                    ++cr.A;
              } else if (cr.A < 255)
                ++cr.A;
              cr.Cm[this._h[i] & cmMask] = cr.Limit;
            }
            break;
          }
        case ZpaqlMachine.Avg:
          break;
        case ZpaqlMachine.Mix2: {
            var err = (y * 32767 - Squash(p[i])) * header[cp + 4] >> 5;
            int w = cr.A16[cr.Cxt];
            w += (err * (p[header[cp + 2]] - p[header[cp + 3]]) + (1 << 12)) >> 13;
            if (w < 0)
              w = 0;
            if (w > 65535)
              w = 65535;
            cr.A16[cr.Cxt] = (ushort)w;
            break;
          }
        case ZpaqlMachine.Mix: {
            int m = header[cp + 3];
            var err = (y * 32767 - Squash(p[i])) * header[cp + 4] >> 4;
            var wt = cr.Cm.AsSpan((int)cr.Cxt, m);
            int j0 = header[cp + 2];
            for (var j = 0; j < m; ++j)
              wt[j] = (uint)Clamp512K((int)wt[j] + ((err * p[j0 + j] + (1 << 12)) >> 13));
            break;
          }
        case ZpaqlMachine.Isse: {
            var err = y * 32767 - Squash(p[i]);
            ref var w0 = ref cr.Cm[cr.Cxt * 2];
            ref var w1 = ref cr.Cm[cr.Cxt * 2 + 1];
            w0 = (uint)Clamp512K((int)w0 + ((err * p[header[cp + 2]] + (1 << 12)) >> 13));
            w1 = (uint)Clamp512K((int)w1 + ((err + 16) >> 5));
            cr.Ht[cr.C + (this._hmap4 & 15)] = (byte)Next((int)cr.Cxt, y);
            break;
          }
        case ZpaqlMachine.Sse:
          Train(cr, y);
          break;
      }
      cp += ZpaqlMachine.ComponentSize[header[cp]];
    }

    this._c8 += this._c8 + y;
    if (this._c8 >= 256) {
      this._z.Run((uint)(this._c8 - 256));
      this._hmap4 = 1;
      this._c8 = 1;
      for (var i = 0; i < n; ++i)
        this._h[i] = this._z.H(i);
    } else if (this._c8 is >= 16 and < 32)
      this._hmap4 = (this._hmap4 & 0xf) << 5 | y << 4 | 1;
    else
      this._hmap4 = (this._hmap4 & 0x1f0) | (((this._hmap4 & 0xf) * 2 + y) & 0xf);
  }

  /// <summary>Squash: x (-2048..2047) to probability 32768/(1+exp(-x/64)).</summary>
  internal static int Squash(int x) => SquashTable[x + 2048];

  /// <summary>Stretch: probability (0..32767) to ln(x/(32768-x))*64, the inverse of squash.</summary>
  internal static int Stretch(int x) => StretchTable[x];

  private static int Clamp2K(int x) => x < -2048 ? -2048 : x > 2047 ? 2047 : x;

  private static int Clamp512K(int x) => x < -(1 << 19) ? -(1 << 19) : x >= 1 << 19 ? (1 << 19) - 1 : x;

  private static int Next(int state, int y) => ZpaqTables.StateTable[state * 4 + y];

  private static int CmInit(int state) {
    var st = ZpaqTables.StateTable;
    return ((st[state * 4 + 3] * 2 + 1) << 22) / (st[state * 4 + 2] + st[state * 4 + 3] + 1);
  }

  private static void Train(Component cr, int y) {
    ref var pn = ref cr.Cm[cr.Cxt & (uint)(cr.Cm.Length - 1)];
    var count = pn & 0x3ff;
    var error = y * 32767 - (int)(pn >> 17);
    pn += (uint)((error * ZpaqTables.CmDivisor[count] & -1024) + (count < cr.Limit ? 1 : 0));
  }

  /// <summary>
  /// Finds the 16-byte row of <paramref name="ht"/> for context <paramref name="cxt"/>, replacing the
  /// least used of three candidate rows when none matches.
  /// </summary>
  private static uint Find(byte[] ht, int sizebits, uint cxt) {
    var chk = (byte)(cxt >> sizebits);
    var h0 = (cxt * 16) & (uint)(ht.Length - 16);
    if (ht[h0] == chk)
      return h0;
    var h1 = h0 ^ 16;
    if (ht[h1] == chk)
      return h1;
    var h2 = h0 ^ 32;
    if (ht[h2] == chk)
      return h2;
    uint pick;
    if (ht[h0 + 1] <= ht[h1 + 1] && ht[h0 + 1] <= ht[h2 + 1])
      pick = h0;
    else if (ht[h1 + 1] < ht[h2 + 1])
      pick = h1;
    else
      pick = h2;
    ht.AsSpan((int)pick, 16).Clear();
    ht[pick] = chk;
    return pick;
  }

  private static ushort[] BuildSquash() {
    var t = new ushort[4096];
    ZpaqTables.SquashMiddle.CopyTo(t, 1376);
    for (var i = 2720; i < 4096; ++i)
      t[i] = 32767;
    return t;
  }

  private static short[] BuildStretch() {
    var t = new short[32768];
    var k = 16384;
    var runs = ZpaqTables.StretchRunLengths;
    for (var i = 0; i < 712; ++i)
      for (int j = runs[i]; j > 0; --j)
        t[k++] = (short)i;
    for (var i = 0; i < 16384; ++i)
      t[i] = (short)-t[32767 - i];
    return t;
  }
}
