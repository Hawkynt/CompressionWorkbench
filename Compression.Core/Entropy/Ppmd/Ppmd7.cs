using System.Buffers.Binary;

namespace Compression.Core.Entropy.Ppmd;

/// <summary>The range decoder a <see cref="Ppmd7"/> model draws its symbols from.</summary>
public interface IPpmd7RangeDecoder {
  /// <summary>Scales the range to <paramref name="total"/> and returns the current count.</summary>
  uint GetThreshold(uint total);

  /// <summary>Consumes the interval [<paramref name="start"/>, <paramref name="start"/> + <paramref name="size"/>).</summary>
  void Decode(uint start, uint size);

  /// <summary>Decodes one binary decision with probability <paramref name="size0"/> / 2^14 for 0.</summary>
  uint DecodeBit(uint size0);
}

/// <summary>
/// The carry-less range decoder RAR 3.x uses for PPMd (Subbotin's coder, 15-bit bottom).
/// </summary>
public sealed class RarPpmdRangeDecoder : IPpmd7RangeDecoder {
  private const uint Top = 1u << 24;
  private const uint Bottom = 1u << 15;
  private readonly Func<byte> _next;
  private uint _low, _code, _range;

  /// <summary>Starts decoding: reads the first four code bytes from <paramref name="next"/>.</summary>
  public RarPpmdRangeDecoder(Func<byte> next) {
    this._next = next;
    this._range = 0xFFFFFFFF;
    for (var i = 0; i < 4; ++i) this._code = (this._code << 8) | next();
  }

  /// <inheritdoc />
  public uint GetThreshold(uint total) => (this._code - this._low) / (this._range /= total);

  /// <inheritdoc />
  public void Decode(uint start, uint size) {
    this._low += start * this._range;
    this._range *= size;
    for (;;) {
      if ((this._low ^ (this._low + this._range)) >= Top) {
        if (this._range >= Bottom) break;
        this._range = (uint)-(int)this._low & (Bottom - 1);
      }
      this._code = (this._code << 8) | this._next();
      this._range <<= 8;
      this._low <<= 8;
    }
  }

  /// <inheritdoc />
  public uint DecodeBit(uint size0) {
    if (this.GetThreshold(Ppmd7.BinScale) < size0) {
      this.Decode(0, size0);
      return 0;
    }
    this.Decode(size0, Ppmd7.BinScale - size0);
    return 1;
  }
}

/// <summary>
/// PPMd variant H (Dmitry Shkarin) exactly as 7-Zip's Ppmd7 and RAR 3.x model it: the
/// sub-allocator, the context tree, SEE and binary contexts, all in one byte heap.
/// </summary>
/// <remarks>
/// Converted from Igor Pavlov's public-domain Ppmd7.c (2010-03-12), as carried in libarchive's
/// <c>archive_ppmd7.c</c>; see <c>THIRD-PARTY-NOTICE.Ppmd7.txt</c>. Heap references are byte
/// offsets; offset 0 is the null reference.
/// </remarks>
public sealed class Ppmd7 {
  internal const uint BinScale = 1 << (IntBits + PeriodBits);
  private const int IntBits = 7;
  private const int PeriodBits = 7;
  private const int MaxOrderLimit = 64;
  private const int UnitSize = 12;
  private const int MaxFreq = 124;
  private const int NumIndexes = 4 + 4 + 4 + 26;

  private static readonly ushort[] InitBinEsc = [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x64A1, 0x5ABC, 0x6632, 0x6051];
  private static readonly byte[] ExpEscape = [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];

  private sealed class See {
    public ushort Summ;
    public byte Shift;
    public byte Count;

    public void Update() {
      if (this.Shift < PeriodBits && --this.Count == 0) {
        this.Summ <<= 1;
        this.Count = (byte)(3 << this.Shift++);
      }
    }
  }

  private byte[] _heap = [];
  private uint _size, _alignOffset;
  private uint _minContext, _maxContext, _foundState;
  private uint _orderFall, _prevSuccess, _maxOrder, _hiBitsFlag;
  private int _runLength, _initRunLength;
  private uint _glueCount;
  private uint _text, _loUnit, _hiUnit, _unitsStart;
  private readonly byte[] _indexToUnits = new byte[NumIndexes];
  private readonly byte[] _unitsToIndex = new byte[128];
  private readonly uint[] _freeList = new uint[NumIndexes];
  private readonly byte[] _ns2Index = new byte[256], _ns2BsIndex = new byte[256], _hb2Flag = new byte[256];
  private readonly See _dummySee = new();
  private readonly See[] _see = [.. Enumerable.Range(0, 25 * 16).Select(_ => new See())];
  private readonly ushort[] _binSumm = new ushort[128 * 64];
  private readonly sbyte[] _charMask = new sbyte[256];
  private readonly uint[] _ps = new uint[256];

  /// <summary>Escape frequency for new binary contexts; RAR may set it per block.</summary>
  public uint InitEsc { get; set; }

  /// <summary>Builds the static tables.</summary>
  public Ppmd7() {
    for (int i = 0, k = 0; i < NumIndexes; ++i) {
      var step = i >= 12 ? 4 : (i >> 2) + 1;
      do this._unitsToIndex[k++] = (byte)i; while (--step != 0);
      this._indexToUnits[i] = (byte)k;
    }
    this._ns2BsIndex[0] = 0 << 1;
    this._ns2BsIndex[1] = 1 << 1;
    this._ns2BsIndex.AsSpan(2, 9).Fill(2 << 1);
    this._ns2BsIndex.AsSpan(11).Fill(3 << 1);
    int n;
    for (n = 0; n < 3; ++n) this._ns2Index[n] = (byte)n;
    for (int m = n, k = 1; n < 256; ++n) {
      this._ns2Index[n] = (byte)m;
      if (--k == 0) k = ++m - 2;
    }
    this._hb2Flag.AsSpan(0x40).Fill(8);
  }

  /// <summary>Allocates (or keeps) a heap of <paramref name="size"/> bytes.</summary>
  public void Allocate(uint size) {
    if (size < UnitSize) throw new ArgumentOutOfRangeException(nameof(size));
    if (this._heap.Length != 0 && this._size == size) return;
    this._alignOffset = 4 - (size & 3);
    this._heap = new byte[this._alignOffset + size + UnitSize];
    this._size = size;
  }

  /// <summary>Resets the model for a new stream with the given maximum order.</summary>
  public void Initialize(int maxOrder) {
    if (maxOrder is < 2 or > MaxOrderLimit) throw new ArgumentOutOfRangeException(nameof(maxOrder));
    if (this._heap.Length == 0) throw new InvalidOperationException("Ppmd7: no heap allocated.");
    this._maxOrder = (uint)maxOrder;
    this.RestartModel();
    this._dummySee.Shift = PeriodBits;
    this._dummySee.Summ = 0;
    this._dummySee.Count = 64;
  }

  // ── heap access ──────────────────────────────────────────────────────

  private ushort U16(uint at) => BinaryPrimitives.ReadUInt16LittleEndian(this._heap.AsSpan((int)at));
  private void U16(uint at, uint v) => BinaryPrimitives.WriteUInt16LittleEndian(this._heap.AsSpan((int)at), (ushort)v);
  private uint U32(uint at) => BinaryPrimitives.ReadUInt32LittleEndian(this._heap.AsSpan((int)at));
  private void U32(uint at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(this._heap.AsSpan((int)at), v);

  // Context: NumStats u16 @0, SummFreq u16 @2, Stats u32 @4, Suffix u32 @8; its single state overlays @2.
  private uint NumStats(uint c) => this.U16(c);
  private void NumStats(uint c, uint v) => this.U16(c, v);
  private uint SummFreq(uint c) => this.U16(c + 2);
  private void SummFreq(uint c, uint v) => this.U16(c + 2, v);
  private uint Stats(uint c) => this.U32(c + 4);
  private void Stats(uint c, uint v) => this.U32(c + 4, v);
  private uint Suffix(uint c) => this.U32(c + 8);
  private void Suffix(uint c, uint v) => this.U32(c + 8, v);
  private static uint OneState(uint c) => c + 2;

  // State: Symbol @0, Freq @1, Successor u32 @2 (two u16 halves).
  private byte Symbol(uint s) => this._heap[s];
  private void Symbol(uint s, uint v) => this._heap[s] = (byte)v;
  private byte Freq(uint s) => this._heap[s + 1];
  private void Freq(uint s, uint v) => this._heap[s + 1] = (byte)v;
  private uint Successor(uint s) => this.U32(s + 2);
  private void Successor(uint s, uint v) => this.U32(s + 2, v);
  private void CopyState(uint to, uint from) => this._heap.AsSpan((int)from, 6).CopyTo(this._heap.AsSpan((int)to));

  private void SwapStates(uint a, uint b) {
    Span<byte> t = stackalloc byte[6];
    this._heap.AsSpan((int)a, 6).CopyTo(t);
    this._heap.AsSpan((int)b, 6).CopyTo(this._heap.AsSpan((int)a));
    t.CopyTo(this._heap.AsSpan((int)b));
  }

  // Free-block node: Stamp u16 @0, NU u16 @2, Next u32 @4, Prev u32 @8.
  private uint UnitsToBytes(uint nu) => nu * UnitSize;
  private uint IndexToUnits(uint index) => this._indexToUnits[index];
  private uint UnitsToIndex(uint nu) => this._unitsToIndex[nu - 1];

  // ── sub-allocator ────────────────────────────────────────────────────

  private void InsertNode(uint node, uint index) {
    this.U32(node, this._freeList[index]);
    this._freeList[index] = node;
  }

  private uint RemoveNode(uint index) {
    var node = this._freeList[index];
    this._freeList[index] = this.U32(node);
    return node;
  }

  private void SplitBlock(uint ptr, uint oldIndex, uint newIndex) {
    var nu = this.IndexToUnits(oldIndex) - this.IndexToUnits(newIndex);
    ptr += this.UnitsToBytes(this.IndexToUnits(newIndex));
    var i = this.UnitsToIndex(nu);
    if (this.IndexToUnits(i) != nu) {
      var k = this.IndexToUnits(--i);
      this.InsertNode(ptr + this.UnitsToBytes(k), nu - k - 1);
    }
    this.InsertNode(ptr, i);
  }

  private void GlueFreeBlocks() {
    var head = this._alignOffset + this._size;
    var n = head;
    this._glueCount = 255;

    for (uint i = 0; i < NumIndexes; ++i) {
      var nu = this.IndexToUnits(i);
      var next = this._freeList[i];
      this._freeList[i] = 0;
      while (next != 0) {
        var node = next;
        this.U32(node + 4, n);        // node.Next = n
        this.U32(n + 8, next);        // n.Prev = next
        n = next;
        next = this.U32(node);        // the free-list link
        this.U16(node, 0);            // Stamp
        this.U16(node + 2, nu);       // NU
      }
    }
    this.U16(head, 1);
    this.U32(head + 4, n);
    this.U32(n + 8, head);
    if (this._loUnit != this._hiUnit) this.U16(this._loUnit, 1);

    while (n != head) {
      var node = n;
      uint nu = this.U16(node + 2);
      for (;;) {
        var node2 = node + nu * UnitSize;
        nu += this.U16(node2 + 2);
        if (this.U16(node2) != 0 || nu >= 0x10000) break;
        this.U32(this.U32(node2 + 8) + 4, this.U32(node2 + 4));
        this.U32(this.U32(node2 + 4) + 8, this.U32(node2 + 8));
        this.U16(node + 2, nu);
      }
      n = this.U32(node + 4);
    }

    for (n = this.U32(head + 4); n != head;) {
      var node = n;
      var next = this.U32(node + 4);
      uint nu;
      for (nu = this.U16(node + 2); nu > 128; nu -= 128, node += 128 * UnitSize)
        this.InsertNode(node, NumIndexes - 1);
      var i = this.UnitsToIndex(nu);
      if (this.IndexToUnits(i) != nu) {
        var k = this.IndexToUnits(--i);
        this.InsertNode(node + k * UnitSize, nu - k - 1);
      }
      this.InsertNode(node, i);
      n = next;
    }
  }

  private uint AllocUnitsRare(uint index) {
    if (this._glueCount == 0) {
      this.GlueFreeBlocks();
      if (this._freeList[index] != 0) return this.RemoveNode(index);
    }
    var i = index;
    do {
      if (++i == NumIndexes) {
        var numBytes = this.UnitsToBytes(this.IndexToUnits(index));
        --this._glueCount;
        return this._unitsStart - this._text > numBytes ? this._unitsStart -= numBytes : 0;
      }
    } while (this._freeList[i] == 0);
    var block = this.RemoveNode(i);
    this.SplitBlock(block, i, index);
    return block;
  }

  private uint AllocUnits(uint index) {
    if (this._freeList[index] != 0) return this.RemoveNode(index);
    var numBytes = this.UnitsToBytes(this.IndexToUnits(index));
    if (numBytes <= this._hiUnit - this._loUnit) {
      var block = this._loUnit;
      this._loUnit += numBytes;
      return block;
    }
    return this.AllocUnitsRare(index);
  }

  private uint ShrinkUnits(uint oldPtr, uint oldNu, uint newNu) {
    var i0 = this.UnitsToIndex(oldNu);
    var i1 = this.UnitsToIndex(newNu);
    if (i0 == i1) return oldPtr;
    if (this._freeList[i1] != 0) {
      var ptr = this.RemoveNode(i1);
      this._heap.AsSpan((int)oldPtr, (int)(newNu * UnitSize)).CopyTo(this._heap.AsSpan((int)ptr));
      this.InsertNode(oldPtr, i0);
      return ptr;
    }
    this.SplitBlock(oldPtr, i0, i1);
    return oldPtr;
  }

  // ── model ────────────────────────────────────────────────────────────

  private void RestartModel() {
    Array.Clear(this._freeList);
    this._text = this._alignOffset;
    this._hiUnit = this._text + this._size;
    this._loUnit = this._unitsStart = this._hiUnit - this._size / 8 / UnitSize * 7 * UnitSize;
    this._glueCount = 0;

    this._orderFall = this._maxOrder;
    this._runLength = this._initRunLength = -(int)Math.Min(this._maxOrder, 12) - 1;
    this._prevSuccess = 0;

    this._hiUnit -= UnitSize;
    this._minContext = this._maxContext = this._hiUnit;
    this.Suffix(this._minContext, 0);
    this.NumStats(this._minContext, 256);
    this.SummFreq(this._minContext, 256 + 1);
    this._foundState = this._loUnit;
    this.Stats(this._minContext, this._foundState);
    this._loUnit += this.UnitsToBytes(256 / 2);
    for (uint i = 0; i < 256; ++i) {
      var s = this._foundState + i * 6;
      this.Symbol(s, i);
      this.Freq(s, 1);
      this.Successor(s, 0);
    }

    for (var i = 0; i < 128; ++i)
      for (var k = 0; k < 8; ++k) {
        var value = (ushort)(BinScale - InitBinEsc[k] / (i + 2));
        for (var m = 0; m < 64; m += 8) this._binSumm[i * 64 + k + m] = value;
      }

    for (var i = 0; i < 25; ++i)
      for (var k = 0; k < 16; ++k) {
        var see = this._see[i * 16 + k];
        see.Shift = PeriodBits - 4;
        see.Summ = (ushort)((5 * i + 10) << see.Shift);
        see.Count = 4;
      }
  }

  private uint CreateSuccessors(bool skip) {
    var c = this._minContext;
    var upBranch = this.Successor(this._foundState);
    Span<uint> ps = stackalloc uint[MaxOrderLimit];
    var numPs = 0;
    if (!skip) ps[numPs++] = this._foundState;

    while (this.Suffix(c) != 0) {
      c = this.Suffix(c);
      uint s;
      if (this.NumStats(c) != 1) {
        for (s = this.Stats(c); this.Symbol(s) != this.Symbol(this._foundState); s += 6) { }
      } else
        s = OneState(c);
      var successor = this.Successor(s);
      if (successor != upBranch) {
        c = successor;
        if (numPs == 0) return c;
        break;
      }
      ps[numPs++] = s;
    }

    var upSymbol = this._heap[upBranch];
    var upSuccessor = upBranch + 1;
    uint upFreq;
    if (this.NumStats(c) == 1)
      upFreq = this.Freq(OneState(c));
    else {
      uint s;
      for (s = this.Stats(c); this.Symbol(s) != upSymbol; s += 6) { }
      var cf = (uint)this.Freq(s) - 1;
      var s0 = this.SummFreq(c) - this.NumStats(c) - cf;
      upFreq = 1 + (2 * cf <= s0 ? (5 * cf > s0 ? 1u : 0u) : (2 * cf + 3 * s0 - 1) / (2 * s0));
    }

    while (numPs != 0) {
      uint c1;
      if (this._hiUnit != this._loUnit)
        c1 = this._hiUnit -= UnitSize;
      else if (this._freeList[0] != 0)
        c1 = this.RemoveNode(0);
      else {
        c1 = this.AllocUnitsRare(0);
        if (c1 == 0) return 0;
      }
      this.NumStats(c1, 1);
      var one = OneState(c1);
      this.Symbol(one, upSymbol);
      this.Freq(one, upFreq);
      this.Successor(one, upSuccessor);
      this.Suffix(c1, c);
      this.Successor(ps[--numPs], c1);
      c = c1;
    }
    return c;
  }

  private void UpdateModel() {
    var fSuccessor = this.Successor(this._foundState);
    var fSymbol = this.Symbol(this._foundState);

    if (this.Freq(this._foundState) < MaxFreq / 4 && this.Suffix(this._minContext) != 0) {
      var c = this.Suffix(this._minContext);
      if (this.NumStats(c) == 1) {
        var s = OneState(c);
        if (this.Freq(s) < 32) this.Freq(s, this.Freq(s) + 1u);
      } else {
        var s = this.Stats(c);
        if (this.Symbol(s) != fSymbol) {
          do s += 6; while (this.Symbol(s) != fSymbol);
          if (this.Freq(s) >= this.Freq(s - 6)) {
            this.SwapStates(s, s - 6);
            s -= 6;
          }
        }
        if (this.Freq(s) < MaxFreq - 9) {
          this.Freq(s, this.Freq(s) + 2u);
          this.SummFreq(c, this.SummFreq(c) + 2);
        }
      }
    }

    if (this._orderFall == 0) {
      this._minContext = this._maxContext = this.CreateSuccessors(true);
      if (this._minContext == 0) {
        this.RestartModel();
        return;
      }
      this.Successor(this._foundState, this._minContext);
      return;
    }

    this._heap[this._text++] = fSymbol;
    var successor = this._text;
    if (this._text >= this._unitsStart) {
      this.RestartModel();
      return;
    }

    if (fSuccessor != 0) {
      if (fSuccessor <= successor) {
        var cs = this.CreateSuccessors(false);
        if (cs == 0) {
          this.RestartModel();
          return;
        }
        fSuccessor = cs;
      }
      if (--this._orderFall == 0) {
        successor = fSuccessor;
        if (this._maxContext != this._minContext) --this._text;
      }
    } else {
      this.Successor(this._foundState, successor);
      fSuccessor = this._minContext;
    }

    var ns = this.NumStats(this._minContext);
    var s0 = this.SummFreq(this._minContext) - ns - (this.Freq(this._foundState) - 1u);

    for (var c = this._maxContext; c != this._minContext; c = this.Suffix(c)) {
      var ns1 = this.NumStats(c);
      if (ns1 != 1) {
        if ((ns1 & 1) == 0) {
          var oldNu = ns1 >> 1;
          var i = this.UnitsToIndex(oldNu);
          if (i != this.UnitsToIndex(oldNu + 1)) {
            var ptr = this.AllocUnits(i + 1);
            if (ptr == 0) {
              this.RestartModel();
              return;
            }
            var oldPtr = this.Stats(c);
            this._heap.AsSpan((int)oldPtr, (int)(oldNu * UnitSize)).CopyTo(this._heap.AsSpan((int)ptr));
            this.InsertNode(oldPtr, i);
            this.Stats(c, ptr);
          }
        }
        var summ = this.SummFreq(c);
        this.SummFreq(c, summ + (2 * ns1 < ns ? 1u : 0u) + 2 * ((4 * ns1 <= ns ? 1u : 0u) & (summ <= 8 * ns1 ? 1u : 0u)));
      } else {
        var s = this.AllocUnits(0);
        if (s == 0) {
          this.RestartModel();
          return;
        }
        this.CopyState(s, OneState(c));
        this.Stats(c, s);
        var f = (uint)this.Freq(s);
        this.Freq(s, f < MaxFreq / 4 - 1 ? f << 1 : MaxFreq - 4);
        this.SummFreq(c, this.Freq(s) + this.InitEsc + (ns > 3 ? 1u : 0u));
      }
      var cf = 2u * this.Freq(this._foundState) * (this.SummFreq(c) + 6);
      var sf = s0 + this.SummFreq(c);
      if (cf < 6 * sf) {
        cf = 1 + (cf > sf ? 1u : 0u) + (cf >= 4 * sf ? 1u : 0u);
        this.SummFreq(c, this.SummFreq(c) + 3);
      } else {
        cf = 4 + (cf >= 9 * sf ? 1u : 0u) + (cf >= 12 * sf ? 1u : 0u) + (cf >= 15 * sf ? 1u : 0u);
        this.SummFreq(c, this.SummFreq(c) + cf);
      }
      var ns2 = this.Stats(c) + ns1 * 6;
      this.Successor(ns2, successor);
      this.Symbol(ns2, fSymbol);
      this.Freq(ns2, cf);
      this.NumStats(c, ns1 + 1);
    }
    this._maxContext = this._minContext = fSuccessor;
  }

  private void Rescale() {
    var stats = this.Stats(this._minContext);
    var s = this._foundState;
    Span<byte> tmp = stackalloc byte[6];
    this._heap.AsSpan((int)s, 6).CopyTo(tmp);
    for (; s != stats; s -= 6) this.CopyState(s, s - 6);
    tmp.CopyTo(this._heap.AsSpan((int)s));

    var escFreq = this.SummFreq(this._minContext) - this.Freq(s);
    this.Freq(s, this.Freq(s) + 4u);
    var adder = this._orderFall != 0 ? 1u : 0u;
    this.Freq(s, (this.Freq(s) + adder) >> 1);
    uint sumFreq = this.Freq(s);

    var i = this.NumStats(this._minContext) - 1;
    do {
      s += 6;
      escFreq -= this.Freq(s);
      this.Freq(s, (this.Freq(s) + adder) >> 1);
      sumFreq += this.Freq(s);
      if (this.Freq(s) > this.Freq(s - 6)) {
        var s1 = s;
        this._heap.AsSpan((int)s1, 6).CopyTo(tmp);
        do {
          this.CopyState(s1, s1 - 6);
          s1 -= 6;
        } while (s1 != stats && tmp[1] > this.Freq(s1 - 6));
        tmp.CopyTo(this._heap.AsSpan((int)s1));
      }
    } while (--i != 0);

    if (this.Freq(s) == 0) {
      var numStats = this.NumStats(this._minContext);
      do {
        ++i;
        s -= 6;
      } while (this.Freq(s) == 0);
      escFreq += i;
      this.NumStats(this._minContext, numStats - i);
      if (this.NumStats(this._minContext) == 1) {
        this._heap.AsSpan((int)stats, 6).CopyTo(tmp);
        do {
          tmp[1] = (byte)(tmp[1] - (tmp[1] >> 1));
          escFreq >>= 1;
        } while (escFreq > 1);
        this.InsertNode(stats, this.UnitsToIndex((numStats + 1) >> 1));
        this._foundState = OneState(this._minContext);
        tmp.CopyTo(this._heap.AsSpan((int)this._foundState));
        return;
      }
      var n0 = (numStats + 1) >> 1;
      var n1 = (this.NumStats(this._minContext) + 1) >> 1;
      if (n0 != n1) this.Stats(this._minContext, this.ShrinkUnits(stats, n0, n1));
    }
    this.SummFreq(this._minContext, sumFreq + escFreq - (escFreq >> 1));
    this._foundState = this.Stats(this._minContext);
  }

  private See MakeEscFreq(uint numMasked, out uint escFreq) {
    var numStats = this.NumStats(this._minContext);
    var nonMasked = numStats - numMasked;
    if (numStats == 256) {
      escFreq = 1;
      return this._dummySee;
    }
    var see = this._see[this._ns2Index[nonMasked - 1] * 16
      + (nonMasked < this.NumStats(this.Suffix(this._minContext)) - numStats ? 1 : 0)
      + 2 * (this.SummFreq(this._minContext) < 11 * numStats ? 1 : 0)
      + 4 * (numMasked > nonMasked ? 1 : 0)
      + (int)this._hiBitsFlag];
    var r = (uint)(see.Summ >> see.Shift);
    see.Summ = (ushort)(see.Summ - r);
    escFreq = r + (r == 0 ? 1u : 0u);
    return see;
  }

  private void NextContext() {
    var c = this.Successor(this._foundState);
    if (this._orderFall == 0 && c > this._text)
      this._minContext = this._maxContext = c;
    else
      this.UpdateModel();
  }

  private void Update1() {
    var s = this._foundState;
    this.Freq(s, this.Freq(s) + 4u);
    this.SummFreq(this._minContext, this.SummFreq(this._minContext) + 4);
    if (this.Freq(s) > this.Freq(s - 6)) {
      this.SwapStates(s, s - 6);
      this._foundState = s -= 6;
      if (this.Freq(s) > MaxFreq) this.Rescale();
    }
    this.NextContext();
  }

  private void Update1First() {
    this._prevSuccess = 2u * this.Freq(this._foundState) > this.SummFreq(this._minContext) ? 1u : 0u;
    this._runLength += (int)this._prevSuccess;
    this.SummFreq(this._minContext, this.SummFreq(this._minContext) + 4);
    this.Freq(this._foundState, this.Freq(this._foundState) + 4u);
    if (this.Freq(this._foundState) > MaxFreq) this.Rescale();
    this.NextContext();
  }

  private void UpdateBin() {
    var f = this.Freq(this._foundState);
    this.Freq(this._foundState, f + (f < 128 ? 1u : 0u));
    this._prevSuccess = 1;
    ++this._runLength;
    this.NextContext();
  }

  private void Update2() {
    this.SummFreq(this._minContext, this.SummFreq(this._minContext) + 4);
    this.Freq(this._foundState, this.Freq(this._foundState) + 4u);
    if (this.Freq(this._foundState) > MaxFreq) this.Rescale();
    this._runLength = this._initRunLength;
    this.UpdateModel();
  }

  private int BinSummIndex() {
    var one = OneState(this._minContext);
    this._hiBitsFlag = this._hb2Flag[this.Symbol(this._foundState)];
    return (this.Freq(one) - 1) * 64
      + (int)(this._prevSuccess
      + this._ns2BsIndex[this.NumStats(this.Suffix(this._minContext)) - 1]
      + this._hiBitsFlag
      + 2u * this._hb2Flag[this.Symbol(one)]
      + (uint)((this._runLength >> 26) & 0x20));
  }

  /// <summary>Decodes one symbol; -1 marks the end of the stream, -2 corrupt data.</summary>
  public int DecodeSymbol(IPpmd7RangeDecoder rc) {
    var mask = this._charMask;
    if (this.NumStats(this._minContext) != 1) {
      var s = this.Stats(this._minContext);
      var count = rc.GetThreshold(this.SummFreq(this._minContext));
      uint hiCnt = this.Freq(s);
      if (count < hiCnt) {
        rc.Decode(0, this.Freq(s));
        this._foundState = s;
        var symbol = this.Symbol(s);
        this.Update1First();
        return symbol;
      }
      this._prevSuccess = 0;
      var i = this.NumStats(this._minContext) - 1;
      do {
        s += 6;
        if ((hiCnt += this.Freq(s)) > count) {
          rc.Decode(hiCnt - this.Freq(s), this.Freq(s));
          this._foundState = s;
          var symbol = this.Symbol(s);
          this.Update1();
          return symbol;
        }
      } while (--i != 0);
      if (count >= this.SummFreq(this._minContext)) return -2;
      this._hiBitsFlag = this._hb2Flag[this.Symbol(this._foundState)];
      rc.Decode(hiCnt, this.SummFreq(this._minContext) - hiCnt);
      Array.Fill(mask, (sbyte)-1);
      mask[this.Symbol(s)] = 0;
      i = this.NumStats(this._minContext) - 1;
      do {
        s -= 6;
        mask[this.Symbol(s)] = 0;
      } while (--i != 0);
    } else {
      var index = this.BinSummIndex();
      var prob = this._binSumm[index];
      if (rc.DecodeBit(prob) == 0) {
        this._binSumm[index] = (ushort)(prob + (1 << IntBits) - ((prob + (1 << (PeriodBits - 2))) >> PeriodBits));
        this._foundState = OneState(this._minContext);
        var symbol = this.Symbol(this._foundState);
        this.UpdateBin();
        return symbol;
      }
      prob = this._binSumm[index] = (ushort)(prob - ((prob + (1 << (PeriodBits - 2))) >> PeriodBits));
      this.InitEsc = ExpEscape[prob >> 10];
      Array.Fill(mask, (sbyte)-1);
      mask[this.Symbol(OneState(this._minContext))] = 0;
      this._prevSuccess = 0;
    }

    var ps = this._ps;
    for (;;) {
      var numMasked = this.NumStats(this._minContext);
      do {
        ++this._orderFall;
        if (this.Suffix(this._minContext) == 0) return -1;
        this._minContext = this.Suffix(this._minContext);
      } while (this.NumStats(this._minContext) == numMasked);

      uint hiCnt = 0;
      var s = this.Stats(this._minContext);
      uint i = 0;
      var num = this.NumStats(this._minContext) - numMasked;
      do {
        int k = mask[this.Symbol(s)];
        hiCnt += (uint)(this.Freq(s) & k);
        ps[i] = s;
        s += 6;
        i = (uint)(i - k);
      } while (i != num);

      var see = this.MakeEscFreq(numMasked, out var freqSum);
      freqSum += hiCnt;
      var count = rc.GetThreshold(freqSum);

      if (count < hiCnt) {
        var k = 0;
        for (hiCnt = 0; (hiCnt += this.Freq(ps[k])) <= count; ++k) { }
        s = ps[k];
        rc.Decode(hiCnt - this.Freq(s), this.Freq(s));
        see.Update();
        this._foundState = s;
        var symbol = this.Symbol(s);
        this.Update2();
        return symbol;
      }
      if (count >= freqSum) return -2;
      rc.Decode(hiCnt, freqSum - hiCnt);
      see.Summ = (ushort)(see.Summ + freqSum);
      do mask[this.Symbol(ps[--i])] = 0; while (i != 0);
    }
  }
}
