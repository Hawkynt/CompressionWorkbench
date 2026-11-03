using System.Buffers.Binary;

namespace FileFormat.FreeArc;

/// <summary>
/// PPMd variant I (PPMII), as FreeArc's <c>ppmd:order:mem[:rN]</c> stage stores it: Dmitry
/// Shkarin's model with Dmitry Subbotin's carryless range coder, decoded until the model escapes
/// past its order −1 context.
/// </summary>
/// <remarks>
/// <para>Converted from <c>Model.cpp</c>, <c>SubAlloc.hpp</c> and <c>Coder.hpp</c> of PPMd var.I
/// (Dmitry Shkarin, public domain; the range coder by Dmitry Subbotin, public domain), the copy in
/// FreeArc 0.67's <c>Compression/PPMD</c>; see THIRD-PARTY-NOTICE.PPMd.txt.</para>
/// <para>The model's decisions depend on its memory layout — which addresses are text and which
/// are contexts, when the heap runs out, how free blocks are glued — so the 32-bit layout is
/// reproduced in one byte array: a state is 6 bytes (symbol, frequency, 32-bit successor), a
/// context and an allocation unit 12, offsets stand in for pointers and 0 for NULL. The free-list
/// heads and the gluing list head live in the same array, below the heap.</para>
/// </remarks>
internal sealed class FreeArcPpmd {
  private const int UpFreq = 5, IntBits = 7, PeriodBits = 7, TotBits = IntBits + PeriodBits;
  private const int Interval = 1 << IntBits, BinScale = 1 << TotBits, MaxFreq = 124, OBound = 9;
  private const int UnitSize = 12, N1 = 4, N2 = 4, N3 = 4, N4 = (128 + 3 - 1 * N1 - 2 * N2 - 3 * N3) / 4;
  private const int NIndexes = N1 + N2 + N3 + N4;
  private const int MaxO = 128;
  private const uint Top = 1u << 24, Bot = 1u << 15;
  private const int MrmRestart = 0, MrmCutOff = 1, MrmFreeze = 2;

  private static readonly byte[] ExpEscape = [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];
  private static readonly ushort[] InitBinEsc = [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x64A1, 0x5ABC, 0x6632, 0x6051];
  private static readonly byte[] Indx2Units = new byte[NIndexes];
  private static readonly byte[] Units2Indx = new byte[128];
  private static readonly byte[] NS2BSIndx = new byte[256];
  private static readonly byte[] QTable = new byte[260];

  static FreeArcPpmd() {
    int i, k;
    for (i = 0, k = 1; i < N1; ++i, k += 1) Indx2Units[i] = (byte)k;
    for (++k; i < N1 + N2; ++i, k += 2) Indx2Units[i] = (byte)k;
    for (++k; i < N1 + N2 + N3; ++i, k += 3) Indx2Units[i] = (byte)k;
    for (++k; i < N1 + N2 + N3 + N4; ++i, k += 4) Indx2Units[i] = (byte)k;
    for (k = i = 0; k < 128; ++k) {
      i += Indx2Units[i] < k + 1 ? 1 : 0;
      Units2Indx[k] = (byte)i;
    }
    NS2BSIndx[0] = 2 * 0;
    NS2BSIndx[1] = 2 * 1;
    for (i = 2; i < 11; ++i) NS2BSIndx[i] = 2 * 2;
    for (i = 11; i < 256; ++i) NS2BSIndx[i] = 2 * 3;
    for (i = 0; i < UpFreq; ++i) QTable[i] = (byte)i;
    int m, step;
    for (m = i = UpFreq, k = step = 1; i < 260; ++i) {
      QTable[i] = (byte)m;
      if (--k == 0) {
        k = ++step;
        ++m;
      }
    }
  }

  // ── memory: one array, offsets as pointers ────────────────────────────────

  private const int S0 = 8;                 // the gluing list head (a MEM_BLK)
  private const int BListBase = 24;         // NIndexes BLK_NODEs of 8 bytes
  private const int HeapBase = 512;
  private readonly byte[] _m;

  private byte B(int a) => this._m[a];
  private void B(int a, int v) => this._m[a] = (byte)v;
  private ushort W(int a) => BinaryPrimitives.ReadUInt16LittleEndian(this._m.AsSpan(a));
  private void W(int a, int v) => BinaryPrimitives.WriteUInt16LittleEndian(this._m.AsSpan(a), (ushort)v);
  private int D(int a) => (int)BinaryPrimitives.ReadUInt32LittleEndian(this._m.AsSpan(a));
  private void D(int a, int v) => BinaryPrimitives.WriteUInt32LittleEndian(this._m.AsSpan(a), (uint)v);
  private uint DU(int a) => BinaryPrimitives.ReadUInt32LittleEndian(this._m.AsSpan(a));
  private void DU(int a, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(this._m.AsSpan(a), v);

  // STATE: Symbol @0, Freq @1, Successor @2
  private byte Sym(int s) => this._m[s];
  private byte Freq(int s) => this._m[s + 1];
  private void Freq(int s, int v) => this._m[s + 1] = (byte)v;
  private int Succ(int s) => this.D(s + 2);
  private void Succ(int s, int v) => this.D(s + 2, v);
  private const int StateSize = 6;

  // PPM_CONTEXT: NumStats @0, Flags @1, SummFreq @2, Stats @4, Suffix @8; oneState @2
  private byte NumStats(int c) => this._m[c];
  private void NumStats(int c, int v) => this._m[c] = (byte)v;
  private byte Flags(int c) => this._m[c + 1];
  private void Flags(int c, int v) => this._m[c + 1] = (byte)v;
  private ushort SummFreq(int c) => this.W(c + 2);
  private void SummFreq(int c, int v) => this.W(c + 2, v);
  private int Stats(int c) => this.D(c + 4);
  private void Stats(int c, int v) => this.D(c + 4, v);
  private int Suffix(int c) => this.D(c + 8);
  private void Suffix(int c, int v) => this.D(c + 8, v);
  private static int OneState(int c) => c + 2;

  private void Swap(int s1, int s2) {
    var t1 = this.W(s1);
    var t2 = this.Succ(s1);
    this.W(s1, this.W(s2));
    this.Succ(s1, this.Succ(s2));
    this.W(s2, t1);
    this.Succ(s2, t2);
  }

  private void StateCpy(int s1, int s2) {
    this.W(s1, this.W(s2));
    this.Succ(s1, this.Succ(s2));
  }

  // BLK_NODE: Stamp @0, next @4; MEM_BLK: NU @8
  private static int BList(int i) => BListBase + 8 * i;
  private uint Stamp(int n) => this.DU(n);
  private int Next(int n) => this.D(n + 4);
  private bool Avail(int n) => this.Next(n) != 0;

  private void Link(int n, int p) {
    this.D(p + 4, this.Next(n));
    this.D(n + 4, p);
  }

  private void Unlink(int n) => this.D(n + 4, this.Next(this.Next(n)));

  private int Remove(int n) {
    var p = this.Next(n);
    this.Unlink(n);
    this.DU(n, this.Stamp(n) - 1);
    return p;
  }

  private void Insert(int n, int pv, int nu) {
    this.Link(n, pv);
    this.DU(pv, ~0u);
    this.D(pv + 8, nu);
    this.DU(n, this.Stamp(n) + 1);
  }

  private uint _glueCount;
  private readonly int _subAllocatorSize;
  private readonly int _heapStart = HeapBase;
  private int _pText, _unitsStart, _loUnit, _hiUnit;

  private static int U2B(int nu) => 8 * nu + 4 * nu;

  private void SplitBlock(int pv, int oldIndx, int newIndx) {
    var uDiff = Indx2Units[oldIndx] - Indx2Units[newIndx];
    var p = pv + U2B(Indx2Units[newIndx]);
    int i;
    if (Indx2Units[i = Units2Indx[uDiff - 1]] != uDiff) {
      var k = Indx2Units[--i];
      this.Insert(BList(i), p, k);
      p += U2B(k);
      uDiff -= k;
    }
    this.Insert(BList(Units2Indx[uDiff - 1]), p, uDiff);
  }

  private uint GetUsedMemory() {
    var ret = (uint)this._subAllocatorSize - (uint)(this._hiUnit - this._loUnit) - (uint)(this._unitsStart - this._pText);
    for (var i = 0; i < NIndexes; ++i)
      ret -= (uint)(UnitSize * Indx2Units[i]) * this.Stamp(BList(i));
    return ret;
  }

  private void InitSubAllocator() {
    Array.Clear(this._m, BListBase, 8 * NIndexes);
    this._hiUnit = (this._pText = this._heapStart) + this._subAllocatorSize;
    var diff = UnitSize * (this._subAllocatorSize / 8 / UnitSize * 7);
    this._loUnit = this._unitsStart = this._hiUnit - diff;
    this._glueCount = 0;
  }

  private void GlueFreeBlocks() {
    if (this._loUnit != this._hiUnit)
      this._m[this._loUnit] = 0;
    var p0 = S0;
    this.D(S0 + 4, 0);
    for (var i = 0; i < NIndexes; ++i)
      while (this.Avail(BList(i))) {
        var p = this.Remove(BList(i));
        if (this.D(p + 8) == 0)
          continue;
        int p1;
        while (this.Stamp(p1 = p + this.D(p + 8) * UnitSize) == ~0u) {
          this.D(p + 8, this.D(p + 8) + this.D(p1 + 8));
          this.D(p1 + 8, 0);
        }
        this.Link(p0, p);
        p0 = p;
      }
    while (this.Avail(S0)) {
      var p = this.Remove(S0);
      var sz = this.D(p + 8);
      if (sz == 0)
        continue;
      for (; sz > 128; sz -= 128, p += 128 * UnitSize)
        this.Insert(BList(NIndexes - 1), p, 128);
      int i;
      if (Indx2Units[i = Units2Indx[sz - 1]] != sz) {
        var k = sz - Indx2Units[--i];
        this.Insert(BList(k - 1), p + (sz - k) * UnitSize, k);
      }
      this.Insert(BList(i), p, Indx2Units[i]);
    }
    this._glueCount = 1 << 13;
  }

  private int AllocUnitsRare(int indx) {
    var i = indx;
    if (this._glueCount == 0) {
      this.GlueFreeBlocks();
      if (this.Avail(BList(i)))
        return this.Remove(BList(i));
    }
    do {
      if (++i == NIndexes) {
        --this._glueCount;
        i = U2B(Indx2Units[indx]);
        return this._unitsStart - this._pText > i ? (this._unitsStart -= i) : 0;
      }
    } while (!this.Avail(BList(i)));
    var ret = this.Remove(BList(i));
    this.SplitBlock(ret, i, indx);
    return ret;
  }

  private int AllocUnits(int nu) {
    int indx = Units2Indx[nu - 1];
    if (this.Avail(BList(indx)))
      return this.Remove(BList(indx));
    var ret = this._loUnit;
    this._loUnit += U2B(Indx2Units[indx]);
    if (this._loUnit <= this._hiUnit)
      return ret;
    this._loUnit -= U2B(Indx2Units[indx]);
    return this.AllocUnitsRare(indx);
  }

  private int AllocContext() {
    if (this._hiUnit != this._loUnit)
      return this._hiUnit -= UnitSize;
    if (this.Avail(BList(0)))
      return this.Remove(BList(0));
    return this.AllocUnitsRare(0);
  }

  private void UnitsCpy(int dest, int src, int nu) => Array.Copy(this._m, src, this._m, dest, nu * UnitSize);

  private int ExpandUnits(int oldPtr, int oldNu) {
    int i0 = Units2Indx[oldNu - 1], i1 = Units2Indx[oldNu - 1 + 1];
    if (i0 == i1)
      return oldPtr;
    var ptr = this.AllocUnits(oldNu + 1);
    if (ptr != 0) {
      this.UnitsCpy(ptr, oldPtr, oldNu);
      this.Insert(BList(i0), oldPtr, oldNu);
    }
    return ptr;
  }

  private int ShrinkUnits(int oldPtr, int oldNu, int newNu) {
    int i0 = Units2Indx[oldNu - 1], i1 = Units2Indx[newNu - 1];
    if (i0 == i1)
      return oldPtr;
    if (this.Avail(BList(i1))) {
      var ptr = this.Remove(BList(i1));
      this.UnitsCpy(ptr, oldPtr, newNu);
      this.Insert(BList(i0), oldPtr, Indx2Units[i0]);
      return ptr;
    }
    this.SplitBlock(oldPtr, i0, i1);
    return oldPtr;
  }

  private void FreeUnits(int ptr, int nu) {
    int indx = Units2Indx[nu - 1];
    this.Insert(BList(indx), ptr, Indx2Units[indx]);
  }

  private void SpecialFreeUnit(int ptr) {
    if (ptr != this._unitsStart)
      this.Insert(BList(0), ptr, 1);
    else {
      this.DU(ptr, ~0u);
      this._unitsStart += UnitSize;
    }
  }

  private int MoveUnitsUp(int oldPtr, int nu) {
    int indx = Units2Indx[nu - 1];
    if (oldPtr > this._unitsStart + 16 * 1024 || oldPtr > this.Next(BList(indx)))
      return oldPtr;
    var ptr = this.Remove(BList(indx));
    this.UnitsCpy(ptr, oldPtr, nu);
    nu = Indx2Units[indx];
    if (oldPtr != this._unitsStart)
      this.Insert(BList(indx), oldPtr, nu);
    else
      this._unitsStart += U2B(nu);
    return ptr;
  }

  private void ExpandTextArea() {
    Span<int> count = stackalloc int[NIndexes];
    int p;
    while (this.Stamp(p = this._unitsStart) == ~0u) {
      var nu = this.D(p + 8);
      this._unitsStart = p + nu * UnitSize;
      ++count[Units2Indx[nu - 1]];
      this.DU(p, 0);
    }
    for (var i = 0; i < NIndexes; ++i)
      for (p = BList(i); count[i] != 0; p = this.Next(p))
        while (this.Stamp(this.Next(p)) == 0) {
          this.Unlink(p);
          this.DU(BList(i), this.Stamp(BList(i)) - 1);
          if (--count[i] == 0)
            break;
        }
  }

  // ── model state ───────────────────────────────────────────────────────────

  private readonly ushort[] _seeSumm = new ushort[24 * 32 + 1];   // the last one is the dummy
  private readonly byte[] _seeShift = new byte[24 * 32 + 1];
  private readonly byte[] _seeCount = new byte[24 * 32 + 1];
  private const int DummySee = 24 * 32;
  private readonly ushort[,] _binSumm = new ushort[25, 64];
  private readonly byte[] _charMask = new byte[256];
  private int _maxContext, _foundState;
  private int _initEsc, _orderFall, _runLength, _initRL, _maxOrder;
  private byte _numMasked, _prevSuccess, _escCount, _printCount;
  private int _mrMethod;
  private uint _lowCount, _highCount, _scale;
  private uint _low, _code, _range;

  private readonly byte[] _input;
  private int _inPos;

  private FreeArcPpmd(byte[] input, int memory) {
    this._input = input;
    this._subAllocatorSize = memory;
    this._m = new byte[HeapBase + memory + 4 * UnitSize];
    // DummySEE2Cont is overwritten with the PPMd signature; only its Summ/Shift are ever touched.
    this._seeSumm[DummySee] = 0xAF8F;
    this._seeShift[DummySee] = 0xAC;
    this._seeCount[DummySee] = 0x84;
  }

  private uint GetByte() => this._inPos < this._input.Length ? this._input[this._inPos++] : PastEnd(ref this._inPos, this._input.Length);

  private static uint PastEnd(ref int pos, int length) {
    if (++pos > length + 64)
      throw new InvalidDataException("FreeArc ppmd: stream ends before the model's end marker.");
    return 0xFFFFFFFF; // EOF (-1) OR-ed into the code, as the original stream reader returns it
  }

  /// <summary>Decodes a whole FreeArc ppmd stream.</summary>
  /// <param name="input">The coded bytes.</param>
  /// <param name="order">Model order (2..128).</param>
  /// <param name="memory">Model memory in bytes.</param>
  /// <param name="restoreMethod">0 restart, 1 cut off, 2 freeze.</param>
  /// <param name="limit">Most bytes the stream may decode to (the block's size, or -1 for no limit).</param>
  public static byte[] Decode(byte[] input, int order, int memory, int restoreMethod, long limit) {
    if (order is < 2 or > MaxO)
      throw new InvalidDataException($"FreeArc ppmd: order {order} is outside 2..128.");
    if (memory < 2048)
      throw new InvalidDataException("FreeArc ppmd: model memory is below the minimum.");
    if (restoreMethod is < MrmRestart or > MrmFreeze)
      throw new InvalidDataException($"FreeArc ppmd: unknown restore method {restoreMethod}.");
    return new FreeArcPpmd(input, memory).Run(order, restoreMethod, limit);
  }

  private byte[] Run(int order, int mrMethod, long limit) {
    using var output = new MemoryStream();
    this._low = this._code = 0;
    this._range = 0xFFFFFFFF;
    for (var i = 0; i < 4; ++i)
      this._code = (this._code << 8) | this.GetByte();
    this.StartModelRare(order, mrMethod);
    var minContext = this._maxContext;
    for (var ns = this.NumStats(minContext); ;) {
      if (ns != 0)
        this.DecodeSymbol1(minContext);
      else
        this.DecodeBinSymbol(minContext);
      this.RemoveSubrange();
      while (this._foundState == 0) {
        this.Normalize();
        do {
          ++this._orderFall;
          minContext = this.Suffix(minContext);
          if (minContext == 0)
            return output.ToArray();
        } while (this.NumStats(minContext) == this._numMasked);
        this.DecodeSymbol2(minContext);
        this.RemoveSubrange();
      }
      output.WriteByte(this.Sym(this._foundState));
      if (limit >= 0 && output.Length > limit)
        throw new InvalidDataException("FreeArc ppmd: stream decodes past its block.");
      if (this._orderFall == 0 && this.Succ(this._foundState) >= this._unitsStart)
        this._maxContext = this.Succ(this._foundState);
      else {
        this.UpdateModel(minContext);
        if (this._escCount == 0)
          this.ClearMask();
      }
      ns = this.NumStats(minContext = this._maxContext);
      this.Normalize();
    }
  }

  // ── range coder ───────────────────────────────────────────────────────────

  private void Normalize() {
    while (true) {
      if ((this._low ^ (this._low + this._range)) >= Top) {
        if (this._range >= Bot)
          break;
        this._range = (uint)-this._low & (Bot - 1);
      }
      this._code = (this._code << 8) | this.GetByte();
      this._range <<= 8;
      this._low <<= 8;
    }
  }

  private uint GetCurrentCount() {
    this._range /= this._scale;
    if (this._range == 0)
      throw new InvalidDataException("FreeArc ppmd: range coder underflow.");
    return (this._code - this._low) / this._range;
  }

  private uint GetCurrentShiftCount(int shift) => (this._code - this._low) / (this._range >>= shift);

  private void RemoveSubrange() {
    this._low += this._range * this._lowCount;
    this._range *= this._highCount - this._lowCount;
  }

  // ── SEE ───────────────────────────────────────────────────────────────────

  private void SeeInit(int s, int initVal) {
    this._seeShift[s] = PeriodBits - 4;
    this._seeSumm[s] = (ushort)(initVal << this._seeShift[s]);
    this._seeCount[s] = 7;
  }

  private uint SeeGetMean(int s) {
    uint ret = (uint)(this._seeSumm[s] >> this._seeShift[s]);
    this._seeSumm[s] = (ushort)(this._seeSumm[s] - ret);
    return ret + (ret == 0 ? 1u : 0u);
  }

  private void SeeUpdate(int s) {
    if (this._seeShift[s] < PeriodBits && --this._seeCount[s] == 0) {
      this._seeSumm[s] = (ushort)(this._seeSumm[s] + this._seeSumm[s]);
      this._seeCount[s] = (byte)(3 << this._seeShift[s]++);
    }
  }

  // ── model ─────────────────────────────────────────────────────────────────

  private void StartModelRare(int maxOrder, int mrMethod) {
    Array.Clear(this._charMask);
    this._escCount = this._printCount = 1;
    this._orderFall = this._maxOrder = maxOrder;
    this._mrMethod = mrMethod;
    this.InitSubAllocator();
    this._runLength = this._initRL = -(this._maxOrder < 12 ? this._maxOrder : 12) - 1;
    this._maxContext = this.AllocContext();
    this.Suffix(this._maxContext, 0);
    this.NumStats(this._maxContext, 255);
    this.SummFreq(this._maxContext, 255 + 2);
    this.Stats(this._maxContext, this.AllocUnits(256 / 2));
    var stats = this.Stats(this._maxContext);
    int i;
    for (this._prevSuccess = 0, i = 0; i < 256; ++i) {
      var s = stats + i * StateSize;
      this.B(s, i);
      this.Freq(s, 1);
      this.Succ(s, 0);
    }
    int m;
    var k = 0;
    for (i = m = 0; m < 25; ++m) {
      while (QTable[i] == m)
        ++i;
      for (k = 0; k < 8; ++k)
        this._binSumm[m, k] = (ushort)(BinScale - InitBinEsc[k] / (i + 1));
      for (k = 8; k < 64; k += 8)
        for (var j = 0; j < 8; ++j)
          this._binSumm[m, k + j] = this._binSumm[m, j];
    }
    for (i = m = 0; m < 24; ++m) {
      while (QTable[i + 3] == m + 3)
        ++i;
      this.SeeInit(m * 32, 2 * i + 5);
      for (k = 1; k < 32; ++k) {
        this._seeSumm[m * 32 + k] = this._seeSumm[m * 32];
        this._seeShift[m * 32 + k] = this._seeShift[m * 32];
        this._seeCount[m * 32 + k] = this._seeCount[m * 32];
      }
    }
  }

  private void Refresh(int ctx, int oldNu, bool scale) {
    int n = this.NumStats(ctx), sc = scale ? 1 : 0;
    var p = this.ShrinkUnits(this.Stats(ctx), oldNu, (n + 2) >> 1);
    this.Stats(ctx, p);
    this.Flags(ctx, (this.Flags(ctx) & (0x10 + 0x04 * sc)) + 0x08 * (this.Sym(p) >= 0x40 ? 1 : 0));
    var escFreq = this.SummFreq(ctx) - this.Freq(p);
    this.Freq(p, (this.Freq(p) + sc) >> sc);
    var summ = (int)this.Freq(p);
    do {
      p += StateSize;
      escFreq -= this.Freq(p);
      this.Freq(p, (this.Freq(p) + sc) >> sc);
      summ += this.Freq(p);
      this.Flags(ctx, this.Flags(ctx) | 0x08 * (this.Sym(p) >= 0x40 ? 1 : 0));
    } while (--n != 0);
    summ += (escFreq + sc) >> sc;
    this.SummFreq(ctx, summ);
  }

  private int CutOff(int ctx, int order) {
    int n, tmp, p;
    if (this.NumStats(ctx) == 0) {
      p = OneState(ctx);
      if (this.Succ(p) >= this._unitsStart) {
        if (order < this._maxOrder)
          this.Succ(p, this.CutOff(this.Succ(p), order + 1));
        else
          this.Succ(p, 0);
        if (this.Succ(p) == 0 && order > OBound) {
          this.SpecialFreeUnit(ctx);
          return 0;
        }
        return ctx;
      }
      this.SpecialFreeUnit(ctx);
      return 0;
    }
    this.Stats(ctx, this.MoveUnitsUp(this.Stats(ctx), tmp = (this.NumStats(ctx) + 2) >> 1));
    var stats = this.Stats(ctx);
    for (p = stats + (n = this.NumStats(ctx)) * StateSize; p >= stats; p -= StateSize)
      if (this.Succ(p) < this._unitsStart) {
        this.Succ(p, 0);
        this.Swap(p, stats + n-- * StateSize);
      } else if (order < this._maxOrder)
        this.Succ(p, this.CutOff(this.Succ(p), order + 1));
      else
        this.Succ(p, 0);
    if (n != this.NumStats(ctx) && order != 0) {
      this.NumStats(ctx, n);
      p = stats;
      if (n < 0) {
        this.FreeUnits(p, tmp);
        this.SpecialFreeUnit(ctx);
        return 0;
      }
      if (n == 0) {
        this.Flags(ctx, (this.Flags(ctx) & 0x10) + 0x08 * (this.Sym(p) >= 0x40 ? 1 : 0));
        this.StateCpy(OneState(ctx), p);
        this.FreeUnits(p, tmp);
        this.Freq(OneState(ctx), (this.Freq(OneState(ctx)) + 11) >> 3);
      } else
        this.Refresh(ctx, tmp, this.SummFreq(ctx) > 16 * n);
    }
    return ctx;
  }

  private int RemoveBinConts(int ctx, int order) {
    int p;
    if (this.NumStats(ctx) == 0) {
      p = OneState(ctx);
      if (this.Succ(p) >= this._unitsStart && order < this._maxOrder)
        this.Succ(p, this.RemoveBinConts(this.Succ(p), order + 1));
      else
        this.Succ(p, 0);
      var suffix = this.Suffix(ctx);
      if (this.Succ(p) == 0 && (this.NumStats(suffix) == 0 || this.Flags(suffix) == 0xFF)) {
        this.FreeUnits(ctx, 1);
        return 0;
      }
      return ctx;
    }
    var stats = this.Stats(ctx);
    for (p = stats + this.NumStats(ctx) * StateSize; p >= stats; p -= StateSize)
      if (this.Succ(p) >= this._unitsStart && order < this._maxOrder)
        this.Succ(p, this.RemoveBinConts(this.Succ(p), order + 1));
      else
        this.Succ(p, 0);
    return ctx;
  }

  private void RestoreModelRare(int pc1, int minContext, int fSuccessor) {
    int pc;
    for (pc = this._maxContext, this._pText = this._heapStart; pc != pc1; pc = this.Suffix(pc)) {
      this.NumStats(pc, this.NumStats(pc) - 1);
      if (this.NumStats(pc) == 0) {
        var p = this.Stats(pc);
        this.Flags(pc, (this.Flags(pc) & 0x10) + 0x08 * (this.Sym(p) >= 0x40 ? 1 : 0));
        this.StateCpy(OneState(pc), p);
        this.SpecialFreeUnit(p);
        this.Freq(OneState(pc), (this.Freq(OneState(pc)) + 11) >> 3);
      } else
        this.Refresh(pc, (this.NumStats(pc) + 3) >> 1, false);
    }
    for (; pc != minContext; pc = this.Suffix(pc))
      if (this.NumStats(pc) == 0)
        this.Freq(OneState(pc), this.Freq(OneState(pc)) - (this.Freq(OneState(pc)) >> 1));
      else {
        this.SummFreq(pc, this.SummFreq(pc) + 4);
        if (this.SummFreq(pc) > 128 + 4 * this.NumStats(pc))
          this.Refresh(pc, (this.NumStats(pc) + 2) >> 1, true);
      }
    if (this._mrMethod > MrmFreeze) {
      this._maxContext = fSuccessor;
      this._glueCount += (this.Stamp(BList(1)) & 1) == 0 ? 1u : 0u;
    } else if (this._mrMethod == MrmFreeze) {
      while (this.Suffix(this._maxContext) != 0)
        this._maxContext = this.Suffix(this._maxContext);
      this.RemoveBinConts(this._maxContext, 0);
      this._mrMethod++;
      this._glueCount = 0;
      this._orderFall = this._maxOrder;
    } else if (this._mrMethod == MrmRestart || this.GetUsedMemory() < (uint)(this._subAllocatorSize >> 1)) {
      this.StartModelRare(this._maxOrder, this._mrMethod);
      this._escCount = 0;
      this._printCount = 0xFF;
    } else {
      while (this.Suffix(this._maxContext) != 0)
        this._maxContext = this.Suffix(this._maxContext);
      do {
        this.CutOff(this._maxContext, 0);
        this.ExpandTextArea();
      } while (this.GetUsedMemory() > 3 * (uint)(this._subAllocatorSize >> 2));
      this._glueCount = 0;
      this._orderFall = this._maxOrder;
    }
  }

  private int ReduceOrder(int p, int pc) {
    Span<int> ps = stackalloc int[MaxO];
    var pps = 0;
    int pc1 = pc, upBranch = this._pText;
    var sym = this.Sym(this._foundState);
    ps[pps++] = this._foundState;
    this.Succ(this._foundState, upBranch);
    ++this._orderFall;
    var entered = p != 0;
    if (entered)
      pc = this.Suffix(pc);
    while (true) {
      if (!entered) {
        if (this.Suffix(pc) == 0) {
          if (this._mrMethod > MrmFreeze) {
            do { this.Succ(ps[--pps], pc); } while (pps != 0);
            this._pText = this._heapStart + 1;
            this._orderFall = 1;
          }
          return pc;
        }
        pc = this.Suffix(pc);
        if (this.NumStats(pc) != 0) {
          if (this.Sym(p = this.Stats(pc)) != sym) {
            byte t;
            do {
              t = this.Sym(p + StateSize);
              p += StateSize;
            } while (t != sym);
          }
          var tmp = 2 * (this.Freq(p) < MaxFreq - 9 ? 1 : 0);
          this.Freq(p, this.Freq(p) + tmp);
          this.SummFreq(pc, this.SummFreq(pc) + tmp);
        } else {
          p = OneState(pc);
          this.Freq(p, this.Freq(p) + (this.Freq(p) < 32 ? 1 : 0));
        }
      }
      entered = false;
      if (this.Succ(p) != 0)
        break;
      if (pps >= MaxO)
        throw new InvalidDataException("FreeArc ppmd: context chain deeper than the model order.");
      ps[pps++] = p;
      this.Succ(p, upBranch);
      ++this._orderFall;
    }
    if (this._mrMethod > MrmFreeze) {
      pc = this.Succ(p);
      do { this.Succ(ps[--pps], pc); } while (pps != 0);
      this._pText = this._heapStart + 1;
      this._orderFall = 1;
      return pc;
    }
    if (this.Succ(p) <= upBranch) {
      var p1 = this._foundState;
      this._foundState = p;
      this.Succ(p, this.CreateSuccessors(false, 0, pc));
      this._foundState = p1;
    }
    if (this._orderFall == 1 && pc1 == this._maxContext) {
      this.Succ(this._foundState, this.Succ(p));
      --this._pText;
    }
    return this.Succ(p);
  }

  private void Rescale(int ctx) {
    int oldNu, n = this.NumStats(ctx);
    uint adder, escFreq;
    var stats = this.Stats(ctx);
    int p;
    for (p = this._foundState; p != stats; p -= StateSize)
      this.Swap(p, p - StateSize);
    this.Freq(p, this.Freq(p) + 4);
    this.SummFreq(ctx, this.SummFreq(ctx) + 4);
    escFreq = (uint)(this.SummFreq(ctx) - this.Freq(p));
    adder = this._orderFall != 0 || this._mrMethod > MrmFreeze ? 1u : 0u;
    this.Freq(p, (int)((this.Freq(p) + adder) >> 1));
    var summ = (uint)this.Freq(p);
    Span<byte> tmp = stackalloc byte[StateSize];
    do {
      p += StateSize;
      escFreq -= this.Freq(p);
      this.Freq(p, (int)((this.Freq(p) + adder) >> 1));
      summ += this.Freq(p);
      if (this.Freq(p) > this.Freq(p - StateSize)) {
        var p1 = p;
        this._m.AsSpan(p1, StateSize).CopyTo(tmp);
        do {
          this.StateCpy(p1, p1 - StateSize);
          p1 -= StateSize;
        } while (tmp[1] > this.Freq(p1 - StateSize));
        tmp.CopyTo(this._m.AsSpan(p1, StateSize));
      }
    } while (--n != 0);
    this.SummFreq(ctx, (int)summ);
    if (this.Freq(p) == 0) {
      n = 0;
      do { ++n; } while (this.Freq(p -= StateSize) == 0);
      escFreq += (uint)n;
      oldNu = (this.NumStats(ctx) + 2) >> 1;
      this.NumStats(ctx, this.NumStats(ctx) - n);
      if (this.NumStats(ctx) == 0) {
        this._m.AsSpan(stats, StateSize).CopyTo(tmp);
        var f = (int)((2 * tmp[1] + escFreq - 1) / escFreq);
        if (f > MaxFreq / 3)
          f = MaxFreq / 3;
        tmp[1] = (byte)f;
        this.FreeUnits(stats, oldNu);
        tmp.CopyTo(this._m.AsSpan(OneState(ctx), StateSize));
        this.Flags(ctx, (this.Flags(ctx) & 0x10) + 0x08 * (tmp[0] >= 0x40 ? 1 : 0));
        this._foundState = OneState(ctx);
        return;
      }
      stats = this.ShrinkUnits(stats, oldNu, (this.NumStats(ctx) + 2) >> 1);
      this.Stats(ctx, stats);
      this.Flags(ctx, this.Flags(ctx) & ~0x08);
      n = this.NumStats(ctx);
      this.Flags(ctx, this.Flags(ctx) | 0x08 * (this.Sym(p = stats) >= 0x40 ? 1 : 0));
      do {
        p += StateSize;
        this.Flags(ctx, this.Flags(ctx) | 0x08 * (this.Sym(p) >= 0x40 ? 1 : 0));
      } while (--n != 0);
    }
    escFreq -= escFreq >> 1;
    this.SummFreq(ctx, this.SummFreq(ctx) + (int)escFreq);
    this.Flags(ctx, this.Flags(ctx) | 0x04);
    this._foundState = stats;
  }

  private int CreateSuccessors(bool skip, int p, int pc) {
    var upBranch = this.Succ(this._foundState);
    Span<int> ps = stackalloc int[MaxO];
    var pps = 0;
    var sym = this.Sym(this._foundState);
    var noLoop = false;
    if (!skip) {
      ps[pps++] = this._foundState;
      if (this.Suffix(pc) == 0)
        noLoop = true;
    }
    if (!noLoop) {
      var entered = p != 0;
      if (entered)
        pc = this.Suffix(pc);
      do {
        if (!entered) {
          pc = this.Suffix(pc);
          if (this.NumStats(pc) != 0) {
            if (this.Sym(p = this.Stats(pc)) != sym) {
              byte t;
              do {
                t = this.Sym(p + StateSize);
                p += StateSize;
              } while (t != sym);
            }
            var tmp = this.Freq(p) < MaxFreq - 9 ? 1 : 0;
            this.Freq(p, this.Freq(p) + tmp);
            this.SummFreq(pc, this.SummFreq(pc) + tmp);
          } else {
            p = OneState(pc);
            this.Freq(p, this.Freq(p) + ((this.NumStats(this.Suffix(pc)) == 0 ? 1 : 0) & (this.Freq(p) < 24 ? 1 : 0)));
          }
        }
        entered = false;
        if (this.Succ(p) != upBranch) {
          pc = this.Succ(p);
          break;
        }
        if (pps >= MaxO)
          throw new InvalidDataException("FreeArc ppmd: context chain deeper than the model order.");
        ps[pps++] = p;
      } while (this.Suffix(pc) != 0);
    }
    if (pps == 0)
      return pc;
    // ct: a one-state context for the symbol that followed in the text
    var flags = 0x10 * (sym >= 0x40 ? 1 : 0);
    var ctSym = this.B(upBranch);
    var ctSucc = upBranch + 1;
    flags |= 0x08 * (ctSym >= 0x40 ? 1 : 0);
    int ctFreq;
    if (this.NumStats(pc) != 0) {
      if (this.Sym(p = this.Stats(pc)) != ctSym) {
        byte t;
        do {
          t = this.Sym(p + StateSize);
          p += StateSize;
        } while (t != ctSym);
      }
      var cf = (uint)(this.Freq(p) - 1);
      var s0 = (uint)(this.SummFreq(pc) - this.NumStats(pc)) - cf;
      ctFreq = (int)(1 + (2 * cf <= s0 ? (5 * cf > s0 ? 1u : 0u) : (cf + 2 * s0 - 3) / s0));
    } else
      ctFreq = this.Freq(OneState(pc));
    do {
      var pc1 = this.AllocContext();
      if (pc1 == 0)
        return 0;
      this.NumStats(pc1, 0);
      this.Flags(pc1, flags);
      this.B(OneState(pc1), ctSym);
      this.Freq(OneState(pc1), ctFreq);
      this.Succ(OneState(pc1), ctSucc);
      this.Suffix(pc1, pc);
      this.Succ(ps[--pps], pc = pc1);
    } while (pps != 0);
    return pc;
  }

  private void UpdateModel(int minContext) {
    var p = 0;
    int successor, fSuccessor, pc, pc1 = this._maxContext;
    uint ns1, ns, cf, sf, s0, fFreq = this.Freq(this._foundState);
    byte flag, fSymbol = this.Sym(this._foundState);
    fSuccessor = this.Succ(this._foundState);
    pc = this.Suffix(minContext);
    if (fFreq < MaxFreq / 4 && pc != 0) {
      if (this.NumStats(pc) != 0) {
        if (this.Sym(p = this.Stats(pc)) != fSymbol) {
          byte sym;
          do {
            sym = this.Sym(p + StateSize);
            p += StateSize;
          } while (sym != fSymbol);
          if (this.Freq(p) >= this.Freq(p - StateSize)) {
            this.Swap(p, p - StateSize);
            p -= StateSize;
          }
        }
        cf = 2u * (this.Freq(p) < MaxFreq - 9 ? 1u : 0u);
        this.Freq(p, (int)(this.Freq(p) + cf));
        this.SummFreq(pc, (int)(this.SummFreq(pc) + cf));
      } else {
        p = OneState(pc);
        this.Freq(p, this.Freq(p) + (this.Freq(p) < 32 ? 1 : 0));
      }
    }
    if (this._orderFall == 0 && fSuccessor != 0) {
      this.Succ(this._foundState, this.CreateSuccessors(true, p, minContext));
      if (this.Succ(this._foundState) == 0) {
        this.RestoreModelRare(pc1, minContext, fSuccessor);
        return;
      }
      this._maxContext = this.Succ(this._foundState);
      return;
    }
    this.B(this._pText++, fSymbol);
    successor = this._pText;
    if (this._pText >= this._unitsStart) {
      this.RestoreModelRare(pc1, minContext, fSuccessor);
      return;
    }
    if (fSuccessor != 0) {
      if (fSuccessor < this._unitsStart)
        fSuccessor = this.CreateSuccessors(false, p, minContext);
    } else
      fSuccessor = this.ReduceOrder(p, minContext);
    if (fSuccessor == 0) {
      this.RestoreModelRare(pc1, minContext, fSuccessor);
      return;
    }
    if (--this._orderFall == 0) {
      successor = fSuccessor;
      this._pText -= this._maxContext != minContext ? 1 : 0;
    } else if (this._mrMethod > MrmFreeze) {
      successor = fSuccessor;
      this._pText = this._heapStart;
      this._orderFall = 0;
    }
    s0 = this.SummFreq(minContext) - (ns = this.NumStats(minContext)) - fFreq;
    for (flag = (byte)(0x08 * (fSymbol >= 0x40 ? 1 : 0)); pc1 != minContext; pc1 = this.Suffix(pc1)) {
      if ((ns1 = this.NumStats(pc1)) != 0) {
        if ((ns1 & 1) != 0) {
          p = this.ExpandUnits(this.Stats(pc1), (int)(ns1 + 1) >> 1);
          if (p == 0) {
            this.RestoreModelRare(pc1, minContext, fSuccessor);
            return;
          }
          this.Stats(pc1, p);
        }
        this.SummFreq(pc1, this.SummFreq(pc1) + (3 * ns1 + 1 < ns ? 1 : 0));
      } else {
        p = this.AllocUnits(1);
        if (p == 0) {
          this.RestoreModelRare(pc1, minContext, fSuccessor);
          return;
        }
        this.StateCpy(p, OneState(pc1));
        this.Stats(pc1, p);
        if (this.Freq(p) < MaxFreq / 4 - 1)
          this.Freq(p, this.Freq(p) + this.Freq(p));
        else
          this.Freq(p, MaxFreq - 4);
        this.SummFreq(pc1, this.Freq(p) + this._initEsc + (ns > 2 ? 1 : 0));
      }
      cf = 2 * fFreq * (uint)(this.SummFreq(pc1) + 6);
      sf = s0 + this.SummFreq(pc1);
      if (cf < 6 * sf) {
        cf = 1 + (cf > sf ? 1u : 0u) + (cf >= 4 * sf ? 1u : 0u);
        this.SummFreq(pc1, this.SummFreq(pc1) + 4);
      } else {
        cf = 4 + (cf > 9 * sf ? 1u : 0u) + (cf > 12 * sf ? 1u : 0u) + (cf > 15 * sf ? 1u : 0u);
        this.SummFreq(pc1, (int)(this.SummFreq(pc1) + cf));
      }
      this.NumStats(pc1, this.NumStats(pc1) + 1);
      p = this.Stats(pc1) + this.NumStats(pc1) * StateSize;
      this.Succ(p, successor);
      this.B(p, fSymbol);
      this.Freq(p, (int)cf);
      this.Flags(pc1, this.Flags(pc1) | flag);
    }
    this._maxContext = fSuccessor;
  }

  private static int GetMean(int summ, int shift, int round) => (summ + (1 << (shift - round))) >> shift;

  private void DecodeBinSymbol(int ctx) {
    var indx = NS2BSIndx[this.NumStats(this.Suffix(ctx))] + this._prevSuccess + this.Flags(ctx);
    var rs = OneState(ctx);
    var row = QTable[this.Freq(rs) - 1];
    var col = (indx + ((this._runLength >> 26) & 0x20)) & 0xFF;
    int bs = this._binSumm[row, col];
    if (this.GetCurrentShiftCount(TotBits) < bs) {
      this._foundState = rs;
      this.Freq(rs, this.Freq(rs) + (this.Freq(rs) < 196 ? 1 : 0));
      this._lowCount = 0;
      this._highCount = (uint)bs;
      this._binSumm[row, col] = (ushort)(bs + Interval - GetMean(bs, PeriodBits, 2));
      this._prevSuccess = 1;
      ++this._runLength;
    } else {
      this._lowCount = (uint)bs;
      bs = (ushort)(bs - GetMean(bs, PeriodBits, 2));
      this._binSumm[row, col] = (ushort)bs;
      this._highCount = BinScale;
      this._initEsc = ExpEscape[bs >> 10];
      this._charMask[this.Sym(rs)] = this._escCount;
      this._numMasked = this._prevSuccess = 0;
      this._foundState = 0;
    }
  }

  private void Update1(int ctx, int p) {
    this._foundState = p;
    this.Freq(p, this.Freq(p) + 4);
    this.SummFreq(ctx, this.SummFreq(ctx) + 4);
    if (this.Freq(p) > this.Freq(p - StateSize)) {
      this.Swap(p, p - StateSize);
      this._foundState = p -= StateSize;
      if (this.Freq(p) > MaxFreq)
        this.Rescale(ctx);
    }
  }

  private void DecodeSymbol1(int ctx) {
    var p = this.Stats(ctx);
    uint hiCnt = this.Freq(p);
    this._scale = this.SummFreq(ctx);
    uint count;
    if ((count = this.GetCurrentCount()) < hiCnt) {
      this._prevSuccess = (byte)(2 * (this._highCount = hiCnt) >= this._scale ? 1 : 0);
      this._foundState = p;
      this.Freq(p, (int)(hiCnt += 4));
      this.SummFreq(ctx, this.SummFreq(ctx) + 4);
      this._runLength += this._prevSuccess;
      if (hiCnt > MaxFreq)
        this.Rescale(ctx);
      this._lowCount = 0;
      return;
    }
    if (count >= this._scale)
      throw new InvalidDataException("FreeArc ppmd: code outside the context's range.");
    int n = this.NumStats(ctx);
    this._prevSuccess = 0;
    while ((hiCnt += this.Freq(p += StateSize)) <= count)
      if (--n == 0) {
        this._lowCount = hiCnt;
        this._charMask[this.Sym(p)] = this._escCount;
        n = this._numMasked = this.NumStats(ctx);
        this._foundState = 0;
        do { this._charMask[this.Sym(p -= StateSize)] = this._escCount; } while (--n != 0);
        this._highCount = this._scale;
        return;
      }
    this._lowCount = (this._highCount = hiCnt) - this.Freq(p);
    this.Update1(ctx, p);
  }

  private void Update2(int ctx, int p) {
    this._foundState = p;
    this.Freq(p, this.Freq(p) + 4);
    this.SummFreq(ctx, this.SummFreq(ctx) + 4);
    if (this.Freq(p) > MaxFreq)
      this.Rescale(ctx);
    ++this._escCount;
    this._runLength = this._initRL;
  }

  private int MakeEscFreq2(int ctx) {
    int see;
    int n = this.NumStats(ctx);
    if (n != 0xFF) {
      int t = this.NumStats(this.Suffix(ctx));
      see = (QTable[n + 2] - 3) * 32 + (this.SummFreq(ctx) > 11 * (n + 1) ? 1 : 0);
      see += 2 * (2 * n < t + this._numMasked ? 1 : 0) + this.Flags(ctx);
      this._scale = this.SeeGetMean(see);
    } else {
      see = DummySee;
      this._scale = 1;
    }
    return see;
  }

  private void DecodeSymbol2(int ctx) {
    var see = this.MakeEscFreq2(ctx);
    Span<int> ps = stackalloc int[256];
    var pps = 0;
    uint hiCnt = 0;
    int n = this.NumStats(ctx) - this._numMasked;
    var p = this.Stats(ctx) - StateSize;
    do {
      byte sym;
      do {
        sym = this.Sym(p += StateSize);
      } while (this._charMask[sym] == this._escCount);
      hiCnt += this.Freq(p);
      ps[pps++] = p;
    } while (--n != 0);
    this._scale += hiCnt;
    var count = this.GetCurrentCount();
    if (count >= this._scale)
      throw new InvalidDataException("FreeArc ppmd: code outside the context's range.");
    pps = 0;
    p = ps[0];
    if (count < hiCnt) {
      hiCnt = 0;
      while ((hiCnt += this.Freq(p)) <= count)
        p = ps[++pps];
      this._lowCount = (this._highCount = hiCnt) - this.Freq(p);
      if (see != DummySee)
        this.SeeUpdate(see);
      this.Update2(ctx, p);
    } else {
      this._lowCount = hiCnt;
      this._highCount = this._scale;
      n = this.NumStats(ctx) - this._numMasked;
      this._numMasked = this.NumStats(ctx);
      do { this._charMask[this.Sym(ps[pps++])] = this._escCount; } while (--n != 0);
      this._seeSumm[see] = (ushort)(this._seeSumm[see] + this._scale);
    }
  }

  private void ClearMask() {
    this._escCount = 1;
    Array.Clear(this._charMask);
    ++this._printCount;
  }
}
