namespace FileFormat.Zpaq;

/// <summary>
/// The ZPAQL virtual machine: a block header (COMP + HCOMP, or a PCOMP post-processor) and
/// the interpreter that runs it once per byte.
/// </summary>
/// <remarks>
/// Converted from libzpaq 7.15 (Matt Mahoney, public domain), class <c>ZPAQL</c>, interpreter
/// only (no JIT). The header layout is the one in the ZPAQ Level 2 specification section 6:
/// <c>hsize[2] hh hm ph pm n COMP[...] 0 (128-byte guard) HCOMP[...] 0</c>.
/// </remarks>
internal sealed class ZpaqlMachine {
  /// <summary>Number of bytes that each component type occupies in COMP, indexed by type.</summary>
  internal static ReadOnlySpan<byte> ComponentSize => [0, 2, 3, 2, 3, 4, 6, 6, 3, 5];

  /// <summary>Component type codes.</summary>
  internal const int None = 0, Cons = 1, Cm = 2, Icm = 3, Match = 4, Avg = 5, Mix2 = 6, Mix = 7, Isse = 8, Sse = 9;

  private const int OutputBufferSize = 1 << 14;

  private byte[] _m = [];
  private uint[] _h = [];
  private uint[] _r = [];
  private int _mMask;
  private int _hMask;
  private readonly byte[] _outbuf = new byte[OutputBufferSize];
  private int _bufptr;
  private uint _a, _b, _c, _d;
  private bool _f;
  private int _pc;

  /// <summary>Block header: <c>hsize[2] hh hm ph pm n COMP (guard) HCOMP (guard)</c>.</summary>
  internal byte[] Header = [];

  /// <summary>End of COMP in <see cref="Header"/>: COMP occupies <c>Header[7..Cend-1]</c>.</summary>
  internal int Cend;

  /// <summary>Start of HCOMP/PCOMP code in <see cref="Header"/>.</summary>
  internal int Hbegin;

  /// <summary>End of HCOMP/PCOMP code in <see cref="Header"/>.</summary>
  internal int Hend;

  /// <summary>Destination of the OUT instruction, or <see langword="null"/> to discard.</summary>
  internal IZpaqWriter? Output;

  /// <summary>Hash of everything written by OUT, or <see langword="null"/>.</summary>
  internal ZpaqSha1? Sha1;

  /// <summary>Creates an empty machine.</summary>
  public ZpaqlMachine() => this.Clear();

  /// <summary>Gets element <paramref name="i"/> of H (index wraps to the array size).</summary>
  internal uint H(int i) => this._h[i & this._hMask];

  /// <summary>Releases the program and resets the machine state.</summary>
  internal void Clear() {
    this.Cend = this.Hbegin = this.Hend = 0;
    this._a = this._b = this._c = this._d = 0;
    this._f = false;
    this._pc = 0;
    this.Header = [];
    this._h = [];
    this._m = [];
    this._r = [];
  }

  /// <summary>Writes the header; with <paramref name="postProcessorOnly"/> only the PCOMP size and code.</summary>
  /// <returns><see langword="true"/> when there was a program to write.</returns>
  internal bool Write(IZpaqWriter output, bool postProcessorOnly) {
    if (this.Header.Length <= 6)
      return false;
    if (!postProcessorOnly) {
      for (var i = 0; i < this.Cend; ++i)
        output.Put(this.Header[i]);
    } else {
      output.Put((this.Hend - this.Hbegin) & 255);
      output.Put((this.Hend - this.Hbegin) >> 8);
    }
    for (var i = this.Hbegin; i < this.Hend; ++i)
      output.Put(this.Header[i]);
    return true;
  }

  /// <summary>Reads a COMP + HCOMP header from <paramref name="input"/>.</summary>
  /// <returns>The number of header bytes held.</returns>
  internal int Read(IZpaqReader input) {
    var hsize = input.Get();
    hsize += input.Get() * 256;
    if (hsize < 0)
      ZpaqError.Throw("unexpected end of file");
    this.Header = new byte[hsize + 300];
    this.Cend = this.Hbegin = this.Hend = 0;
    this.Header[this.Cend++] = (byte)hsize;
    this.Header[this.Cend++] = (byte)(hsize >> 8);
    while (this.Cend < 7)
      this.Header[this.Cend++] = (byte)input.Get();

    var n = this.Header[this.Cend - 1];
    for (var i = 0; i < n; ++i) {
      var type = input.Get();
      if (type is < 0 or > 255)
        ZpaqError.Throw("unexpected end of file");
      this.Header[this.Cend++] = (byte)type;
      var size = type < ComponentSize.Length ? ComponentSize[type] : 0;
      if (size < 1)
        ZpaqError.Throw("invalid component type");
      if (this.Cend + size > hsize)
        ZpaqError.Throw("COMP overflows header");
      for (var j = 1; j < size; ++j)
        this.Header[this.Cend++] = (byte)input.Get();
    }
    if ((this.Header[this.Cend++] = (byte)input.Get()) != 0)
      ZpaqError.Throw("missing COMP END");

    this.Hbegin = this.Hend = this.Cend + 128;
    if (this.Hend > hsize + 129)
      ZpaqError.Throw("missing HCOMP");
    while (this.Hend < hsize + 129) {
      var op = input.Get();
      if (op == -1)
        ZpaqError.Throw("unexpected end of file");
      this.Header[this.Hend++] = (byte)op;
    }
    if ((this.Header[this.Hend++] = (byte)input.Get()) != 0)
      ZpaqError.Throw("missing HCOMP END");
    return this.Cend + this.Hend - this.Hbegin;
  }

  /// <summary>Prepares the machine to run as HCOMP (sizes hh, hm).</summary>
  internal void InitHcomp() => this.Init(this.Header[2], this.Header[3]);

  /// <summary>Prepares the machine to run as PCOMP (sizes ph, pm).</summary>
  internal void InitPcomp() => this.Init(this.Header[4], this.Header[5]);

  /// <summary>Approximate memory needed to run this block, in bytes.</summary>
  internal double Memory() {
    static double Pow2(int x) => Math.Pow(2, x);
    var mem = Pow2(this.Header[2] + 2) + Pow2(this.Header[3]) + Pow2(this.Header[4] + 2) + Pow2(this.Header[5]) + this.Header.Length;
    var cp = 7;
    for (var i = 0; i < this.Header[6]; ++i) {
      var size = Pow2(this.Header[cp + 1]);
      switch (this.Header[cp]) {
        case Cm: mem += 4 * size; break;
        case Icm: mem += 64 * size + 1024; break;
        case Match: mem += 4 * size + Pow2(this.Header[cp + 2]); break;
        case Mix2: mem += 2 * size; break;
        case Mix: mem += 4 * size * this.Header[cp + 3]; break;
        case Isse: mem += 64 * size + 2048; break;
        case Sse: mem += 128 * size; break;
      }
      cp += ComponentSize[this.Header[cp]];
    }
    return mem;
  }

  /// <summary>Writes the pending OUT bytes to <see cref="Output"/> and <see cref="Sha1"/>.</summary>
  internal void Flush() {
    var pending = this._outbuf.AsSpan(0, this._bufptr);
    this.Output?.Write(pending);
    this.Sha1?.Write(pending);
    this._bufptr = 0;
  }

  /// <summary>Runs the program once with <paramref name="input"/> in A.</summary>
  internal void Run(uint input) {
    this._pc = this.Hbegin;
    this._a = input;
    while (this.Execute()) { }
  }

  /// <summary>Allocates a zero-filled array of 2^<paramref name="bits"/> elements of <typeparamref name="T"/>.</summary>
  internal static T[] AllocatePow2<T>(int bits, int extraShift = 0) {
    var total = bits + extraShift;
    if (total > 32)
      ZpaqError.Throw("array too big");
    var length = 1L << total;
    if (length > Array.MaxLength)
      throw new NotSupportedException($"ZPAQ: a model array of 2^{total} elements exceeds what this runtime can allocate.");
    return new T[length];
  }

  private void Init(int hbits, int mbits) {
    if (hbits > 32)
      ZpaqError.Throw("H too big");
    if (mbits > 32)
      ZpaqError.Throw("M too big");
    this._h = AllocatePow2<uint>(hbits);
    this._m = AllocatePow2<byte>(mbits);
    this._hMask = this._h.Length - 1;
    this._mMask = this._m.Length - 1;
    this._r = new uint[256];
    this._a = this._b = this._c = this._d = 0;
    this._pc = 0;
    this._f = false;
  }

  /// <summary>Buffers output byte <paramref name="ch"/>; -1 flushes the buffer.</summary>
  internal void OutC(int ch) {
    if (ch < 0) {
      this.Flush();
      return;
    }
    this._outbuf[this._bufptr] = (byte)ch;
    if (++this._bufptr == this._outbuf.Length)
      this.Flush();
  }

  [System.Diagnostics.CodeAnalysis.DoesNotReturn]
  private static void Error() => ZpaqError.Throw("ZPAQL execution error");

  private void Div(uint x) => this._a = x != 0 ? this._a / x : 0;

  private void Mod(uint x) => this._a = x != 0 ? this._a % x : 0;

  private static void Swap(ref uint a, ref uint x) {
    a ^= x;
    x ^= a;
    a ^= x;
  }

  private static void Swap(ref uint a, ref byte x) {
    a ^= x;
    x = (byte)(x ^ a);
    a ^= x;
  }

  /// <summary>Interprets one instruction; returns <see langword="false"/> after HALT.</summary>
  private bool Execute() {
    var header = this.Header;
    var m = this._m;
    var h = this._h;
    var mm = this._mMask;
    var hm = this._hMask;
    ref var mb = ref m[(int)(this._b & (uint)mm)];
    ref var mc = ref m[(int)(this._c & (uint)mm)];
    ref var hd = ref h[(int)(this._d & (uint)hm)];
    switch (header[this._pc++]) {
      case 0: Error(); break;
      case 1: ++this._a; break;
      case 2: --this._a; break;
      case 3: this._a = ~this._a; break;
      case 4: this._a = 0; break;
      case 7: this._a = this._r[header[this._pc++]]; break;
      case 8: Swap(ref this._a, ref this._b); break;
      case 9: ++this._b; break;
      case 10: --this._b; break;
      case 11: this._b = ~this._b; break;
      case 12: this._b = 0; break;
      case 15: this._b = this._r[header[this._pc++]]; break;
      case 16: Swap(ref this._a, ref this._c); break;
      case 17: ++this._c; break;
      case 18: --this._c; break;
      case 19: this._c = ~this._c; break;
      case 20: this._c = 0; break;
      case 23: this._c = this._r[header[this._pc++]]; break;
      case 24: Swap(ref this._a, ref this._d); break;
      case 25: ++this._d; break;
      case 26: --this._d; break;
      case 27: this._d = ~this._d; break;
      case 28: this._d = 0; break;
      case 31: this._d = this._r[header[this._pc++]]; break;
      case 32: Swap(ref this._a, ref mb); break;
      case 33: ++mb; break;
      case 34: --mb; break;
      case 35: mb = (byte)~mb; break;
      case 36: mb = 0; break;
      case 39: if (this._f) this._pc += ((header[this._pc] + 128) & 255) - 127; else ++this._pc; break;
      case 40: Swap(ref this._a, ref mc); break;
      case 41: ++mc; break;
      case 42: --mc; break;
      case 43: mc = (byte)~mc; break;
      case 44: mc = 0; break;
      case 47: if (!this._f) this._pc += ((header[this._pc] + 128) & 255) - 127; else ++this._pc; break;
      case 48: Swap(ref this._a, ref hd); break;
      case 49: ++hd; break;
      case 50: --hd; break;
      case 51: hd = ~hd; break;
      case 52: hd = 0; break;
      case 55: this._r[header[this._pc++]] = this._a; break;
      case 56: return false;
      case 57: this.OutC((int)(this._a & 255)); break;
      case 59: this._a = (this._a + mb + 512) * 773; break;
      case 60: hd = (hd + this._a + 512) * 773; break;
      case 63: this._pc += ((header[this._pc] + 128) & 255) - 127; break;
      case 64: break;
      case 65: this._a = this._b; break;
      case 66: this._a = this._c; break;
      case 67: this._a = this._d; break;
      case 68: this._a = mb; break;
      case 69: this._a = mc; break;
      case 70: this._a = hd; break;
      case 71: this._a = header[this._pc++]; break;
      case 72: this._b = this._a; break;
      case 73: break;
      case 74: this._b = this._c; break;
      case 75: this._b = this._d; break;
      case 76: this._b = mb; break;
      case 77: this._b = mc; break;
      case 78: this._b = hd; break;
      case 79: this._b = header[this._pc++]; break;
      case 80: this._c = this._a; break;
      case 81: this._c = this._b; break;
      case 82: break;
      case 83: this._c = this._d; break;
      case 84: this._c = mb; break;
      case 85: this._c = mc; break;
      case 86: this._c = hd; break;
      case 87: this._c = header[this._pc++]; break;
      case 88: this._d = this._a; break;
      case 89: this._d = this._b; break;
      case 90: this._d = this._c; break;
      case 91: break;
      case 92: this._d = mb; break;
      case 93: this._d = mc; break;
      case 94: this._d = hd; break;
      case 95: this._d = header[this._pc++]; break;
      case 96: mb = (byte)this._a; break;
      case 97: mb = (byte)this._b; break;
      case 98: mb = (byte)this._c; break;
      case 99: mb = (byte)this._d; break;
      case 100: break;
      case 101: mb = mc; break;
      case 102: mb = (byte)hd; break;
      case 103: mb = header[this._pc++]; break;
      case 104: mc = (byte)this._a; break;
      case 105: mc = (byte)this._b; break;
      case 106: mc = (byte)this._c; break;
      case 107: mc = (byte)this._d; break;
      case 108: mc = mb; break;
      case 109: break;
      case 110: mc = (byte)hd; break;
      case 111: mc = header[this._pc++]; break;
      case 112: hd = this._a; break;
      case 113: hd = this._b; break;
      case 114: hd = this._c; break;
      case 115: hd = this._d; break;
      case 116: hd = mb; break;
      case 117: hd = mc; break;
      case 118: break;
      case 119: hd = header[this._pc++]; break;
      case 128: this._a += this._a; break;
      case 129: this._a += this._b; break;
      case 130: this._a += this._c; break;
      case 131: this._a += this._d; break;
      case 132: this._a += mb; break;
      case 133: this._a += mc; break;
      case 134: this._a += hd; break;
      case 135: this._a += header[this._pc++]; break;
      case 136: this._a = 0; break;
      case 137: this._a -= this._b; break;
      case 138: this._a -= this._c; break;
      case 139: this._a -= this._d; break;
      case 140: this._a -= mb; break;
      case 141: this._a -= mc; break;
      case 142: this._a -= hd; break;
      case 143: this._a -= header[this._pc++]; break;
      case 144: this._a *= this._a; break;
      case 145: this._a *= this._b; break;
      case 146: this._a *= this._c; break;
      case 147: this._a *= this._d; break;
      case 148: this._a *= mb; break;
      case 149: this._a *= mc; break;
      case 150: this._a *= hd; break;
      case 151: this._a *= header[this._pc++]; break;
      case 152: this.Div(this._a); break;
      case 153: this.Div(this._b); break;
      case 154: this.Div(this._c); break;
      case 155: this.Div(this._d); break;
      case 156: this.Div(mb); break;
      case 157: this.Div(mc); break;
      case 158: this.Div(hd); break;
      case 159: this.Div(header[this._pc++]); break;
      case 160: this.Mod(this._a); break;
      case 161: this.Mod(this._b); break;
      case 162: this.Mod(this._c); break;
      case 163: this.Mod(this._d); break;
      case 164: this.Mod(mb); break;
      case 165: this.Mod(mc); break;
      case 166: this.Mod(hd); break;
      case 167: this.Mod(header[this._pc++]); break;
      case 168: break;
      case 169: this._a &= this._b; break;
      case 170: this._a &= this._c; break;
      case 171: this._a &= this._d; break;
      case 172: this._a &= mb; break;
      case 173: this._a &= mc; break;
      case 174: this._a &= hd; break;
      case 175: this._a &= header[this._pc++]; break;
      case 176: this._a = 0; break;
      case 177: this._a &= ~this._b; break;
      case 178: this._a &= ~this._c; break;
      case 179: this._a &= ~this._d; break;
      case 180: this._a &= ~(uint)mb; break;
      case 181: this._a &= ~(uint)mc; break;
      case 182: this._a &= ~hd; break;
      case 183: this._a &= ~(uint)header[this._pc++]; break;
      case 184: break;
      case 185: this._a |= this._b; break;
      case 186: this._a |= this._c; break;
      case 187: this._a |= this._d; break;
      case 188: this._a |= mb; break;
      case 189: this._a |= mc; break;
      case 190: this._a |= hd; break;
      case 191: this._a |= header[this._pc++]; break;
      case 192: this._a = 0; break;
      case 193: this._a ^= this._b; break;
      case 194: this._a ^= this._c; break;
      case 195: this._a ^= this._d; break;
      case 196: this._a ^= mb; break;
      case 197: this._a ^= mc; break;
      case 198: this._a ^= hd; break;
      case 199: this._a ^= header[this._pc++]; break;
      case 200: this._a <<= (int)(this._a & 31); break;
      case 201: this._a <<= (int)(this._b & 31); break;
      case 202: this._a <<= (int)(this._c & 31); break;
      case 203: this._a <<= (int)(this._d & 31); break;
      case 204: this._a <<= mb & 31; break;
      case 205: this._a <<= mc & 31; break;
      case 206: this._a <<= (int)(hd & 31); break;
      case 207: this._a <<= header[this._pc++] & 31; break;
      case 208: this._a >>= (int)(this._a & 31); break;
      case 209: this._a >>= (int)(this._b & 31); break;
      case 210: this._a >>= (int)(this._c & 31); break;
      case 211: this._a >>= (int)(this._d & 31); break;
      case 212: this._a >>= mb & 31; break;
      case 213: this._a >>= mc & 31; break;
      case 214: this._a >>= (int)(hd & 31); break;
      case 215: this._a >>= header[this._pc++] & 31; break;
      case 216: this._f = true; break;
      case 217: this._f = this._a == this._b; break;
      case 218: this._f = this._a == this._c; break;
      case 219: this._f = this._a == this._d; break;
      case 220: this._f = this._a == mb; break;
      case 221: this._f = this._a == mc; break;
      case 222: this._f = this._a == hd; break;
      case 223: this._f = this._a == header[this._pc++]; break;
      case 224: this._f = false; break;
      case 225: this._f = this._a < this._b; break;
      case 226: this._f = this._a < this._c; break;
      case 227: this._f = this._a < this._d; break;
      case 228: this._f = this._a < mb; break;
      case 229: this._f = this._a < mc; break;
      case 230: this._f = this._a < hd; break;
      case 231: this._f = this._a < header[this._pc++]; break;
      case 232: this._f = false; break;
      case 233: this._f = this._a > this._b; break;
      case 234: this._f = this._a > this._c; break;
      case 235: this._f = this._a > this._d; break;
      case 236: this._f = this._a > mb; break;
      case 237: this._f = this._a > mc; break;
      case 238: this._f = this._a > hd; break;
      case 239: this._f = this._a > header[this._pc++]; break;
      case 255:
        if ((this._pc = this.Hbegin + header[this._pc] + 256 * header[this._pc + 1]) >= this.Hend)
          Error();
        break;
      default: Error(); break;
    }
    return true;
  }
}
