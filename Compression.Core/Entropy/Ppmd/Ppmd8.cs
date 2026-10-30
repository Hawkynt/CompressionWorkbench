using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Compression.Core.Entropy.Ppmd;

/// <summary>How a <see cref="Ppmd8"/> model recovers when its memory is exhausted.</summary>
public enum Ppmd8RestoreMethod {
  /// <summary>Discard the model and start over.</summary>
  Restart = 0,

  /// <summary>Cut the context tree back to the most recently used branches.</summary>
  CutOff = 1,
}

/// <summary>
/// PPMd variant I revision 1 (Dmitry Shkarin's PPMd var.I, 2002) with Dmitry Subbotin's
/// carry-less range coder: the model ZIP compression method 98 uses.
/// </summary>
/// <remarks>
/// Converted from Igor Pavlov's public-domain Ppmd8.c, Ppmd8Dec.c and Ppmd8Enc.c (7-Zip 9.20,
/// 2010), see THIRD-PARTY-NOTICE.Ppmd8.txt. The C code addresses one memory block through
/// 32-bit offsets; here that block is a byte array with the same layout (12-byte units,
/// 6-byte states), so the model allocates, frees and restores exactly as the original does.
/// One instance either encodes or decodes one stream.
/// </remarks>
public sealed partial class Ppmd8 {
  /// <summary>Smallest model order the format allows.</summary>
  public const int MinOrder = 2;

  /// <summary>Largest model order the format allows.</summary>
  public const int MaxOrderLimit = 16;

  private const int MaxFreq = 124;
  private const int UnitSize = 12;
  private const int StateSize = 6;
  private const uint EmptyNode = 0xFFFFFFFF;
  private const int IntBits = 7;
  private const int PeriodBits = 7;
  private const int BinScale = 1 << (Ppmd8.IntBits + Ppmd8.PeriodBits);
  private const int NumIndexes = 4 + 4 + 4 + (128 + 3 - 1 * 4 - 2 * 4 - 3 * 4) / 4;
  private const int SeeCount = 24 * 32;
  private const int DummySee = Ppmd8.SeeCount;

  private static readonly byte[] ExpEscape = [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];
  private static readonly ushort[] InitBinEsc = [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x64A1, 0x5ABC, 0x6632, 0x6051];

  private readonly byte[] _heap;
  private readonly uint _size;
  private readonly uint _alignOffset;
  private readonly uint _maxOrder;
  private readonly Ppmd8RestoreMethod _restoreMethod;

  private uint _minContext, _maxContext, _foundState;
  private uint _orderFall, _initEsc, _prevSuccess;
  private int _runLength, _initRunLength;
  private uint _glueCount;
  private uint _loUnit, _hiUnit, _text, _unitsStart;

  private readonly byte[] _indexToUnits = new byte[Ppmd8.NumIndexes];
  private readonly byte[] _unitsToIndex = new byte[128];
  private readonly uint[] _freeList = new uint[Ppmd8.NumIndexes];
  private readonly uint[] _stamps = new uint[Ppmd8.NumIndexes];
  private readonly byte[] _ns2BsIndex = new byte[256];
  private readonly byte[] _ns2Index = new byte[260];
  private readonly ushort[] _seeSumm = new ushort[Ppmd8.SeeCount + 1];
  private readonly byte[] _seeShift = new byte[Ppmd8.SeeCount + 1];
  private readonly byte[] _seeCount = new byte[Ppmd8.SeeCount + 1];
  private readonly ushort[] _binSumm = new ushort[25 * 64];

  /// <summary>Creates a model with <paramref name="memorySize"/> bytes of context memory.</summary>
  /// <param name="memorySize">Model memory in bytes (ZIP stores it in whole megabytes).</param>
  /// <param name="maxOrder">Model order, <see cref="MinOrder"/>..<see cref="MaxOrderLimit"/>.</param>
  /// <param name="restoreMethod">What the model does when its memory runs out.</param>
  public Ppmd8(int memorySize, int maxOrder, Ppmd8RestoreMethod restoreMethod) {
    ArgumentOutOfRangeException.ThrowIfLessThan(memorySize, 1 << 11);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxOrder, Ppmd8.MinOrder);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxOrder, Ppmd8.MaxOrderLimit);
    if (restoreMethod is not (Ppmd8RestoreMethod.Restart or Ppmd8RestoreMethod.CutOff))
      throw new ArgumentOutOfRangeException(nameof(restoreMethod));

    this._size = (uint)memorySize;
    this._alignOffset = 4 - (this._size & 3);
    this._heap = new byte[this._alignOffset + this._size];
    this._maxOrder = (uint)maxOrder;
    this._restoreMethod = restoreMethod;

    for (int i = 0, k = 0; i < Ppmd8.NumIndexes; ++i) {
      var step = i >= 12 ? 4 : (i >> 2) + 1;
      do {
        this._unitsToIndex[k++] = (byte)i;
      } while (--step != 0);

      this._indexToUnits[i] = (byte)k;
    }

    this._ns2BsIndex[0] = 0 << 1;
    this._ns2BsIndex[1] = 1 << 1;
    this._ns2BsIndex.AsSpan(2, 9).Fill(2 << 1);
    this._ns2BsIndex.AsSpan(11).Fill(3 << 1);

    int n;
    for (n = 0; n < 5; ++n)
      this._ns2Index[n] = (byte)n;

    for (int m = n, k = 1; n < 260; ++n) {
      this._ns2Index[n] = (byte)m;
      if (--k == 0)
        k = ++m - 4;
    }

    this.RestartModel();
    this._seeShift[Ppmd8.DummySee] = Ppmd8.PeriodBits;
    this._seeSumm[Ppmd8.DummySee] = 0;
    this._seeCount[Ppmd8.DummySee] = 64;
  }

  // ---------- heap layout ----------
  // context (12 bytes): NumStats u8 @0, Flags u8 @1, SummFreq u16 @2, Stats u32 @4, Suffix u32 @8;
  //   a context with one state keeps it in place at @2 (overlaying SummFreq and Stats).
  // state (6 bytes): Symbol u8 @0, Freq u8 @1, Successor u32 @2 (two little-endian halves).
  // free node (12 bytes): Stamp u32 @0, Next u32 @4, NU u32 @8.

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint U16(uint at) => BinaryPrimitives.ReadUInt16LittleEndian(this._heap.AsSpan((int)at, 2));

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void U16(uint at, uint value) => BinaryPrimitives.WriteUInt16LittleEndian(this._heap.AsSpan((int)at, 2), (ushort)value);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint U32(uint at) => BinaryPrimitives.ReadUInt32LittleEndian(this._heap.AsSpan((int)at, 4));

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void U32(uint at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(this._heap.AsSpan((int)at, 4), value);

  private uint NumStats(uint ctx) => this._heap[ctx];
  private void NumStats(uint ctx, uint value) => this._heap[ctx] = (byte)value;
  private uint Flags(uint ctx) => this._heap[ctx + 1];
  private void Flags(uint ctx, uint value) => this._heap[ctx + 1] = (byte)value;
  private uint SummFreq(uint ctx) => this.U16(ctx + 2);
  private void SummFreq(uint ctx, uint value) => this.U16(ctx + 2, value);
  private uint Stats(uint ctx) => this.U32(ctx + 4);
  private void Stats(uint ctx, uint value) => this.U32(ctx + 4, value);
  private uint Suffix(uint ctx) => this.U32(ctx + 8);
  private void Suffix(uint ctx, uint value) => this.U32(ctx + 8, value);
  private static uint OneState(uint ctx) => ctx + 2;

  private uint Symbol(uint s) => this._heap[s];
  private void Symbol(uint s, uint value) => this._heap[s] = (byte)value;
  private uint Freq(uint s) => this._heap[s + 1];
  private void Freq(uint s, uint value) => this._heap[s + 1] = (byte)value;
  private uint Successor(uint s) => this.U16(s + 2) | this.U16(s + 4) << 16;

  private void Successor(uint s, uint value) {
    this.U16(s + 2, value & 0xFFFF);
    this.U16(s + 4, value >> 16);
  }

  private void CopyState(uint to, uint from) => this._heap.AsSpan((int)from, Ppmd8.StateSize).CopyTo(this._heap.AsSpan((int)to, Ppmd8.StateSize));

  private void SwapStates(uint a, uint b) {
    Span<byte> tmp = stackalloc byte[Ppmd8.StateSize];
    this._heap.AsSpan((int)a, Ppmd8.StateSize).CopyTo(tmp);
    this.CopyState(a, b);
    tmp.CopyTo(this._heap.AsSpan((int)b, Ppmd8.StateSize));
  }

  private uint NodeStamp(uint node) => this.U32(node);
  private void NodeStamp(uint node, uint value) => this.U32(node, value);
  private uint NodeNext(uint node) => this.U32(node + 4);
  private void NodeNext(uint node, uint value) => this.U32(node + 4, value);
  private uint NodeUnits(uint node) => this.U32(node + 8);
  private void NodeUnits(uint node, uint value) => this.U32(node + 8, value);

  private static uint UnitsToBytes(uint units) => units * Ppmd8.UnitSize;
  private uint UnitsToIndex(uint units) => this._unitsToIndex[units - 1];
  private uint IndexToUnits(uint index) => this._indexToUnits[index];

  // ---------- sub-allocator ----------

  private void InsertNode(uint node, uint index) {
    this.NodeStamp(node, Ppmd8.EmptyNode);
    this.NodeNext(node, this._freeList[index]);
    this.NodeUnits(node, this.IndexToUnits(index));
    this._freeList[index] = node;
    ++this._stamps[index];
  }

  private uint RemoveNode(uint index) {
    var node = this._freeList[index];
    this._freeList[index] = this.NodeNext(node);
    --this._stamps[index];
    return node;
  }

  private void SplitBlock(uint ptr, uint oldIndex, uint newIndex) {
    var nu = this.IndexToUnits(oldIndex) - this.IndexToUnits(newIndex);
    ptr += UnitsToBytes(this.IndexToUnits(newIndex));
    var i = this.UnitsToIndex(nu);
    if (this.IndexToUnits(i) != nu) {
      var k = this.IndexToUnits(--i);
      this.InsertNode(ptr + UnitsToBytes(k), nu - k - 1);
    }

    this.InsertNode(ptr, i);
  }

  private void GlueFreeBlocks() {
    var head = 0u;
    var prevIsHead = true;
    var prevNode = 0u;

    this._glueCount = 1 << 13;
    Array.Clear(this._stamps);

    // The order-0 context always holds the top unit, so only LoUnit needs a guard node.
    if (this._loUnit != this._hiUnit)
      this.NodeStamp(this._loUnit, 0);

    for (var i = 0; i < Ppmd8.NumIndexes; ++i) {
      var next = this._freeList[i];
      this._freeList[i] = 0;
      while (next != 0) {
        var node = next;
        if (this.NodeUnits(node) != 0) {
          if (prevIsHead)
            head = next;
          else
            this.NodeNext(prevNode, next);

          prevIsHead = false;
          prevNode = node;
          uint node2;
          while (this.NodeStamp(node2 = node + UnitsToBytes(this.NodeUnits(node))) == Ppmd8.EmptyNode) {
            this.NodeUnits(node, this.NodeUnits(node) + this.NodeUnits(node2));
            this.NodeUnits(node2, 0);
          }
        }

        next = this.NodeNext(node);
      }
    }

    if (prevIsHead)
      head = 0;
    else
      this.NodeNext(prevNode, 0);

    while (head != 0) {
      var node = head;
      head = this.NodeNext(node);
      var nu = this.NodeUnits(node);
      if (nu == 0)
        continue;

      for (; nu > 128; nu -= 128, node += UnitsToBytes(128))
        this.InsertNode(node, Ppmd8.NumIndexes - 1);

      var i = this.UnitsToIndex(nu);
      if (this.IndexToUnits(i) != nu) {
        var k = this.IndexToUnits(--i);
        this.InsertNode(node + UnitsToBytes(k), nu - k - 1);
      }

      this.InsertNode(node, i);
    }
  }

  private uint AllocUnitsRare(uint index) {
    if (this._glueCount == 0) {
      this.GlueFreeBlocks();
      if (this._freeList[index] != 0)
        return this.RemoveNode(index);
    }

    var i = index;
    do {
      if (++i == Ppmd8.NumIndexes) {
        var numBytes = UnitsToBytes(this.IndexToUnits(index));
        --this._glueCount;
        return this._unitsStart - this._text > numBytes ? this._unitsStart -= numBytes : 0;
      }
    } while (this._freeList[i] == 0);

    var block = this.RemoveNode(i);
    this.SplitBlock(block, i, index);
    return block;
  }

  private uint AllocUnits(uint index) {
    if (this._freeList[index] != 0)
      return this.RemoveNode(index);

    var numBytes = UnitsToBytes(this.IndexToUnits(index));
    if (numBytes <= this._hiUnit - this._loUnit) {
      var block = this._loUnit;
      this._loUnit += numBytes;
      return block;
    }

    return this.AllocUnitsRare(index);
  }

  private void CopyUnits(uint to, uint from, uint units)
    => this._heap.AsSpan((int)from, (int)UnitsToBytes(units)).CopyTo(this._heap.AsSpan((int)to));

  private uint ShrinkUnits(uint oldPtr, uint oldUnits, uint newUnits) {
    var i0 = this.UnitsToIndex(oldUnits);
    var i1 = this.UnitsToIndex(newUnits);
    if (i0 == i1)
      return oldPtr;

    if (this._freeList[i1] != 0) {
      var ptr = this.RemoveNode(i1);
      this.CopyUnits(ptr, oldPtr, newUnits);
      this.InsertNode(oldPtr, i0);
      return ptr;
    }

    this.SplitBlock(oldPtr, i0, i1);
    return oldPtr;
  }

  private void FreeUnits(uint ptr, uint units) => this.InsertNode(ptr, this.UnitsToIndex(units));

  private void SpecialFreeUnit(uint ptr) {
    if (ptr != this._unitsStart)
      this.InsertNode(ptr, 0);
    else
      this._unitsStart += Ppmd8.UnitSize;
  }

  private uint MoveUnitsUp(uint oldPtr, uint units) {
    var index = this.UnitsToIndex(units);
    if (oldPtr > this._unitsStart + 16 * 1024 || oldPtr > this._freeList[index])
      return oldPtr;

    var ptr = this.RemoveNode(index);
    this.CopyUnits(ptr, oldPtr, units);
    if (oldPtr != this._unitsStart)
      this.InsertNode(oldPtr, index);
    else
      this._unitsStart += UnitsToBytes(this.IndexToUnits(index));

    return ptr;
  }

  private void ExpandTextArea() {
    Span<uint> count = stackalloc uint[Ppmd8.NumIndexes];
    count.Clear();
    if (this._loUnit != this._hiUnit)
      this.NodeStamp(this._loUnit, 0);

    var node = this._unitsStart;
    for (; this.NodeStamp(node) == Ppmd8.EmptyNode; node += UnitsToBytes(this.NodeUnits(node))) {
      this.NodeStamp(node, 0);
      ++count[(int)this.UnitsToIndex(this.NodeUnits(node))];
    }

    this._unitsStart = node;

    for (var i = 0; i < Ppmd8.NumIndexes; ++i) {
      // "next" is the address of a link: the free-list head itself, or a node's Next field.
      var linkIsHead = true;
      var linkNode = 0u;
      while (count[i] != 0) {
        var current = linkIsHead ? this._freeList[i] : this.NodeNext(linkNode);
        while (this.NodeStamp(current) == 0) {
          var after = this.NodeNext(current);
          if (linkIsHead)
            this._freeList[i] = after;
          else
            this.NodeNext(linkNode, after);

          current = after;
          --this._stamps[i];
          if (--count[i] == 0)
            break;
        }

        linkIsHead = false;
        linkNode = current;
      }
    }
  }

  // ---------- model ----------

  private void RestartModel() {
    Array.Clear(this._freeList);
    Array.Clear(this._stamps);
    this._text = this._alignOffset;
    this._hiUnit = this._text + this._size;
    this._loUnit = this._unitsStart = this._hiUnit - this._size / 8 / Ppmd8.UnitSize * 7 * Ppmd8.UnitSize;
    this._glueCount = 0;

    this._orderFall = this._maxOrder;
    this._runLength = this._initRunLength = -(int)(this._maxOrder < 12 ? this._maxOrder : 12) - 1;
    this._prevSuccess = 0;

    this._minContext = this._maxContext = this._hiUnit -= Ppmd8.UnitSize;
    this.Suffix(this._minContext, 0);
    this.NumStats(this._minContext, 255);
    this.Flags(this._minContext, 0);
    this.SummFreq(this._minContext, 256 + 1);
    this._foundState = this._loUnit;
    this._loUnit += UnitsToBytes(256 / 2);
    this.Stats(this._minContext, this._foundState);
    for (var i = 0u; i < 256; ++i) {
      var s = this._foundState + i * Ppmd8.StateSize;
      this.Symbol(s, i);
      this.Freq(s, 1);
      this.Successor(s, 0);
    }

    for (int i = 0, m = 0; m < 25; ++m) {
      while (this._ns2Index[i] == m)
        ++i;

      for (var k = 0; k < 8; ++k) {
        var value = (ushort)(Ppmd8.BinScale - Ppmd8.InitBinEsc[k] / (i + 1));
        for (var r = 0; r < 64; r += 8)
          this._binSumm[m * 64 + k + r] = value;
      }
    }

    for (int i = 0, m = 0; m < 24; ++m) {
      while (this._ns2Index[i + 3] == m + 3)
        ++i;

      for (var k = 0; k < 32; ++k) {
        var see = m * 32 + k;
        this._seeShift[see] = Ppmd8.PeriodBits - 4;
        this._seeSumm[see] = (ushort)((2 * i + 5) << (Ppmd8.PeriodBits - 4));
        this._seeCount[see] = 7;
      }
    }
  }

  private void Refresh(uint ctx, uint oldUnits, uint scale) {
    var i = this.NumStats(ctx);
    var s = this.ShrinkUnits(this.Stats(ctx), oldUnits, (i + 2) >> 1);
    this.Stats(ctx, s);
    var flags = (this.Flags(ctx) & (0x10 + 0x04 * scale)) + 0x08 * (this.Symbol(s) >= 0x40 ? 1u : 0);
    var escFreq = this.SummFreq(ctx) - this.Freq(s);
    var sumFreq = (this.Freq(s) + scale) >> (int)scale;
    this.Freq(s, sumFreq);
    do {
      s += Ppmd8.StateSize;
      escFreq -= this.Freq(s);
      var freq = (this.Freq(s) + scale) >> (int)scale;
      this.Freq(s, freq);
      sumFreq += freq;
      flags |= 0x08 * (this.Symbol(s) >= 0x40 ? 1u : 0);
    } while (--i != 0);

    this.SummFreq(ctx, sumFreq + ((escFreq + scale) >> (int)scale));
    this.Flags(ctx, flags);
  }

  private uint CutOff(uint ctx, uint order) {
    if (this.NumStats(ctx) == 0) {
      var one = OneState(ctx);
      if (this.Successor(one) >= this._unitsStart) {
        this.Successor(one, order < this._maxOrder ? this.CutOff(this.Successor(one), order + 1) : 0);
        if (this.Successor(one) != 0 || order <= 9)
          return ctx;
      }

      this.SpecialFreeUnit(ctx);
      return 0;
    }

    uint units;
    this.Stats(ctx, this.MoveUnitsUp(this.Stats(ctx), units = (this.NumStats(ctx) + 2) >> 1));

    var i = (int)this.NumStats(ctx);
    for (var s = this.Stats(ctx) + (uint)i * Ppmd8.StateSize; (int)s >= (int)this.Stats(ctx); s -= Ppmd8.StateSize) {
      if (this.Successor(s) < this._unitsStart) {
        var s2 = this.Stats(ctx) + (uint)i-- * Ppmd8.StateSize;
        this.Successor(s, 0);
        this.SwapStates(s, s2);
      } else if (order < this._maxOrder)
        this.Successor(s, this.CutOff(this.Successor(s), order + 1));
      else
        this.Successor(s, 0);
    }

    if (i != (int)this.NumStats(ctx) && order != 0) {
      this.NumStats(ctx, (uint)i);
      var s = this.Stats(ctx);
      if (i < 0) {
        this.FreeUnits(s, units);
        this.SpecialFreeUnit(ctx);
        return 0;
      }

      if (i == 0) {
        this.Flags(ctx, (this.Flags(ctx) & 0x10) + 0x08 * (this.Symbol(s) >= 0x40 ? 1u : 0));
        this.CopyState(OneState(ctx), s);
        this.FreeUnits(s, units);
        this.Freq(OneState(ctx), (this.Freq(OneState(ctx)) + 11) >> 3);
      } else
        this.Refresh(ctx, units, this.SummFreq(ctx) > 16 * (uint)i ? 1u : 0);
    }

    return ctx;
  }

  private uint GetUsedMemory() {
    var v = 0u;
    for (var i = 0u; i < Ppmd8.NumIndexes; ++i)
      v += this._stamps[i] * this.IndexToUnits(i);

    return this._size - (this._hiUnit - this._loUnit) - (this._unitsStart - this._text) - UnitsToBytes(v);
  }

  private void RestoreModel(uint c1) {
    this._text = this._alignOffset;
    uint c;
    for (c = this._maxContext; c != c1; c = this.Suffix(c)) {
      var numStats = this.NumStats(c) - 1;
      this.NumStats(c, numStats);
      if ((numStats & 0xFF) == 0) {
        var s = this.Stats(c);
        this.Flags(c, (this.Flags(c) & 0x10) + 0x08 * (this.Symbol(s) >= 0x40 ? 1u : 0));
        this.CopyState(OneState(c), s);
        this.SpecialFreeUnit(s);
        this.Freq(OneState(c), (this.Freq(OneState(c)) + 11) >> 3);
      } else
        this.Refresh(c, (this.NumStats(c) + 3) >> 1, 0);
    }

    for (; c != this._minContext; c = this.Suffix(c)) {
      if (this.NumStats(c) == 0)
        this.Freq(OneState(c), this.Freq(OneState(c)) - (this.Freq(OneState(c)) >> 1));
      else {
        var summ = (this.SummFreq(c) + 4) & 0xFFFF;
        this.SummFreq(c, summ);
        if (summ > 128 + 4 * this.NumStats(c))
          this.Refresh(c, (this.NumStats(c) + 2) >> 1, 1);
      }
    }

    if (this._restoreMethod == Ppmd8RestoreMethod.Restart || this.GetUsedMemory() < (this._size >> 1))
      this.RestartModel();
    else {
      while (this.Suffix(this._maxContext) != 0)
        this._maxContext = this.Suffix(this._maxContext);

      do {
        this.CutOff(this._maxContext, 0);
        this.ExpandTextArea();
      } while (this.GetUsedMemory() > 3 * (this._size >> 2));

      this._glueCount = 0;
      this._orderFall = this._maxOrder;
    }
  }

  private uint CreateSuccessors(bool skip, uint s1, uint c) {
    var upBranch = this.Successor(this._foundState);
    Span<uint> ps = stackalloc uint[Ppmd8.MaxOrderLimit + 1];
    var numPs = 0;

    if (!skip)
      ps[numPs++] = this._foundState;

    while (this.Suffix(c) != 0) {
      uint s;
      c = this.Suffix(c);
      if (s1 != 0) {
        s = s1;
        s1 = 0;
      } else if (this.NumStats(c) != 0) {
        var symbol = this.Symbol(this._foundState);
        for (s = this.Stats(c); this.Symbol(s) != symbol; s += Ppmd8.StateSize) { }

        if (this.Freq(s) < Ppmd8.MaxFreq - 9) {
          this.Freq(s, this.Freq(s) + 1);
          this.SummFreq(c, this.SummFreq(c) + 1);
        }
      } else {
        s = OneState(c);
        if (this.NumStats(this.Suffix(c)) == 0 && this.Freq(s) < 24)
          this.Freq(s, this.Freq(s) + 1);
      }

      var successor = this.Successor(s);
      if (successor != upBranch) {
        c = successor;
        if (numPs == 0)
          return c;

        break;
      }

      ps[numPs++] = s;
    }

    var upSymbol = this._heap[upBranch];
    var upSuccessor = upBranch + 1;
    var flags = 0x10 * (this.Symbol(this._foundState) >= 0x40 ? 1u : 0) + 0x08 * (upSymbol >= 0x40 ? 1u : 0);

    uint upFreq;
    if (this.NumStats(c) == 0)
      upFreq = this.Freq(OneState(c));
    else {
      uint s;
      for (s = this.Stats(c); this.Symbol(s) != upSymbol; s += Ppmd8.StateSize) { }

      var cf = this.Freq(s) - 1;
      var s0 = this.SummFreq(c) - this.NumStats(c) - cf;
      upFreq = 1 + (2 * cf <= s0 ? (5 * cf > s0 ? 1u : 0) : (cf + 2 * s0 - 3) / s0);
    }

    do {
      uint c1;
      if (this._hiUnit != this._loUnit)
        c1 = this._hiUnit -= Ppmd8.UnitSize;
      else if (this._freeList[0] != 0)
        c1 = this.RemoveNode(0);
      else {
        c1 = this.AllocUnitsRare(0);
        if (c1 == 0)
          return 0;
      }

      this.NumStats(c1, 0);
      this.Flags(c1, flags);
      var one = OneState(c1);
      this.Symbol(one, upSymbol);
      this.Freq(one, upFreq);
      this.Successor(one, upSuccessor);
      this.Suffix(c1, c);
      this.Successor(ps[--numPs], c1);
      c = c1;
    } while (numPs != 0);

    return c;
  }

  private uint ReduceOrder(uint s1, uint c) {
    var s = 0u;
    var c1 = c;
    var upBranch = this._text;

    this.Successor(this._foundState, upBranch);
    ++this._orderFall;

    for (;;) {
      if (s1 != 0) {
        c = this.Suffix(c);
        s = s1;
        s1 = 0;
      } else {
        if (this.Suffix(c) == 0)
          return c;

        c = this.Suffix(c);
        if (this.NumStats(c) != 0) {
          var symbol = this.Symbol(this._foundState);
          s = this.Stats(c);
          while (this.Symbol(s) != symbol)
            s += Ppmd8.StateSize;

          if (this.Freq(s) < Ppmd8.MaxFreq - 9) {
            this.Freq(s, this.Freq(s) + 2);
            this.SummFreq(c, this.SummFreq(c) + 2);
          }
        } else {
          s = OneState(c);
          if (this.Freq(s) < 32)
            this.Freq(s, this.Freq(s) + 1);
        }
      }

      if (this.Successor(s) != 0)
        break;

      this.Successor(s, upBranch);
      ++this._orderFall;
    }

    if (this.Successor(s) <= upBranch) {
      var saved = this._foundState;
      this._foundState = s;
      var successor = this.CreateSuccessors(false, 0, c);
      this.Successor(s, successor);
      this._foundState = saved;
    }

    if (this._orderFall == 1 && c1 == this._maxContext) {
      this.Successor(this._foundState, this.Successor(s));
      --this._text;
    }

    return this.Successor(s);
  }

  private void UpdateModel() {
    var fSuccessor = this.Successor(this._foundState);
    var fFreq = this.Freq(this._foundState);
    var fSymbol = this.Symbol(this._foundState);
    var s = 0u;

    if (fFreq < Ppmd8.MaxFreq / 4 && this.Suffix(this._minContext) != 0) {
      var c0 = this.Suffix(this._minContext);
      if (this.NumStats(c0) == 0) {
        s = OneState(c0);
        if (this.Freq(s) < 32)
          this.Freq(s, this.Freq(s) + 1);
      } else {
        s = this.Stats(c0);
        if (this.Symbol(s) != fSymbol) {
          do {
            s += Ppmd8.StateSize;
          } while (this.Symbol(s) != fSymbol);

          if (this.Freq(s) >= this.Freq(s - Ppmd8.StateSize)) {
            this.SwapStates(s, s - Ppmd8.StateSize);
            s -= Ppmd8.StateSize;
          }
        }

        if (this.Freq(s) < Ppmd8.MaxFreq - 9) {
          this.Freq(s, this.Freq(s) + 2);
          this.SummFreq(c0, this.SummFreq(c0) + 2);
        }
      }
    }

    var c = this._maxContext;
    if (this._orderFall == 0 && fSuccessor != 0) {
      var cs = this.CreateSuccessors(true, s, this._minContext);
      if (cs == 0) {
        this.Successor(this._foundState, 0);
        this.RestoreModel(c);
      } else {
        this.Successor(this._foundState, cs);
        this._maxContext = cs;
      }

      return;
    }

    this._heap[this._text++] = (byte)fSymbol;
    var successor = this._text;
    if (this._text >= this._unitsStart) {
      this.RestoreModel(c);
      return;
    }

    if (fSuccessor == 0) {
      var cs = this.ReduceOrder(s, this._minContext);
      if (cs == 0) {
        this.RestoreModel(c);
        return;
      }

      fSuccessor = cs;
    } else if (fSuccessor < this._unitsStart) {
      var cs = this.CreateSuccessors(false, s, this._minContext);
      if (cs == 0) {
        this.RestoreModel(c);
        return;
      }

      fSuccessor = cs;
    }

    if (--this._orderFall == 0) {
      successor = fSuccessor;
      if (this._maxContext != this._minContext)
        --this._text;
    }

    var ns = this.NumStats(this._minContext);
    var s0 = this.SummFreq(this._minContext) - ns - fFreq;
    var flag = 0x08 * (fSymbol >= 0x40 ? 1u : 0);

    for (; c != this._minContext; c = this.Suffix(c)) {
      var ns1 = this.NumStats(c);
      if (ns1 != 0) {
        if ((ns1 & 1) != 0) {
          // Grow the state array by one unit.
          var oldUnits = (ns1 + 1) >> 1;
          var i = this.UnitsToIndex(oldUnits);
          if (i != this.UnitsToIndex(oldUnits + 1)) {
            var ptr = this.AllocUnits(i + 1);
            if (ptr == 0) {
              this.RestoreModel(c);
              return;
            }

            var oldPtr = this.Stats(c);
            this.CopyUnits(ptr, oldPtr, oldUnits);
            this.InsertNode(oldPtr, i);
            this.Stats(c, ptr);
          }
        }

        this.SummFreq(c, this.SummFreq(c) + (3 * ns1 + 1 < ns ? 1u : 0));
      } else {
        var ns0 = this.AllocUnits(0);
        if (ns0 == 0) {
          this.RestoreModel(c);
          return;
        }

        this.CopyState(ns0, OneState(c));
        this.Stats(c, ns0);
        var freq = this.Freq(ns0);
        this.Freq(ns0, freq < Ppmd8.MaxFreq / 4 - 1 ? freq << 1 : Ppmd8.MaxFreq - 4);
        this.SummFreq(c, this.Freq(ns0) + this._initEsc + (ns > 2 ? 1u : 0));
      }

      var cf = 2 * fFreq * (this.SummFreq(c) + 6);
      var sf = s0 + this.SummFreq(c);
      if (cf < 6 * sf) {
        cf = 1 + (cf > sf ? 1u : 0) + (cf >= 4 * sf ? 1u : 0);
        this.SummFreq(c, this.SummFreq(c) + 4);
      } else {
        cf = 4 + (cf > 9 * sf ? 1u : 0) + (cf > 12 * sf ? 1u : 0) + (cf > 15 * sf ? 1u : 0);
        this.SummFreq(c, this.SummFreq(c) + cf);
      }

      var added = this.Stats(c) + (ns1 + 1) * Ppmd8.StateSize;
      this.Successor(added, successor);
      this.Symbol(added, fSymbol);
      this.Freq(added, cf);
      this.Flags(c, this.Flags(c) | flag);
      this.NumStats(c, ns1 + 1);
    }

    this._maxContext = this._minContext = fSuccessor;
  }

  private void Rescale() {
    var stats = this.Stats(this._minContext);
    var s = this._foundState;

    // Move the found state to the front.
    Span<byte> tmp = stackalloc byte[Ppmd8.StateSize];
    this._heap.AsSpan((int)s, Ppmd8.StateSize).CopyTo(tmp);
    for (; s != stats; s -= Ppmd8.StateSize)
      this.CopyState(s, s - Ppmd8.StateSize);

    tmp.CopyTo(this._heap.AsSpan((int)s, Ppmd8.StateSize));

    var escFreq = this.SummFreq(this._minContext) - this.Freq(s);
    var adder = this._orderFall != 0 ? 1u : 0;
    var freq0 = (this.Freq(s) + 4 + adder) >> 1;
    this.Freq(s, freq0);
    var sumFreq = freq0;

    var i = this.NumStats(this._minContext);
    do {
      s += Ppmd8.StateSize;
      escFreq -= this.Freq(s);
      var freq = (this.Freq(s) + adder) >> 1;
      this.Freq(s, freq);
      sumFreq += freq;
      if (freq > this.Freq(s - Ppmd8.StateSize)) {
        // Insertion sort: carry the state back past every lower-frequency one.
        var s1 = s;
        this._heap.AsSpan((int)s1, Ppmd8.StateSize).CopyTo(tmp);
        do {
          this.CopyState(s1, s1 - Ppmd8.StateSize);
          s1 -= Ppmd8.StateSize;
        } while (s1 != stats && tmp[1] > this.Freq(s1 - Ppmd8.StateSize));

        tmp.CopyTo(this._heap.AsSpan((int)s1, Ppmd8.StateSize));
      }
    } while (--i != 0);

    if (this.Freq(s) == 0) {
      var numStats = this.NumStats(this._minContext);
      do {
        ++i;
        s -= Ppmd8.StateSize;
      } while (this.Freq(s) == 0);

      escFreq += i;
      this.NumStats(this._minContext, this.NumStats(this._minContext) - i);
      if (this.NumStats(this._minContext) == 0) {
        this._heap.AsSpan((int)stats, Ppmd8.StateSize).CopyTo(tmp);
        // The quotient is narrowed to a byte before it is capped, as in the original.
        var freq = (byte)((2 * (uint)tmp[1] + escFreq - 1) / escFreq);
        tmp[1] = freq > Ppmd8.MaxFreq / 3 ? (byte)(Ppmd8.MaxFreq / 3) : freq;
        this.InsertNode(stats, this.UnitsToIndex((numStats + 2) >> 1));
        this.Flags(this._minContext, (this.Flags(this._minContext) & 0x10) + 0x08 * (tmp[0] >= 0x40 ? 1u : 0));
        this._foundState = OneState(this._minContext);
        tmp.CopyTo(this._heap.AsSpan((int)this._foundState, Ppmd8.StateSize));
        return;
      }

      var n0 = (numStats + 2) >> 1;
      var n1 = (this.NumStats(this._minContext) + 2) >> 1;
      if (n0 != n1)
        this.Stats(this._minContext, this.ShrinkUnits(stats, n0, n1));

      var flags = this.Flags(this._minContext) & ~0x08u;
      s = this.Stats(this._minContext);
      flags |= 0x08 * (this.Symbol(s) >= 0x40 ? 1u : 0);
      i = this.NumStats(this._minContext);
      do {
        s += Ppmd8.StateSize;
        flags |= 0x08 * (this.Symbol(s) >= 0x40 ? 1u : 0);
      } while (--i != 0);

      this.Flags(this._minContext, flags);
    }

    this.SummFreq(this._minContext, sumFreq + escFreq - (escFreq >> 1));
    this.Flags(this._minContext, this.Flags(this._minContext) | 0x4);
    this._foundState = this.Stats(this._minContext);
  }

  private int MakeEscFreq(uint numMasked, out uint escFreq) {
    var numStats = this.NumStats(this._minContext);
    if (numStats == 0xFF) {
      escFreq = 1;
      return Ppmd8.DummySee;
    }

    var see = (this._ns2Index[numStats + 2] - 3) * 32
              + (this.SummFreq(this._minContext) > 11 * (numStats + 1) ? 1 : 0)
              + 2 * (2 * numStats < this.NumStats(this.Suffix(this._minContext)) + numMasked ? 1 : 0)
              + (int)this.Flags(this._minContext);
    var r = (uint)(this._seeSumm[see] >> this._seeShift[see]);
    this._seeSumm[see] = (ushort)(this._seeSumm[see] - r);
    escFreq = r + (r == 0 ? 1u : 0);
    return see;
  }

  private void SeeUpdate(int see) {
    if (this._seeShift[see] < Ppmd8.PeriodBits && --this._seeCount[see] == 0) {
      this._seeSumm[see] <<= 1;
      this._seeCount[see] = (byte)(3 << this._seeShift[see]++);
    }
  }

  private void NextContext() {
    var c = this.Successor(this._foundState);
    if (this._orderFall == 0 && c >= this._unitsStart)
      this._minContext = this._maxContext = c;
    else {
      this.UpdateModel();
      this._minContext = this._maxContext;
    }
  }

  private void Update1() {
    var s = this._foundState;
    this.Freq(s, this.Freq(s) + 4);
    this.SummFreq(this._minContext, this.SummFreq(this._minContext) + 4);
    if (this.Freq(s) > this.Freq(s - Ppmd8.StateSize)) {
      this.SwapStates(s, s - Ppmd8.StateSize);
      this._foundState = s -= Ppmd8.StateSize;
      if (this.Freq(s) > Ppmd8.MaxFreq)
        this.Rescale();
    }

    this.NextContext();
  }

  private void Update1First() {
    this._prevSuccess = 2 * this.Freq(this._foundState) >= this.SummFreq(this._minContext) ? 1u : 0;
    this._runLength += (int)this._prevSuccess;
    this.SummFreq(this._minContext, this.SummFreq(this._minContext) + 4);
    var freq = this.Freq(this._foundState) + 4;
    this.Freq(this._foundState, freq);
    if (freq > Ppmd8.MaxFreq)
      this.Rescale();

    this.NextContext();
  }

  private void UpdateBin() {
    var freq = this.Freq(this._foundState);
    this.Freq(this._foundState, freq + (freq < 196 ? 1u : 0));
    this._prevSuccess = 1;
    ++this._runLength;
    this.NextContext();
  }

  private void Update2() {
    this.SummFreq(this._minContext, this.SummFreq(this._minContext) + 4);
    var freq = this.Freq(this._foundState) + 4;
    this.Freq(this._foundState, freq);
    if (freq > Ppmd8.MaxFreq)
      this.Rescale();

    this._runLength = this._initRunLength;
    this.UpdateModel();
    this._minContext = this._maxContext;
  }

  private int BinSummIndex() {
    var one = OneState(this._minContext);
    return this._ns2Index[this.Freq(one) - 1] * 64
           + this._ns2BsIndex[this.NumStats(this.Suffix(this._minContext))]
           + (int)this._prevSuccess
           + (int)this.Flags(this._minContext)
           + ((this._runLength >> 26) & 0x20);
  }
}
