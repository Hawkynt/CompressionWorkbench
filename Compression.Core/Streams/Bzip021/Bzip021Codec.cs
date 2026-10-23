using Compression.Core.Transforms;
using Compression.Registry;

namespace Compression.Core.Streams.Bzip021;

/// <summary>
/// bzip 0.21 (<c>.bz</c>, magic <c>BZ0</c>): Julian Seward's arithmetic-coded predecessor
/// of bzip2 — run-length pre-pass, "spotting", Burrows-Wheeler transform, move-to-front with
/// RUNA/RUNB zero runs, and a structured adaptive model driving a 26-bit range coder.
/// </summary>
/// <remarks>
/// Implemented from <c>docs/BZIP021-ON-DISK.md</c>, which was written from the GPL-2.0
/// reference program (sourcing rung 2); no code was carried across. Verified against
/// bzip 0.21 itself in both directions.
/// </remarks>
public static class Bzip021Codec {

  private const int CodeBits = 26;
  private const uint Half = 1u << (CodeBits - 1);
  private const uint Quarter = 1u << (CodeBits - 2);
  private const int SpotBase = 8000;
  private const byte EndMarker = 42;

  // ── bit I/O ──────────────────────────────────────────────────────────

  private sealed class BitWriter(int capacity) {
    private readonly List<byte> _bytes = new(capacity);
    private int _acc, _count;

    public void Bit(int b) {
      this._acc = (this._acc << 1) | (b & 1);
      if (++this._count == 8) {
        this._bytes.Add((byte)this._acc);
        this._acc = this._count = 0;
      }
    }

    public void Byte(byte b) {
      for (var i = 7; i >= 0; --i) this.Bit(b >> i);
    }

    public byte[] Finish() {
      // A partial final byte is padded with zero bits.
      while (this._count != 0) this.Bit(0);
      return [.. this._bytes];
    }
  }

  private ref struct BitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private long _bit;

    public int Bit() {
      if (this._bit >= (long)this._data.Length * 8) throw new InvalidDataException("bzip: unexpected end of compressed data.");
      var b = (this._data[(int)(this._bit >> 3)] >> (7 - (int)(this._bit & 7))) & 1;
      ++this._bit;
      return b;
    }

    public byte Byte() {
      var v = 0;
      for (var i = 0; i < 8; ++i) v = (v << 1) | this.Bit();
      return (byte)v;
    }
  }

  // ── models ───────────────────────────────────────────────────────────

  private sealed class Model {
    private readonly uint[] _freq;
    private readonly uint _inc, _limit;
    public uint Total { get; private set; }
    public int Count => this._freq.Length;

    public Model(int symbols, uint inc, uint limit) {
      this._freq = new uint[symbols];
      this._inc = inc;
      this._limit = limit;
      Array.Fill(this._freq, inc == 0 ? 1u : inc);
      this.Total = (uint)symbols * (inc == 0 ? 1u : inc);
    }

    /// <summary>Cumulative frequency below symbol index <paramref name="s"/> (0-based).</summary>
    public uint Low(int s) {
      uint lo = 0;
      for (var i = 0; i < s; ++i) lo += this._freq[i];
      return lo;
    }

    public uint Freq(int s) => this._freq[s];

    /// <summary>Finds the symbol whose cumulative range contains <paramref name="target"/>.</summary>
    public int Find(uint target, out uint lo) {
      lo = 0;
      for (var s = 0; s < this._freq.Length; ++s) {
        if (lo + this._freq[s] > target) return s;
        lo += this._freq[s];
      }
      throw new InvalidDataException("bzip: corrupt arithmetic-coded data.");
    }

    public void Update(int s) {
      if (this._inc == 0) return;
      this._freq[s] += this._inc;
      this.Total += this._inc;
      if (this.Total <= this._limit) return;
      this.Total = 0;
      for (var i = 0; i < this._freq.Length; ++i) this.Total += this._freq[i] = (this._freq[i] + 1) >> 1;
    }
  }

  /// <summary>The per-block structured model: basis class plus seven range sub-models.</summary>
  private sealed class BlockModels {
    public readonly Model Basis = new(11, 12, 1000);
    public readonly Model[] Ranges = [
      new(2, 4, 1000), new(4, 3, 1000), new(8, 3, 1000), new(16, 3, 1000),
      new(32, 3, 1000), new(64, 2, 1000), new(128, 1, 1000),
    ];
  }

  private const int SymRunA = 0, SymRunB = 1, SymOne = 2, SymEob = 10; // basis indices (symbol − 1)

  // ── encoder ──────────────────────────────────────────────────────────

  private sealed class Encoder(BitWriter w) {
    private uint _low;
    private uint _range = Half;
    private long _pending;

    private void Emit(int bit) {
      w.Bit(bit);
      for (; this._pending > 0; --this._pending) w.Bit(1 - bit);
    }

    public void Code(Model m, int s) {
      var r = this._range / m.Total;
      var lo = m.Low(s);
      var hi = lo + m.Freq(s);
      this._low += r * lo;
      this._range = hi < m.Total ? r * (hi - lo) : this._range - r * lo;
      while (this._range <= Quarter) {
        if (this._low + this._range <= Half)
          this.Emit(0);
        else if (this._low >= Half) {
          this.Emit(1);
          this._low -= Half;
        } else {
          ++this._pending;
          this._low -= Quarter;
        }
        this._low <<= 1;
        this._range <<= 1;
      }
      m.Update(s);
    }

    public void Finish() {
      for (var i = CodeBits - 1; i >= 0; --i) this.Emit((int)(this._low >> i) & 1);
    }
  }

  private ref struct Decoder {
    private BitReader _in;
    private uint _code;
    private uint _range;

    public Decoder(BitReader input) {
      this._in = input;
      this._range = Half;
      this._code = 0;
      for (var i = 0; i < CodeBits; ++i) this._code = (this._code << 1) | (uint)this._in.Bit();
    }

    public int Decode(Model m) {
      var r = this._range / m.Total;
      if (r == 0) throw new InvalidDataException("bzip: corrupt arithmetic-coded data.");
      var target = Math.Min(m.Total - 1, this._code / r);
      var s = m.Find(target, out var lo);
      var hi = lo + m.Freq(s);
      this._code -= r * lo;
      this._range = hi < m.Total ? r * (hi - lo) : this._range - r * lo;
      while (this._range <= Quarter) {
        this._range <<= 1;
        this._code = (this._code << 1) | (uint)this._in.Bit();
      }
      m.Update(s);
      return s;
    }
  }

  // ── shared helpers ───────────────────────────────────────────────────

  private static readonly uint[] CrcTable = BuildCrc();

  private static uint[] BuildCrc() {
    var t = new uint[256];
    for (uint i = 0; i < 256; ++i) {
      var c = i << 24;
      for (var k = 0; k < 8; ++k) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
      t[i] = c;
    }
    return t;
  }

  private static uint CrcUpdate(uint crc, byte b) => (crc << 8) ^ CrcTable[(crc >> 24) ^ b];

  /// <summary>Applies (+1) or removes (−1) the spotting perturbation in place.</summary>
  private static void Spot(Span<byte> block, int last, int direction) {
    var pos = SpotBase;
    var delta = 1;
    while (pos < last) {
      block[pos] = (byte)(block[pos] + direction);
      delta = delta switch { 3 => 1, 1 => 4, 4 => 5, 5 => 9, 9 => 2, 2 => 6, 6 => 7, 8 => 8, 7 => 3, _ => 1 };
      pos += SpotBase + 17 * (delta - 5);
    }
  }

  private static void WriteInt32(Encoder e, Model bytes, int value) {
    for (var shift = 24; shift >= 0; shift -= 8) e.Code(bytes, (int)(((uint)value >> shift) & 0xFF));
  }

  private static uint ReadUInt32(ref Decoder d, Model bytes) {
    uint v = 0;
    for (var i = 0; i < 4; ++i) v = (v << 8) | (uint)d.Decode(bytes);
    return v;
  }

  // ── encode ───────────────────────────────────────────────────────────

  /// <summary>Encodes <paramref name="data"/> as a bzip 0.21 file with block size 9 (900 k).</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int blockSize100k = 9) {
    ArgumentOutOfRangeException.ThrowIfLessThan(blockSize100k, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize100k, 9);
    var w = new BitWriter(data.Length / 3 + 64);
    foreach (var b in "BZ0"u8) w.Byte(b);
    w.Byte((byte)('0' + blockSize100k));
    var enc = new Encoder(w);
    var bytes = new Model(256, 0, 256);

    var crc = 0xFFFFFFFFu;
    foreach (var b in data) crc = CrcUpdate(crc, b);

    var allowable = 100000 * blockSize100k - 20;
    var block = new byte[allowable + 6];
    var pos = 0;
    bool lastBlock;
    do {
      // Load: run-length code until the block is full or the input ends.
      var n = 0;
      lastBlock = false;
      while (true) {
        if (n > allowable) break;
        if (pos >= data.Length) {
          block[n++] = EndMarker;
          lastBlock = true;
          break;
        }
        var ch = data[pos];
        var run = 1;
        while (run < 255 && pos + run < data.Length && data[pos + run] == ch) ++run;
        pos += run;
        var literal = Math.Min(run, 4);
        for (var k = 0; k < literal; ++k) block[n++] = ch;
        if (run >= 4) block[n++] = (byte)(run - 4);
      }
      var last = n - 1;
      Spot(block, last, +1);
      var (bwt, origPtr) = BurrowsWheelerTransform.Forward(block.AsSpan(0, n));
      WriteInt32(enc, bytes, lastBlock ? -(origPtr + 1) : origPtr + 1);
      EncodeMtf(enc, bwt);
    } while (!lastBlock);

    WriteInt32(enc, bytes, (int)~crc);
    enc.Finish();
    return w.Finish();
  }

  private static void EncodeMtf(Encoder enc, ReadOnlySpan<byte> lastColumn) {
    var m = new BlockModels();
    Span<byte> list = stackalloc byte[256];
    for (var i = 0; i < 256; ++i) list[i] = (byte)i;
    var zeros = 0;
    foreach (var c in lastColumn) {
      var j = list.IndexOf(c);
      if (j == 0) {
        ++zeros;
        continue;
      }
      SendZeros(enc, m, zeros);
      zeros = 0;
      list[..j].CopyTo(list[1..]);
      list[0] = c;
      SendValue(enc, m, j);
    }
    SendZeros(enc, m, zeros);
    enc.Code(m.Basis, SymEob);
  }

  /// <summary>A zero run in bijective base 2, most significant digit first (RUNA = 2, RUNB = 1).</summary>
  private static void SendZeros(Encoder enc, BlockModels m, int count) {
    if (count == 0) return;
    Span<byte> digits = stackalloc byte[32];
    var n = 0;
    while (count > 0) {
      var d = (count & 1) == 0 ? 2 : 1;
      digits[n++] = (byte)d;
      count = (count - d) >> 1;
    }
    while (n-- > 0) enc.Code(m.Basis, digits[n] == 2 ? SymRunA : SymRunB);
  }

  private static void SendValue(Encoder enc, BlockModels m, int v) {
    if (v == 1) {
      enc.Code(m.Basis, SymOne);
      return;
    }
    var cls = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v); // 1 for 2–3 … 7 for 128–255
    enc.Code(m.Basis, SymOne + cls);
    enc.Code(m.Ranges[cls - 1], v - (1 << cls));
  }

  // ── decode ───────────────────────────────────────────────────────────

  /// <summary>Decodes a complete bzip 0.21 file.</summary>
  /// <exception cref="InvalidDataException">Bad magic, corrupt data, or a CRC mismatch.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    var input = new BitReader(data);
    if (input.Byte() != 'B' || input.Byte() != 'Z' || input.Byte() != '0')
      throw new InvalidDataException("bzip: missing BZ0 magic.");
    var level = input.Byte() - '0';
    if (level is < 1 or > 9) throw new InvalidDataException("bzip: invalid block size digit.");
    var limit = 100000 * level;

    var dec = new Decoder(input);
    var bytes = new Model(256, 0, 256);
    using var output = new MemoryStream();
    var crc = 0xFFFFFFFFu;
    var ll = new byte[limit];
    var block = new byte[limit];
    var counts = new int[256];
    var next = new int[limit];
    Span<byte> list = stackalloc byte[256];

    bool lastBlock;
    do {
      var signed = (int)ReadUInt32(ref dec, bytes);
      lastBlock = signed < 0;
      var origPtr = (lastBlock ? -signed : signed) - 1;

      // MTF + zero runs.
      var m = new BlockModels();
      for (var i = 0; i < 256; ++i) list[i] = (byte)i;
      var n = 0;
      var sym = dec.Decode(m.Basis);
      while (sym != SymEob) {
        if (sym is SymRunA or SymRunB) {
          var run = 0;
          do {
            run = 2 * run + (sym == SymRunA ? 2 : 1);
            if (run > limit) throw new InvalidDataException("bzip: block overrun.");
            sym = dec.Decode(m.Basis);
          } while (sym is SymRunA or SymRunB);
          if (n + run > limit) throw new InvalidDataException("bzip: block overrun.");
          ll.AsSpan(n, run).Fill(list[0]);
          n += run;
          continue;
        }
        int v;
        if (sym == SymOne) v = 1;
        else {
          var cls = sym - SymOne;
          v = (1 << cls) + dec.Decode(m.Ranges[cls - 1]);
        }
        if (n >= limit) throw new InvalidDataException("bzip: block overrun.");
        var c = list[v];
        list[..v].CopyTo(list[1..]);
        list[0] = c;
        ll[n++] = c;
        sym = dec.Decode(m.Basis);
      }
      if (n == 0 || origPtr < 0 || origPtr >= n) throw new InvalidDataException("bzip: invalid block origin.");

      // Inverse BWT.
      Array.Clear(counts);
      for (var i = 0; i < n; ++i) next[i] = counts[ll[i]]++;
      var sum = 0;
      for (var c = 0; c < 256; ++c) { var t = counts[c]; counts[c] = sum; sum += t; }
      var p = origPtr;
      for (var j = n - 1; j >= 0; --j) {
        var c = ll[p];
        block[j] = c;
        p = next[p] + counts[c];
      }
      var last = n - 1;
      Spot(block, last, -1);

      // Undo the run-length pre-pass.
      var end = lastBlock ? last - 1 : last;
      var run4 = 0;
      var prev = -1;
      for (var i = 0; i <= end;) {
        var ch = block[i++];
        output.WriteByte(ch);
        crc = CrcUpdate(crc, ch);
        if (ch != prev) {
          run4 = 1;
          prev = ch;
          continue;
        }
        if (++run4 < 4) continue;
        if (i > last) throw new InvalidDataException("bzip: run count missing.");
        var extra = block[i++];
        for (var k = 0; k < extra; ++k) {
          output.WriteByte(ch);
          crc = CrcUpdate(crc, ch);
        }
        run4 = 0;
      }
      if (lastBlock && block[last] != EndMarker) throw new InvalidDataException("bzip: missing end-of-data marker.");
    } while (!lastBlock);

    var stored = ReadUInt32(ref dec, bytes);
    if (stored != ~crc) throw new InvalidDataException($"bzip: CRC mismatch (stored 0x{stored:X8}, computed 0x{~crc:X8}).");
    return output.ToArray();
  }
}

/// <summary>bzip 0.21 as a building block.</summary>
public sealed class Bzip021BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Bzip021";
  /// <inheritdoc/>
  public string DisplayName => "bzip 0.21";
  /// <inheritdoc/>
  public string Description => "bzip 0.21: RLE + BWT + MTF with a structured adaptive model and a 26-bit arithmetic coder";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => Bzip021Codec.Encode(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => Bzip021Codec.Decode(data);
}
