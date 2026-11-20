using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace FileFormat.Paq8l;

/// <summary>
/// The coding position every model reads (docs/PAQ8L-FORMAT.md §6): the last bit, the partial
/// byte, the last four bytes, the bit position, the byte count and the history buffer.
/// </summary>
internal sealed class Paq8lHistory {
  private readonly byte[] _buffer;
  private readonly int _mask;

  /// <summary>Memory unit of the level: 65536 &lt;&lt; level.</summary>
  public int Mem { get; }

  /// <summary>Compression level 0–9.</summary>
  public int Level { get; }

  /// <summary>Last coded bit.</summary>
  public int Bit;

  /// <summary>Partial byte with a leading 1 (1–255).</summary>
  public int C0 = 1;

  /// <summary>Last four whole bytes, newest in bits 0–7.</summary>
  public uint C4;

  /// <summary>Bits of the current byte already coded (0–7).</summary>
  public int BitPos;

  /// <summary>Whole bytes coded so far.</summary>
  public int Pos;

  /// <summary>The generator shared by every nibble map.</summary>
  public Paq8lRandom Random { get; } = new();

  public Paq8lHistory(int level) {
    this.Level = level;
    this.Mem = 0x10000 << level;
    this._buffer = new byte[this.Mem * 8];
    this._mask = this._buffer.Length - 1;
  }

  /// <summary>Size of the history buffer.</summary>
  public int Size => this._buffer.Length;

  /// <summary>The byte <paramref name="back"/> positions before the current one (1 = last).</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int Back(int back) => this._buffer[(this.Pos - back) & this._mask];

  /// <summary>The byte at absolute position <paramref name="at"/> (wrapping).</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int At(int at) => this._buffer[at & this._mask];

  /// <summary>Takes in coded bit <paramref name="bit"/>.</summary>
  public void Push(int bit) {
    this.Bit = bit;
    this.C0 += this.C0 + bit;
    if (this.C0 >= 256) {
      this._buffer[this.Pos++ & this._mask] = (byte)this.C0;
      this.C4 = (this.C4 << 8) + (uint)this.C0 - 256;
      this.C0 = 1;
    }
    this.BitPos = (this.BitPos + 1) & 7;
  }
}

/// <summary>Maps a bit-history state to an adaptive 12-bit probability (§4.5).</summary>
internal sealed class Paq8lStateMap {
  private static readonly ushort[] Initial = BuildInitial();
  private readonly ushort[] _table = (ushort[])Initial.Clone();
  private int _last;

  private static ushort[] BuildInitial() {
    var t = new ushort[256];
    for (var s = 0; s < 256; ++s) {
      var zeros = Paq8lTables.Zeros(s);
      var ones = Paq8lTables.Ones(s);
      if (zeros == 0) ones *= 64;
      if (ones == 0) zeros *= 64;
      t[s] = (ushort)(65536 * (ones + 1) / (zeros + ones + 2));
    }
    return t;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int Map(int state, int bit) {
    ref var last = ref this._table[this._last];
    last = (ushort)(last + (((bit << 16) - last + 128) >> 8));
    this._last = state;
    return this._table[state] >> 4;
  }
}

/// <summary>Adaptive probability map: refines a probability within a context (§4.6).</summary>
internal sealed class Paq8lApm {
  private readonly ushort[] _table;
  private int _index;

  public Paq8lApm(int contexts) {
    this._table = new ushort[contexts * 33];
    for (var j = 0; j < 33; ++j) this._table[j] = (ushort)(Paq8lTables.Squash((j - 16) * 128) * 16);
    for (var i = 1; i < contexts; ++i) Array.Copy(this._table, 0, this._table, i * 33, 33);
  }

  public int Refine(int probability, int context, int bit, int rate = 7) {
    var g = (bit << 16) + (bit << rate) - bit - bit;
    ref var a = ref this._table[this._index];
    a = (ushort)(a + ((g - a) >> rate));
    ref var b = ref this._table[this._index + 1];
    b = (ushort)(b + ((g - b) >> rate));
    var s = Paq8lTables.Stretch[probability];
    var w = s & 127;
    this._index = ((s + 2048) >> 7) + context * 33;
    return (this._table[this._index] * (128 - w) + this._table[this._index + 1] * w) >> 11;
  }
}

/// <summary>
/// Gated linear mixer (§4.7): weight sets selected by small contexts; with several selected sets
/// a second mixer combines their outputs.
/// </summary>
internal sealed class Paq8lMixer {
  private readonly int _inputs;
  private readonly short[] _x;
  private readonly short[] _weights;
  private readonly int[] _selected;
  private readonly int[] _output;
  private readonly Paq8lMixer? _final;
  private int _count, _base, _selections;

  public Paq8lMixer(int inputs, int sets, int selections = 1, int weight = 0) {
    this._inputs = (inputs + 7) & -8;
    this._x = new short[this._inputs];
    this._weights = new short[this._inputs * sets];
    Array.Fill(this._weights, (short)weight);
    this._selected = new int[selections];
    this._output = new int[selections];
    Array.Fill(this._output, 2048);
    if (selections > 1) this._final = new Paq8lMixer(selections, 1, 1, 0x7FFF);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Add(int x) => this._x[this._count++] = (short)x;

  public void Select(int context, int range) {
    this._selected[this._selections++] = this._base + context;
    this._base += range;
  }

  /// <summary>Trains every selected set towards <paramref name="bit"/> and clears the inputs.</summary>
  public void Train(int bit) {
    for (var i = 0; i < this._selections; ++i) {
      var err = ((bit << 12) - this._output[i]) * 7;
      TrainSet(this._x.AsSpan(0, this._count), this._weights.AsSpan(this._selected[i] * this._inputs, this._count), err * 2);
    }
    this._count = this._base = this._selections = 0;
  }

  /// <summary>w += ((x·err2 &gt;&gt; 16) + 1) &gt;&gt; 1, saturated to 16 bits; the count is a multiple of 8.</summary>
  private static void TrainSet(ReadOnlySpan<short> x, Span<short> w, int err2) {
    var k = 0;
    if (Avx2.IsSupported) {
      var e = Vector256.Create(err2);
      var one = Vector256.Create(1);
      for (; k + 16 <= w.Length; k += 16) {
        var xs = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(x), (nuint)k);
        var ws = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(w), (nuint)k);
        var lo = Step(Avx2.ConvertToVector256Int32(xs.GetLower()), Avx2.ConvertToVector256Int32(ws.GetLower()), e, one);
        var hi = Step(Avx2.ConvertToVector256Int32(xs.GetUpper()), Avx2.ConvertToVector256Int32(ws.GetUpper()), e, one);
        // PackSignedSaturate interleaves 128-bit lanes; restore element order
        var packed = Avx2.Permute4x64(Avx2.PackSignedSaturate(lo, hi).AsInt64(), 0b11_01_10_00).AsInt16();
        packed.StoreUnsafe(ref MemoryMarshal.GetReference(w), (nuint)k);
      }
    }
    for (; k < w.Length; ++k) {
      var v = w[k] + ((((x[k] * err2) >> 16) + 1) >> 1);
      w[k] = (short)(v < -32768 ? -32768 : v > 32767 ? 32767 : v);
    }

    static Vector256<int> Step(Vector256<int> x, Vector256<int> w, Vector256<int> e, Vector256<int> one) =>
      Avx2.Add(w, Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.ShiftRightArithmetic(Avx2.MultiplyLow(x, e), 16), one), 1));
  }

  public int Predict(int bit) {
    while ((this._count & 7) != 0) this._x[this._count++] = 0;
    if (this._final is { } final) {
      final.Train(bit);
      for (var i = 0; i < this._selections; ++i) {
        this._output[i] = Paq8lTables.Squash(this.Dot(this._selected[i]) >> 5);
        final.Add(Paq8lTables.Stretch[this._output[i]]);
      }
      final.Select(0, 1);
      return final.Predict(bit);
    }
    return this._output[0] = Paq8lTables.Squash(this.Dot(0) >> 8);
  }

  /// <summary>Sum over input pairs of (x·w + x'·w') &gt;&gt; 8; the count is a multiple of 8.</summary>
  private int Dot(int set) {
    ReadOnlySpan<short> w = this._weights.AsSpan(set * this._inputs, this._count);
    ReadOnlySpan<short> x = this._x;
    var k = 0;
    var sum = 0;
    if (Avx2.IsSupported) {
      var acc = Vector256<int>.Zero;
      for (; k + 16 <= w.Length; k += 16) {
        var xs = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(x), (nuint)k);
        var ws = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(w), (nuint)k);
        acc = Avx2.Add(acc, Avx2.ShiftRightArithmetic(Avx2.MultiplyAddAdjacent(xs, ws), 8));
      }
      sum = Vector256.Sum(acc);
    }
    for (; k < w.Length; k += 2)
      sum += (x[k] * w[k] + x[k + 1] * w[k + 1]) >> 8;
    return sum;
  }
}

/// <summary>
/// Hash table of B-byte elements found by a 16-bit checksum, with move-to-front over eight
/// probes (§5.1). Lookups return the byte offset of the element's byte 1.
/// </summary>
internal sealed class Paq8lBucketTable {
  private readonly int _width;
  private readonly uint _mask;
  private readonly byte[] _scratch;

  public byte[] Bytes { get; }

  public Paq8lBucketTable(int elements, int width) {
    this._width = width;
    this._mask = (uint)elements - 1;
    this.Bytes = new byte[elements * width];
    this._scratch = new byte[width];
  }

  public int Find(uint context) {
    var chk = (ushort)((context >> 16) ^ context);
    var first = (int)((context * 8) & this._mask);
    var t = this.Bytes;
    var w = this._width;
    int j;
    for (j = 0; j < 8; ++j) {
      var at = (first + j) * w;
      if (t[at + 2] == 0) BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(at), chk);
      if (BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(at)) == chk) break;
    }
    if (j == 0) return first * w + 1;
    var moved = this._scratch;
    if (j == 8) {
      --j;
      Array.Clear(moved);
      BinaryPrimitives.WriteUInt16LittleEndian(moved, chk);
      if (t[(first + j) * w + 2] > t[(first + j - 1) * w + 2]) --j;
    } else
      Array.Copy(t, (first + j) * w, moved, 0, w);
    Array.Copy(t, first * w, t, (first + 1) * w, j * w);
    Array.Copy(moved, 0, t, first * w, w);
    return first * w + 1;
  }
}

/// <summary>Predicts the byte last seen in a context and how often it repeated (§5.2).</summary>
internal sealed class Paq8lRunMap {
  private readonly Paq8lBucketTable _table;
  private readonly Paq8lHistory _h;
  private int _at;

  public Paq8lRunMap(Paq8lHistory h, int bytes) {
    this._h = h;
    this._table = new Paq8lBucketTable(bytes / 4, 4);
    this._at = this._table.Find(0) + 1;
  }

  public void Set(uint context) {
    var t = this._table.Bytes;
    var last = (byte)this._h.Back(1);
    if (t[this._at] == 0 || t[this._at + 1] != last) {
      t[this._at] = 1;
      t[this._at + 1] = last;
    } else if (t[this._at] < 255)
      ++t[this._at];
    this._at = this._table.Find(context) + 1;
  }

  public void Mix(Paq8lMixer m) {
    var t = this._table.Bytes;
    var h = this._h;
    int value = t[this._at + 1];
    if ((value + 256) >> (8 - h.BitPos) == h.C0)
      m.Add((((value >> (7 - h.BitPos)) & 1) * 2 - 1) * Paq8lTables.Log[t[this._at] + 1] * 8);
    else
      m.Add(0);
  }
}

/// <summary>Directly indexed stationary map over a small context and the partial byte (§5.3).</summary>
internal sealed class Paq8lDirectMap {
  private readonly ushort[] _table;
  private readonly Paq8lHistory _h;
  private int _row, _at;

  public Paq8lDirectMap(Paq8lHistory h, int bytes) {
    this._h = h;
    this._table = new ushort[bytes / 2];
    Array.Fill(this._table, (ushort)32768);
  }

  public void Set(uint context) => this._row = (int)(context * 256 & (uint)(this._table.Length - 256));

  public void Mix(Paq8lMixer m, int rate = 7) {
    ref var p = ref this._table[this._at];
    p = (ushort)(p + (((this._h.Bit << 16) - p + (1 << (rate - 1))) >> rate));
    this._at = this._row + this._h.C0;
    m.Add(Paq8lTables.Stretch[this._table[this._at] >> 4]);
  }
}

/// <summary>
/// The main context map (§5.4): 64-byte slots of seven checksummed bit-history groups, looked up
/// at bits 0, 2 and 5 of each byte, with a run predictor kept beside the byte-boundary histories.
/// </summary>
internal sealed class Paq8lNibbleMap {
  private const int SlotSize = 64;
  private const int HistoryStart = 15;
  private readonly Paq8lHistory _h;
  private readonly byte[] _t;
  private readonly int _slotMask;
  private readonly int[] _ptr, _base, _run;
  private readonly uint[] _context;
  private readonly Paq8lStateMap[] _maps;
  private int _set;

  public Paq8lNibbleMap(Paq8lHistory h, int bytes, int contexts) {
    this._h = h;
    this._t = new byte[bytes];
    this._slotMask = bytes / SlotSize - 1;
    this._ptr = new int[contexts];
    this._base = new int[contexts];
    this._run = new int[contexts];
    this._context = new uint[contexts];
    this._maps = new Paq8lStateMap[contexts];
    for (var i = 0; i < contexts; ++i) {
      this._maps[i] = new();
      this._ptr[i] = this._base[i] = HistoryStart;
      this._run[i] = HistoryStart + 3;
    }
  }

  /// <summary>Sets the next context of this byte.</summary>
  public void Set(uint context) {
    var k = (uint)this._set++;
    var x = unchecked(context * 987654323u + k);
    x = x << 16 | x >> 16;
    this._context[k] = unchecked(x * 123456791u + k);
  }

  /// <summary>Finds or claims the history for checksum <paramref name="check"/> in slot <paramref name="slot"/>; returns its offset.</summary>
  private int Lookup(int slot, ushort check) {
    var t = this._t;
    var s = slot * SlotSize;
    var recent = t[s + 14];
    var low = recent & 15;
    if (BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(s + low * 2)) == check)
      return s + HistoryStart + low * 7;
    int best = 0xFFFF, bi = 0;
    for (var i = 0; i < 7; ++i) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(s + i * 2)) == check) {
        t[s + 14] = (byte)(recent << 4 | i);
        return s + HistoryStart + i * 7;
      }
      int priority = t[s + HistoryStart + i * 7];
      if (low != i && recent >> 4 != i && priority < best) {
        best = priority;
        bi = i;
      }
    }
    t[s + 14] = (byte)(0xF0 | bi);
    BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(s + bi * 2), check);
    var at = s + HistoryStart + bi * 7;
    Array.Clear(t, at, 7);
    return at;
  }

  /// <summary>Updates with the last bit and adds this bit's inputs; returns how many contexts were found.</summary>
  public int Mix(Paq8lMixer m) {
    var h = this._h;
    var t = this._t;
    var bit = h.Bit;
    var bpos = h.BitPos;
    var c0 = h.C0;
    var found = 0;
    for (var i = 0; i < this._set; ++i) {
      // 1. update the history the last bit belonged to
      if (this._ptr[i] >= 0) {
        var ns = Paq8lTables.Next(t[this._ptr[i]], bit);
        if (ns >= 204 && (h.Random.Next() << ((452 - ns) >> 3)) != 0) ns -= 4;
        t[this._ptr[i]] = (byte)ns;
      }

      // 2. move to this bit's history
      if (bpos > 1 && t[this._run[i]] == 0)
        this._ptr[i] = -1;
      else if (bpos is 1 or 3 or 6)
        this._ptr[i] = this._base[i] + 1 + (c0 & 1);
      else if (bpos is 4 or 7)
        this._ptr[i] = this._base[i] + 3 + (c0 & 3);
      else {
        var cx = this._context[i];
        var check = (ushort)(cx >> 16);
        this._base[i] = this._ptr[i] = this.Lookup((int)((cx + (uint)c0) & (uint)this._slotMask), check);
        if (bpos == 0) {
          var b = this._base[i];
          if (t[b + 3] == 2) {
            var c = t[b + 4] + 256;
            var p = this.Lookup((int)((cx + (uint)(c >> 6)) & (uint)this._slotMask), check);
            t[p] = (byte)(1 + ((c >> 5) & 1));
            t[p + 1 + ((c >> 5) & 1)] = (byte)(1 + ((c >> 4) & 1));
            t[p + 3 + ((c >> 4) & 3)] = (byte)(1 + ((c >> 3) & 1));
            p = this.Lookup((int)((cx + (uint)(c >> 3)) & (uint)this._slotMask), check);
            t[p] = (byte)(1 + ((c >> 2) & 1));
            t[p + 1 + ((c >> 2) & 1)] = (byte)(1 + ((c >> 1) & 1));
            t[p + 3 + ((c >> 1) & 3)] = (byte)(1 + (c & 1));
            t[b + 6] = 0;
          }
          var run = this._run[i];
          var last = (byte)h.Back(1);
          if (t[run] == 0) {
            t[run] = 2;
            t[run + 1] = last;
          } else if (t[run + 1] != last) {
            t[run] = 1;
            t[run + 1] = last;
          } else if (t[run] < 254)
            t[run] += 2;
          else if (t[run] == 255)
            t[run] = 128;
          this._run[i] = b + 3;
        }
      }

      // 3. the byte last seen in this context
      var r = this._run[i];
      int rc = t[r];
      int value = t[r + 1];
      if ((value + 256) >> (8 - bpos) == c0)
        m.Add((((value >> (7 - bpos)) & 1) * 2 - 1) * (Paq8lTables.Log[rc + 1] << (2 + (~rc & 1))));
      else
        m.Add(0);

      // 4. the bit history
      var state = this._ptr[i] >= 0 ? t[this._ptr[i]] : 0;
      found += AddState(m, state, this._maps[i], bit);
    }
    if (bpos == 7) this._set = 0;
    return found;
  }

  /// <summary>The five inputs a bit-history state gives (§5.5); 1 if the state is not empty.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int AddState(Paq8lMixer m, int state, Paq8lStateMap map, int bit) {
    var p1 = map.Map(state, bit);
    var z0 = Paq8lTables.Zeros(state) == 0 ? -1 : 0;
    var z1 = Paq8lTables.Ones(state) == 0 ? -1 : 0;
    var st = Paq8lTables.Stretch[p1] >> 2;
    m.Add(st);
    p1 >>= 4;
    var p0 = 255 - p1;
    m.Add(p1 - p0);
    m.Add(st * (z1 - z0));
    m.Add((p1 & z0) - (p0 & z1));
    m.Add((p1 & z1) - (p0 & z0));
    return state > 0 ? 1 : 0;
  }
}
