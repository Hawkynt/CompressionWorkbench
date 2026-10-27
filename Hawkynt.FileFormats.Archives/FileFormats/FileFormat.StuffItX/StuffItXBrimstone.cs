using System.Buffers.Binary;

namespace FileFormat.StuffItX;

/// <summary>
/// Brimstone, StuffIt X's method 0: Dmitry Shkarin's PPMd variant G with three changes: the range
/// coder renormalises below 0x10000 instead of 0x8000, the order-0 context starts with frequency 2
/// for the 128 ASCII symbols (total 385), and the sub-allocator hands out 12-byte units with no
/// "text area" (index classes 1-4, 6-12 step 2, 15-24 step 3, 28-128 step 4).
/// </summary>
/// <remarks>
/// <para>
/// The stream opens with two bytes: the base-2 logarithm of the model memory and the maximum
/// order. The model lives in one byte heap addressed by 32-bit offsets (0 = none), laid out exactly
/// as the reference: a context is 12 bytes (last state index, flags, 16-bit total frequency, states
/// offset, suffix offset) and a state is 6 bytes (symbol, frequency, successor offset). A context
/// with a single state keeps that state in place of its total frequency and states offset.
/// </para>
/// <para>Converted from XADMaster's <c>PPMd/VariantG.c</c>, <c>Context.c</c> and
/// <c>SubAllocatorBrimstone.c</c> (LGPL-2.1-or-later, see THIRD-PARTY-NOTICE.XADMaster.txt).</para>
/// </remarks>
internal sealed class StuffItXBrimstone {
  private const int MaxO = 255;
  private const int IntBits = 7;
  private const int PeriodBits = 7;
  private const int TotBits = IntBits + PeriodBits;
  private const int MaxFreq = 124;
  private const int Interval = 1 << IntBits;
  private const int BinScale = 1 << TotBits;
  private const int UnitSize = 12;
  private const int IndexCount = 38;
  private const int HeapStart = UnitSize; // offset 0 is the null reference

  private static readonly byte[] ExpEscape = [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];
  private static readonly ushort[] InitBinEsc = [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x5FFB, 0x5545, 0x63D1, 0x5D9D, 0x64A1, 0x5ABC, 0x6632, 0x6051, 0x68F6, 0x549B, 0x6BCA, 0x3AB0];
  private static readonly byte[] Index2Units = BuildIndex2Units();
  private static readonly byte[] Units2Index = BuildUnits2Index();
  private static readonly byte[] Ns2BsIndx = BuildNs2BsIndx();
  private static readonly byte[] Ns2Indx = BuildNs2Indx();

  // ── sub-allocator ────────────────────────────────────────────────────────────
  private readonly byte[] _heap;
  private readonly int _heapSize;
  private readonly int[] _freeList = new int[IndexCount];
  private int _lowUnit;
  private int _highUnit;

  // ── model ────────────────────────────────────────────────────────────────────
  private readonly StuffItXRangeDecoder _coder;
  private readonly int _maxOrder;
  private readonly byte[] _charMask = new byte[256];
  private readonly ushort[] _binSumm = new ushort[128 * 16];
  private readonly ushort[] _seeSumm = new ushort[43 * 8 + 1];
  private readonly byte[] _seeShift = new byte[43 * 8 + 1];
  private readonly byte[] _seeCount = new byte[43 * 8 + 1];
  private const int DummySee = 43 * 8;
  private readonly int[] _ps = new int[256];
  private readonly int[] _stateList = new int[MaxO];
  private uint _scale;
  private int _foundState;
  private int _orderFall;
  private int _initEsc;
  private int _runLength;
  private byte _lastMaskIndex;
  private byte _escCount;
  private byte _prevSuccess;
  private int _minContext;
  private int _medContext;
  private int _maxContext;

  private StuffItXBrimstone(StuffItXInput input, int heapSize, int maxOrder) {
    this._heapSize = heapSize;
    this._heap = new byte[HeapStart + heapSize];
    this._maxOrder = maxOrder;
    this._coder = new StuffItXRangeDecoder(input, true, 0x10000);
    this._escCount = 1;
    this._seeShift[DummySee] = PeriodBits;
    if (!this.RestartModel()) throw new InvalidDataException("Brimstone model memory is too small for its order.");
  }

  /// <summary>Decodes up to <paramref name="length"/> bytes of a Brimstone stream.</summary>
  public static byte[] Decode(StuffItXInput input, long length) {
    var exponent = input.ReadByte();
    if (exponent > 30) throw new InvalidDataException($"Brimstone model memory 2^{exponent} is out of range.");
    var order = input.ReadByte();
    if (order < 1) throw new InvalidDataException("Brimstone model order 0 is not valid.");
    var model = new StuffItXBrimstone(input, 1 << exponent, order);
    var output = new byte[length];
    var n = 0;
    while (n < length) {
      var b = model.NextByte();
      if (b < 0) break;
      output[n++] = (byte)b;
    }
    return n == length ? output : output.AsSpan(0, n).ToArray();
  }

  // ── heap field access ────────────────────────────────────────────────────────
  // context: +0 LastStateIndex, +1 Flags, +2 SummFreq (u16), +4 States, +8 Suffix; OneState at +2
  // state:   +0 Symbol, +1 Freq, +2 Successor
  private int Lsi(int c) => this._heap[c];
  private void SetLsi(int c, int v) => this._heap[c] = (byte)v;
  private int Flags(int c) => this._heap[c + 1];
  private void SetFlags(int c, int v) => this._heap[c + 1] = (byte)v;
  private int SummFreq(int c) => BinaryPrimitives.ReadUInt16LittleEndian(this._heap.AsSpan(c + 2));
  private void SetSummFreq(int c, int v) => BinaryPrimitives.WriteUInt16LittleEndian(this._heap.AsSpan(c + 2), (ushort)v);
  private int States(int c) => (int)BinaryPrimitives.ReadUInt32LittleEndian(this._heap.AsSpan(c + 4));
  private void SetStates(int c, int v) => BinaryPrimitives.WriteUInt32LittleEndian(this._heap.AsSpan(c + 4), (uint)v);
  private int Suffix(int c) => (int)BinaryPrimitives.ReadUInt32LittleEndian(this._heap.AsSpan(c + 8));
  private void SetSuffix(int c, int v) => BinaryPrimitives.WriteUInt32LittleEndian(this._heap.AsSpan(c + 8), (uint)v);
  private static int OneState(int c) => c + 2;
  private int Symbol(int s) => this._heap[s];
  private void SetSymbol(int s, int v) => this._heap[s] = (byte)v;
  private int Freq(int s) => this._heap[s + 1];
  private void SetFreq(int s, int v) => this._heap[s + 1] = (byte)v;
  private int Successor(int s) => (int)BinaryPrimitives.ReadUInt32LittleEndian(this._heap.AsSpan(s + 2));
  private void SetSuccessor(int s, int v) => BinaryPrimitives.WriteUInt32LittleEndian(this._heap.AsSpan(s + 2), (uint)v);
  private int NumberOfStates(int c) => this.Flags(c) != 0 ? 0 : this.Lsi(c) + 1;
  private void CopyState(int from, int to) => this._heap.AsSpan(from, 6).CopyTo(this._heap.AsSpan(to, 6));

  private void SwapStates(int a, int b) {
    Span<byte> tmp = stackalloc byte[6];
    this._heap.AsSpan(a, 6).CopyTo(tmp);
    this._heap.AsSpan(b, 6).CopyTo(this._heap.AsSpan(a, 6));
    tmp.CopyTo(this._heap.AsSpan(b, 6));
  }

  private static int NotNull(int offset) => offset != 0 ? offset : throw new InvalidDataException("Brimstone stream is corrupt.");

  // ── sub-allocator (Brimstone) ────────────────────────────────────────────────
  private static byte[] BuildIndex2Units() {
    var t = new byte[IndexCount];
    for (var i = 0; i < 4; ++i) t[i] = (byte)(1 + i);
    for (var i = 0; i < 4; ++i) t[4 + i] = (byte)(6 + i * 2);
    for (var i = 0; i < 4; ++i) t[8 + i] = (byte)(15 + i * 3);
    for (var i = 0; i < 26; ++i) t[12 + i] = (byte)(28 + i * 4);
    return t;
  }

  private static byte[] BuildUnits2Index() {
    var t = new byte[128];
    var index2Units = BuildIndex2Units();
    var i = 0;
    for (var k = 0; k < 128; ++k) {
      if (index2Units[i] < k + 1) ++i;
      t[k] = (byte)i;
    }
    return t;
  }

  private static int I2B(int index) => UnitSize * Index2Units[index];

  private void InitAllocator() {
    Array.Clear(this._freeList);
    this._lowUnit = HeapStart;
    this._highUnit = HeapStart + UnitSize * (this._heapSize / UnitSize);
  }

  private void InsertNode(int p, int index) {
    BinaryPrimitives.WriteInt32LittleEndian(this._heap.AsSpan(p), this._freeList[index]);
    this._freeList[index] = p;
  }

  private int RemoveNode(int index) {
    var node = this._freeList[index];
    this._freeList[index] = BinaryPrimitives.ReadInt32LittleEndian(this._heap.AsSpan(node));
    return node;
  }

  private void SplitBlock(int p, int oldIndex, int newIndex) {
    p += I2B(newIndex);
    var diff = Index2Units[oldIndex] - Index2Units[newIndex];
    var i = Units2Index[diff - 1];
    if (Index2Units[i] != diff) {
      this.InsertNode(p, i - 1);
      p += I2B(i - 1);
      diff -= Index2Units[i - 1];
    }
    this.InsertNode(p, Units2Index[diff - 1]);
  }

  private int AllocContext() {
    if (this._highUnit > this._lowUnit) {
      this._highUnit -= UnitSize;
      return this._highUnit;
    }
    return this.AllocUnits(1);
  }

  private int AllocUnits(int num) {
    int index = Units2Index[num - 1];
    if (this._freeList[index] != 0) return this.RemoveNode(index);
    var units = this._lowUnit;
    this._lowUnit += I2B(index);
    if (this._lowUnit <= this._highUnit) return units;
    this._lowUnit -= I2B(index);
    for (var i = index + 1; i < IndexCount; ++i) {
      if (this._freeList[i] == 0) continue;
      var block = this.RemoveNode(i);
      this.SplitBlock(block, i, index);
      return block;
    }
    return 0;
  }

  private int ExpandUnits(int oldOffset, int oldNum) {
    int oldIndex = Units2Index[oldNum - 1], newIndex = Units2Index[oldNum];
    if (oldIndex == newIndex) return oldOffset;
    var offset = this.AllocUnits(oldNum + 1);
    if (offset != 0) {
      Array.Copy(this._heap, oldOffset, this._heap, offset, I2B(oldIndex));
      this.InsertNode(oldOffset, oldIndex);
    }
    return offset;
  }

  private int ShrinkUnits(int oldOffset, int oldNum, int newNum) {
    int oldIndex = Units2Index[oldNum - 1], newIndex = Units2Index[newNum - 1];
    if (oldIndex == newIndex) return oldOffset;
    if (this._freeList[newIndex] != 0) {
      var p = this.RemoveNode(newIndex);
      Array.Copy(this._heap, oldOffset, this._heap, p, I2B(newIndex));
      this.InsertNode(oldOffset, oldIndex);
      return p;
    }
    this.SplitBlock(oldOffset, oldIndex, newIndex);
    return oldOffset;
  }

  private void FreeUnits(int offset, int num) => this.InsertNode(offset, Units2Index[num - 1]);

  // ── contexts ─────────────────────────────────────────────────────────────────
  private int NewContext() {
    var c = this.AllocContext();
    if (c != 0) {
      this.SetLsi(c, 0);
      this.SetFlags(c, 0);
      this.SetSuffix(c, 0);
    }
    return c;
  }

  private int NewContextAsChildOf(int suffixContext, int suffixState, int firstState) {
    var c = this.AllocContext();
    if (c != 0) {
      this.SetLsi(c, 0);
      this.SetFlags(c, 0);
      this.SetSuffix(c, suffixContext);
      this.SetSuccessor(suffixState, c);
      if (firstState != 0) this.CopyState(firstState, OneState(c));
    }
    return c;
  }

  private static byte[] BuildNs2BsIndx() {
    var t = new byte[256];
    for (var i = 0; i < 6; ++i) t[i] = (byte)(2 * i);
    for (var i = 6; i < 50; ++i) t[i] = 12;
    for (var i = 50; i < 256; ++i) t[i] = 14;
    return t;
  }

  private static byte[] BuildNs2Indx() {
    var t = new byte[256];
    for (var i = 0; i < 4; ++i) t[i] = (byte)i;
    for (var i = 4; i < 4 + 8; ++i) t[i] = (byte)(4 + ((i - 4) >> 1));
    for (var i = 4 + 8; i < 4 + 8 + 32; ++i) t[i] = (byte)(4 + 4 + ((i - 4 - 8) >> 2));
    for (var i = 4 + 8 + 32; i < 256; ++i) t[i] = (byte)(4 + 4 + 8 + ((i - 4 - 8 - 32) >> 3));
    return t;
  }

  private bool RestartModel() {
    this.InitAllocator();
    Array.Clear(this._charMask);
    this._prevSuccess = 0;
    this._orderFall = 1;

    this._maxContext = this.NewContext();
    if (this._maxContext == 0) return false;
    this.SetLsi(this._maxContext, 255);
    this.SetSummFreq(this._maxContext, 385);
    var states = this.AllocUnits(256 / 2);
    this.SetStates(this._maxContext, states);
    if (states == 0) return false;
    for (var i = 0; i < 256; ++i) {
      var s = states + 6 * i;
      this.SetSymbol(s, i);
      this.SetFreq(s, i < 0x80 ? 2 : 1);
      this.SetSuccessor(s, 0);
    }

    var state = states;
    for (var i = 1; ; ++i) {
      this._maxContext = this.NewContextAsChildOf(this._maxContext, state, 0);
      if (this._maxContext == 0) return false;
      if (i == this._maxOrder) break;
      state = OneState(this._maxContext);
      this.SetSymbol(state, 0);
      this.SetFreq(state, 1);
    }
    this.SetFlags(this._maxContext, 1);
    this._medContext = this._minContext = this.Suffix(this._maxContext);

    for (var i = 0; i < 128; ++i)
      for (var k = 0; k < 16; ++k)
        this._binSumm[i * 16 + k] = (ushort)(BinScale - InitBinEsc[k] / (i + 2));

    for (var i = 0; i < 43; ++i)
      for (var k = 0; k < 8; ++k) {
        var see = i * 8 + k;
        this._seeShift[see] = PeriodBits - 4;
        this._seeSumm[see] = (ushort)((4 * i + 10) << (PeriodBits - 4));
        this._seeCount[see] = 3;
      }
    return true;
  }

  // ── SEE ──────────────────────────────────────────────────────────────────────
  private uint GetSeeMeanMasked(int see) {
    var r = (uint)(this._seeSumm[see] >> this._seeShift[see]);
    this._seeSumm[see] = (ushort)(this._seeSumm[see] - r);
    r &= 0x03FF;
    return r == 0 ? 1u : r;
  }

  private void UpdateSee(int see) {
    if (this._seeShift[see] >= PeriodBits) return;
    if (--this._seeCount[see] != 0) return;
    this._seeSumm[see] = (ushort)(this._seeSumm[see] * 2);
    this._seeCount[see] = (byte)(3 << this._seeShift[see]);
    ++this._seeShift[see];
  }

  // ── decoding ─────────────────────────────────────────────────────────────────
  private int NextByte() {
    if (this._minContext == 0) return -1;

    if (this.NumberOfStates(this._minContext) != 1) this.DecodeSymbol1(this._minContext);
    else this.DecodeBinSymbol(this._minContext);

    while (this._foundState == 0) {
      do {
        ++this._orderFall;
        this._minContext = this.Suffix(this._minContext);
        if (this._minContext == 0) return -1;
      } while (this.Lsi(this._minContext) == this._lastMaskIndex);
      this.DecodeSymbol2(this._minContext);
    }

    var symbol = this.Symbol(this._foundState);
    var successor = this.Successor(this._foundState);
    if (this._orderFall == 0 && this.Flags(NotNull(successor)) == 0) {
      this._minContext = this._medContext = successor;
    } else {
      this.UpdateModel();
      if (this._escCount == 0) this.ClearMask();
    }
    return symbol;
  }

  private void ClearMask() {
    this._escCount = 1;
    Array.Clear(this._charMask);
  }

  private void DecodeBinSymbol(int context) {
    var rs = OneState(context);
    var bs = (this.Freq(rs) - 1) * 16 + this._prevSuccess + Ns2BsIndx[this.Lsi(NotNull(this.Suffix(context)))];
    var bit = this._coder.NextWeightedBit(this._binSumm[bs], BinScale);
    if (bit == 0) {
      this._prevSuccess = 1;
      ++this._runLength;
      this._foundState = rs;
      if (this.Freq(rs) < 128) this.SetFreq(rs, this.Freq(rs) + 1);
      this._binSumm[bs] = (ushort)(this._binSumm[bs] + Interval - ((this._binSumm[bs] + 32) >> PeriodBits));
    } else {
      this._prevSuccess = 0;
      this._foundState = 0;
      this._lastMaskIndex = 0;
      this._charMask[this.Symbol(rs)] = this._escCount;
      this._binSumm[bs] = (ushort)(this._binSumm[bs] - ((this._binSumm[bs] + 32) >> PeriodBits));
      this._initEsc = ExpEscape[this._binSumm[bs] >> 10];
    }
  }

  private void DecodeSymbol1(int context) {
    this._scale = (uint)this.SummFreq(context);
    var states = this.States(context);
    var firstCount = this.Freq(states);
    var count = this._coder.CurrentCount(this._scale);

    if (count < firstCount) {
      this._coder.RemoveSubRange(0, (uint)firstCount);
      if (2 * firstCount > this._scale) {
        this._prevSuccess = 1;
        ++this._runLength;
      } else this._prevSuccess = 0;
      this._foundState = states;
      this.SetFreq(states, firstCount + 4);
      this.SetSummFreq(context, this.SummFreq(context) + 4);
      if (firstCount + 4 > MaxFreq) this.Rescale(context);
      return;
    }

    var highCount = (uint)firstCount;
    this._prevSuccess = 0;
    var last = this.Lsi(context);
    for (var i = 1; i <= last; ++i) {
      var s = states + 6 * i;
      highCount += (uint)this.Freq(s);
      if (highCount > count) {
        this._coder.RemoveSubRange(highCount - (uint)this.Freq(s), highCount);
        this.UpdateContext1(context, s);
        return;
      }
    }

    // The reference bails out here, without consuming the escape, when no symbol was found yet.
    if (this._foundState == 0) return;

    this._coder.RemoveSubRange(highCount, this._scale);
    this._lastMaskIndex = (byte)last;
    this._foundState = 0;
    for (var i = 0; i <= last; ++i) this._charMask[this.Symbol(states + 6 * i)] = this._escCount;
  }

  private void UpdateContext1(int context, int state) {
    this.SetFreq(state, this.Freq(state) + 4);
    this.SetSummFreq(context, this.SummFreq(context) + 4);
    if (this.Freq(state) > this.Freq(state - 6)) {
      this.SwapStates(state, state - 6);
      this._foundState = state - 6;
      if (this.Freq(state - 6) > MaxFreq) this.Rescale(context);
    } else {
      this._foundState = state;
    }
  }

  private void DecodeSymbol2(int context) {
    var lsi = this.Lsi(context);
    var diff = lsi - this._lastMaskIndex;
    int see;
    if (lsi != 255) {
      see = Ns2Indx[diff - 1] * 8
        + (diff < this.Lsi(NotNull(this.Suffix(context))) - lsi ? 1 : 0)
        + (this.SummFreq(context) < 11 * this.NumberOfStates(context) ? 2 : 0)
        + (this._lastMaskIndex + 1 > diff ? 4 : 0);
      this._scale = this.GetSeeMeanMasked(see);
    } else {
      this._scale = 1;
      see = DummySee;
    }

    var n = diff;
    var total = 0u;
    var state = this.States(context);
    for (var i = 0; i < n; ++i) {
      while (this._charMask[this.Symbol(state)] == this._escCount) state += 6;
      total += (uint)this.Freq(state);
      this._ps[i] = state;
      state += 6;
    }

    this._scale += total;
    var count = this._coder.CurrentCount(this._scale);
    if (count < total) {
      var k = 0;
      var highCount = (uint)this.Freq(this._ps[0]);
      while (highCount <= count && k + 1 < n) highCount += (uint)this.Freq(this._ps[++k]);
      this._coder.RemoveSubRange(highCount - (uint)this.Freq(this._ps[k]), highCount);
      this.UpdateSee(see);
      this.UpdateContext2(context, this._ps[k]);
    } else {
      this._coder.RemoveSubRange(total, this._scale);
      this._lastMaskIndex = (byte)lsi;
      this._seeSumm[see] = (ushort)(this._seeSumm[see] + this._scale);
      for (var i = 0; i < n; ++i) this._charMask[this.Symbol(this._ps[i])] = this._escCount;
    }
  }

  private void UpdateContext2(int context, int state) {
    this._foundState = state;
    this.SetFreq(state, this.Freq(state) + 4);
    this.SetSummFreq(context, this.SummFreq(context) + 4);
    if (this.Freq(state) > MaxFreq) this.Rescale(context);
    ++this._escCount;
    this._runLength = 0;
  }

  private void Rescale(int context) {
    var states = this.States(context);
    var n = this.Lsi(context) + 1;

    this.SetFreq(this._foundState, this.Freq(this._foundState) + 4);

    var escFreq = this.SummFreq(context) + 4;
    var adder = this._orderFall == 0 ? 0 : 1;
    var summ = 0;
    Span<byte> tmp = stackalloc byte[6];

    for (var i = 0; i < n; ++i) {
      var s = states + 6 * i;
      escFreq -= this.Freq(s);
      this.SetFreq(s, (this.Freq(s) + adder) >> 1);
      summ += this.Freq(s);

      if (i > 0 && this.Freq(s) > this.Freq(s - 6)) {
        this._heap.AsSpan(s, 6).CopyTo(tmp);
        var j = i - 1;
        while (j > 0 && tmp[1] > this.Freq(states + 6 * (j - 1))) --j;
        Array.Copy(this._heap, states + 6 * j, this._heap, states + 6 * (j + 1), 6 * (i - j));
        tmp.CopyTo(this._heap.AsSpan(states + 6 * j, 6));
      }
    }
    this.SetSummFreq(context, summ);

    if (this.Freq(states + 6 * (n - 1)) == 0) {
      var numZeros = 1;
      while (numZeros < n && this.Freq(states + 6 * (n - 1 - numZeros)) == 0) ++numZeros;
      escFreq += numZeros;

      this.SetLsi(context, this.Lsi(context) - numZeros);
      if (this.Lsi(context) == 0) {
        this._heap.AsSpan(states, 6).CopyTo(tmp);
        do {
          tmp[1] = (byte)((tmp[1] + 1) >> 1);
          escFreq >>= 1;
        } while (escFreq > 1);
        this.FreeUnits(states, (n + 1) >> 1);
        this._foundState = OneState(context);
        tmp.CopyTo(this._heap.AsSpan(this._foundState, 6));
        return;
      }

      int n0 = (n + 1) >> 1, n1 = (this.Lsi(context) + 2) >> 1;
      if (n0 != n1) this.SetStates(context, this.ShrinkUnits(states, n0, n1));
    }

    this.SetSummFreq(context, this.SummFreq(context) + ((escFreq + 1) >> 1));
    this._foundState = this.States(context);
  }

  private void UpdateModel() {
    int fsSymbol = this.Symbol(this._foundState), fsFreq = this.Freq(this._foundState), fsSuccessor = this.Successor(this._foundState);
    var state = 0;

    if (fsFreq < MaxFreq / 4 && this.Suffix(this._minContext) != 0) {
      var context = this.Suffix(this._minContext);
      if (this.NumberOfStates(context) != 1) {
        state = this.States(context);
        if (this.Symbol(state) != fsSymbol) {
          do state += 6;
          while (this.Symbol(state) != fsSymbol);
          if (this.Freq(state) >= this.Freq(state - 6)) {
            this.SwapStates(state, state - 6);
            state -= 6;
          }
        }
        if (this.Freq(state) < 7 * MaxFreq / 8) {
          this.SetFreq(state, this.Freq(state) + 2);
          this.SetSummFreq(context, this.SummFreq(context) + 2);
        }
      } else {
        state = OneState(context);
        if (this.Freq(state) < 32) this.SetFreq(state, this.Freq(state) + 1);
      }
    }

    int successor;
    var skipCount = 0;
    if (this._orderFall == 0) {
      if (!this.MakeRoot(2, 0)) {
        this.Restart();
        return;
      }
      this._minContext = this._medContext = fsSuccessor;
      return;
    }
    if (--this._orderFall == 0) {
      successor = fsSuccessor;
      skipCount = 1;
    } else {
      successor = this.NewContext();
      if (successor == 0) {
        this.Restart();
        return;
      }
      this.SetFlags(successor, 1);
    }

    if (this.Flags(this._maxContext) == 1) {
      this.SetSymbol(OneState(this._maxContext), fsSymbol);
      this.SetSuccessor(OneState(this._maxContext), successor);
    }

    var minNum = this.NumberOfStates(this._minContext);
    var s0 = this.SummFreq(this._minContext) - minNum - (fsFreq - 1);

    for (var current = this._medContext; current != this._minContext; current = NotNull(this.Suffix(current))) {
      var currentNum = this.NumberOfStates(current);
      if (currentNum != 1) {
        if (currentNum == 0) throw new InvalidDataException("Brimstone stream is corrupt.");
        if ((currentNum & 1) == 0) {
          var expanded = this.ExpandUnits(this.States(current), currentNum >> 1);
          this.SetStates(current, expanded);
          if (expanded == 0) {
            this.Restart();
            return;
          }
        }
        if (4 * currentNum <= minNum && this.SummFreq(current) <= 8 * currentNum) this.SetSummFreq(current, this.SummFreq(current) + 2);
        if (2 * currentNum < minNum) this.SetSummFreq(current, this.SummFreq(current) + 1);
      } else {
        var states = this.AllocUnits(1);
        if (states == 0) {
          this.Restart();
          return;
        }
        this.CopyState(OneState(current), states);
        this.SetStates(current, states);
        if (this.Freq(states) < MaxFreq / 4 - 1) this.SetFreq(states, this.Freq(states) * 2);
        else this.SetFreq(states, MaxFreq - 4);
        this.SetSummFreq(current, this.Freq(states) + this._initEsc + (minNum > 3 ? 1 : 0));
      }

      var cf = (uint)(2 * fsFreq * (this.SummFreq(current) + 6));
      var sf = (uint)(s0 + this.SummFreq(current));
      int freq;
      if (cf < 6 * sf) {
        freq = cf >= 4 * sf ? 3 : cf > sf ? 2 : 1;
        this.SetSummFreq(current, this.SummFreq(current) + 3);
      } else {
        freq = cf >= 15 * sf ? 7 : cf >= 12 * sf ? 6 : cf >= 9 * sf ? 5 : 4;
        this.SetSummFreq(current, this.SummFreq(current) + freq);
      }

      var added = this.States(current) + 6 * currentNum;
      this.SetSuccessor(added, successor);
      this.SetSymbol(added, fsSymbol);
      this.SetFreq(added, freq);
      this.SetLsi(current, currentNum);
    }

    if (fsSuccessor != 0) {
      if (this.Flags(fsSuccessor) == 1 && !this.MakeRoot(skipCount, state)) {
        this.Restart();
        return;
      }
      this._minContext = this.Successor(this._foundState);
    } else {
      this.SetSuccessor(this._foundState, successor);
      ++this._orderFall;
    }

    this._medContext = this._minContext;
    this._maxContext = NotNull(successor);
  }

  private void Restart() {
    this.RestartModel();
    this._escCount = 0;
  }

  private bool MakeRoot(int skipCount, int state) {
    var context = this._minContext;
    var upBranch = this.Successor(this._foundState);
    var fsSymbol = this.Symbol(this._foundState);
    var n = 0;

    var skip = false;
    if (skipCount == 0) {
      this._stateList[n++] = this._foundState;
      if (this.Suffix(context) == 0) skip = true;
    } else if (skipCount == 2) context = this.Suffix(context);

    if (!skip && state != 0) {
      context = this.Suffix(context);
      if (this.Successor(state) != upBranch) {
        context = this.Successor(state);
        skip = true;
      } else {
        this._stateList[n++] = state;
        if (this.Suffix(context) == 0) skip = true;
      }
    }

    if (!skip) {
      do {
        context = NotNull(this.Suffix(context));
        if (this.NumberOfStates(context) != 1) {
          state = this.States(context);
          while (this.Symbol(state) != fsSymbol) state += 6;
        } else state = OneState(context);

        if (this.Successor(state) != upBranch) {
          context = this.Successor(state);
          break;
        }
        if (n >= MaxO) throw new InvalidDataException("Brimstone stream is corrupt.");
        this._stateList[n++] = state;
      } while (this.Suffix(context) != 0);
    }

    var upState = OneState(NotNull(upBranch));
    if (this.NumberOfStates(context) != 1) {
      state = this.States(context);
      while (this.Symbol(state) != this.Symbol(upState)) state += 6;
      var cf = this.Freq(state) - 1;
      var s0 = this.SummFreq(context) - this.Lsi(context) - 1 - cf;
      if (2 * cf <= s0) this.SetFreq(upState, 5 * cf > s0 ? 2 : 1);
      else if (s0 == 0) throw new InvalidDataException("Brimstone stream is corrupt.");
      else this.SetFreq(upState, 1 + (2 * cf + 3 * s0 - 1) / (2 * s0));
    } else this.SetFreq(upState, this.Freq(OneState(context)));

    for (var i = n - 1; i >= 0; --i) {
      context = this.NewContextAsChildOf(context, this._stateList[i], upState);
      if (context == 0) return false;
    }

    if (this._orderFall == 0) {
      this.SetLsi(upBranch, 0);
      this.SetFlags(upBranch, 0);
      this.SetSuffix(upBranch, context);
    }
    return true;
  }
}
