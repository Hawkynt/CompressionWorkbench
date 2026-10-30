namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Encoder for the RAR 5.0 compression format (algorithm version 0), the counterpart of
/// <see cref="Rar5Unpacker"/>: LZ77 with hash-chain match finding, the four repeat distances
/// and the repeat-last-match symbol, coded in blocks that each carry fresh Huffman tables.
/// </summary>
/// <remarks>
/// <para>
/// The bitstream is the one <see cref="Rar5Unpacker"/> decodes (libarchive's BSD-2
/// RAR 5 reader, converted); nothing here comes from RAR's own compressor, whose algorithm is
/// proprietary. Filters are not emitted. unrar and 7-Zip accept what it writes.
/// </para>
/// <para>
/// Successive calls continue one solid stream: the window, the repeat distances and the last
/// length carry over. Input is streamed through a sliding buffer, so memory is bounded by the
/// match window (the dictionary, at most 64 MB here) whatever the file size.
/// </para>
/// </remarks>
public sealed class Rar5Encoder {
  private const int NC = 306, DC = 64, LDC = 16, RC = 44, BC = 20;
  private const int MinMatch = 3;
  private const int MaxLengthCoded = 4097;
  private const int HashBits = 17;
  private const int ChunkSize = 1 << 20;
  private const int BlockTokens = 1 << 16;

  private readonly int _windowBits;
  private readonly int _window;
  private readonly int _maxChain;
  private readonly bool _lazy;

  private byte[] _buf;
  private int _bufLen;          // valid bytes in _buf
  private long _bufBase;        // stream offset of _buf[0]
  private readonly int[] _head = new int[1 << HashBits];
  private readonly int[] _prev;
  private int _inserted;        // buffer index up to which hash positions are inserted
  private readonly int[] _rep = new int[4];
  private int _lastLen;

  private readonly List<Token> _tokens = new(BlockTokens + 16);

  private readonly record struct Token(int Kind, int Length, int Distance, byte Literal);
  // Kind: 0 literal, 1 match (dist new), 2..5 repeat distance 0..3, 6 repeat last

  /// <summary>Creates an encoder.</summary>
  /// <param name="dictionarySize">Largest distance used, rounded up to a power of two (128 KB - 64 MB).</param>
  /// <param name="level">1 (fastest) to 5 (best).</param>
  public Rar5Encoder(int dictionarySize = Rar5Constants.MinDictionarySize, int level = 3) {
    ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, Rar5Constants.MinDictionarySize);
    var bits = 17;
    while (bits < 26 && (1 << bits) < dictionarySize) ++bits;
    this._windowBits = bits;
    this._window = 1 << bits;
    this._prev = new int[this._window];
    this._buf = new byte[this._window + ChunkSize];
    Array.Fill(this._head, -1);
    (this._maxChain, this._lazy) = Math.Clamp(level, 1, 5) switch {
      1 => (4, false),
      2 => (12, false),
      3 => (32, true),
      4 => (96, true),
      _ => (256, true),
    };
  }

  /// <summary>The dictionary size this encoder's matches stay within.</summary>
  public int DictionarySize => this._window;

  /// <summary>Compresses one file held in memory; call again for the next file of a solid stream.</summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return [];
    using var input = new MemoryStream(data.ToArray(), writable: false);
    using var output = new MemoryStream();
    this.Compress(input, data.Length, output);
    return output.ToArray();
  }

  /// <summary>
  /// Compresses <paramref name="length"/> bytes of <paramref name="input"/> into
  /// <paramref name="output"/> as one file's packed data.
  /// </summary>
  public void Compress(Stream input, long length, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (length <= 0) return;
    var pos = this._bufLen;    // start of this file's data in the buffer
    var left = length;
    var wroteBlock = false;
    while (true) {
      // Fill.
      while (left > 0 && this._bufLen < this._buf.Length) {
        var n = input.Read(this._buf, this._bufLen, (int)Math.Min(this._buf.Length - this._bufLen, left));
        if (n <= 0) throw new EndOfStreamException("Input ended before the declared length.");
        this._bufLen += n;
        left -= n;
      }
      var final = left == 0;
      var parseEnd = final ? this._bufLen : this._bufLen - MaxLengthCoded - 8;
      pos = this.Parse(pos, parseEnd, final, output, ref wroteBlock);
      if (final) break;
      this.Slide(ref pos);
    }
    this.FlushBlock(output, last: true, ref wroteBlock);
  }

  private void Slide(ref int pos) {
    // Keep the last window of history before pos.
    var keepFrom = Math.Max(0, pos - this._window);
    if (keepFrom == 0) {
      if (this._bufLen < this._buf.Length) return;
      // Buffer full yet nothing to drop (window larger than the data so far): grow.
      Array.Resize(ref this._buf, this._buf.Length + ChunkSize);
      return;
    }
    var keep = this._bufLen - keepFrom;
    Buffer.BlockCopy(this._buf, keepFrom, this._buf, 0, keep);
    this._bufLen = keep;
    this._bufBase += keepFrom;
    pos -= keepFrom;
    this._inserted -= keepFrom;
    for (var i = 0; i < this._head.Length; ++i)
      this._head[i] = this._head[i] >= keepFrom ? this._head[i] - keepFrom : -1;
    // _prev is indexed by absolute position masked to the window, so it survives the move;
    // its stored positions are buffer indices and move with it.
    for (var i = 0; i < this._prev.Length; ++i)
      this._prev[i] = this._prev[i] >= keepFrom ? this._prev[i] - keepFrom : -1;
  }

  private int Hash(int p) => (int)(((uint)(this._buf[p] | (this._buf[p + 1] << 8) | (this._buf[p + 2] << 16)) * 2654435761u) >> (32 - HashBits));

  private void InsertUpTo(int p, int limit) {
    var stop = Math.Min(p, limit - MinMatch + 1);
    for (var i = Math.Max(this._inserted, 0); i < stop; ++i) {
      var h = this.Hash(i);
      this._prev[(this._bufBase + i) & (this._window - 1)] = this._head[h];
      this._head[h] = i;
    }
    if (stop > this._inserted) this._inserted = stop;
  }

  private (int Length, int Distance) FindMatch(int p, int end) {
    if (p + MinMatch > end) return (0, 0);
    this.InsertUpTo(p, end);
    var maxLen = Math.Min(MaxLengthCoded, end - p);
    var best = 0;
    var bestDist = 0;
    var cand = this._head[this.Hash(p)];
    var chain = this._maxChain;
    var buf = this._buf;
    while (cand >= 0 && chain-- > 0) {
      var dist = p - cand;
      if (dist <= 0 || dist > this._window) break;
      if (buf[cand + best] == buf[p + best] && buf[cand] == buf[p]) {
        var l = 0;
        while (l < maxLen && buf[cand + l] == buf[p + l]) ++l;
        if (l > best) {
          best = l;
          bestDist = dist;
          if (l >= maxLen) break;
        }
      }
      var next = this._prev[(this._bufBase + cand) & (this._window - 1)];
      if (next >= cand) break;
      cand = next;
    }
    return (best, bestDist);
  }

  private int RepLength(int p, int end, int dist) {
    if (dist <= 0 || dist > p + (int)Math.Min(this._bufBase, int.MaxValue) || dist > p) return 0;
    var maxLen = Math.Min(MaxLengthCoded, end - p);
    var l = 0;
    var buf = this._buf;
    while (l < maxLen && buf[p - dist + l] == buf[p + l]) ++l;
    return l;
  }

  private static int Bonus(int dist) => dist > 0x40000 ? 3 : dist > 0x2000 ? 2 : dist > 0x100 ? 1 : 0;

  private int Parse(int p, int end, bool final, Stream output, ref bool wroteBlock) {
    var limit = final ? this._bufLen : end + MaxLengthCoded + 8;
    while (p < end) {
      // Repeat distances first: they cost far fewer bits.
      int repIdx = -1, repLen = 0;
      for (var k = 0; k < 4; ++k) {
        var l = this.RepLength(p, limit, this._rep[k]);
        if (l > repLen) { repLen = l; repIdx = k; }
      }
      var (len, dist) = this.FindMatch(p, limit);
      if (len > MaxLengthCoded + Bonus(dist)) len = MaxLengthCoded + Bonus(dist);
      if (len < MinMatch + Bonus(dist)) len = 0;

      if (this._lazy && len > 0 && len < 64 && p + 1 < end) {
        var (len2, dist2) = this.FindMatch(p + 1, limit);
        if (len2 > len + 1 && len2 >= MinMatch + Bonus(dist2)) {
          this.Emit(new Token(0, 1, 0, this._buf[p]), output, ref wroteBlock);
          ++p;
          continue;
        }
      }

      if (repLen >= 2 && repLen + 2 >= len) {
        repLen = Math.Min(repLen, MaxLengthCoded);
        var d = this._rep[repIdx];
        if (repIdx == 0 && repLen == this._lastLen)
          this.Emit(new Token(6, repLen, d, 0), output, ref wroteBlock);
        else {
          this.Emit(new Token(2 + repIdx, repLen, d, 0), output, ref wroteBlock);
          for (var i = repIdx; i > 0; --i) this._rep[i] = this._rep[i - 1];
          this._rep[0] = d;
          this._lastLen = repLen;
        }
        p += repLen;
        continue;
      }
      if (len > 0) {
        this.Emit(new Token(1, len, dist, 0), output, ref wroteBlock);
        this._rep[3] = this._rep[2];
        this._rep[2] = this._rep[1];
        this._rep[1] = this._rep[0];
        this._rep[0] = dist;
        this._lastLen = len;
        p += len;
        continue;
      }
      this.Emit(new Token(0, 1, 0, this._buf[p]), output, ref wroteBlock);
      ++p;
    }
    return p;
  }

  private void Emit(Token t, Stream output, ref bool wroteBlock) {
    // A full block is written only once another token follows, so the last block of a
    // file is never empty.
    if (this._tokens.Count >= BlockTokens)
      this.FlushBlock(output, last: false, ref wroteBlock);
    this._tokens.Add(t);
  }

  // ---- block coding ----

  private static int LengthCode(int length, out int extraBits, out int extra) {
    // Inverse of: code<8 → 2+code; else 2 + ((4|(code&3)) << lbits) + extra, lbits = code/4-1.
    var v = length - 2;
    if (v < 8) { extraBits = 0; extra = 0; return v; }
    var lbits = 31 - int.LeadingZeroCount(v) - 2;
    var code = ((lbits + 1) << 2) | ((v >> lbits) & 3);
    extraBits = lbits;
    extra = v & ((1 << lbits) - 1);
    return code;
  }

  private static int DistSlot(int d0, out int dbits, out int extra) {
    if (d0 < 4) { dbits = 0; extra = 0; return d0; }
    var p = 31 - int.LeadingZeroCount(d0);
    var slot = 2 * p + ((d0 >> (p - 1)) & 1);
    dbits = p - 1;
    extra = d0 - ((2 | (slot & 1)) << dbits);
    return slot;
  }

  private void FlushBlock(Stream output, bool last, ref bool wroteBlock) {
    if (this._tokens.Count == 0 && !last) return;
    var main = new int[NC];
    var dist = new int[DC];
    var low = new int[LDC];
    var rep = new int[RC];
    foreach (var t in this._tokens) {
      switch (t.Kind) {
        case 0: ++main[t.Literal]; break;
        case 1: {
          var code = LengthCode(t.Length - Bonus(t.Distance), out _, out _);
          ++main[262 + code];
          var slot = DistSlot(t.Distance - 1, out var dbits, out var extra);
          ++dist[slot];
          if (dbits >= 4) ++low[extra & 15];
          break;
        }
        case 6: ++main[257]; break;
        default:
          ++main[258 + (t.Kind - 2)];
          ++rep[LengthCode(t.Length, out _, out _)];
          break;
      }
    }

    var mainL = BuildLengths(main);
    var distL = BuildLengths(dist);
    var lowL = BuildLengths(low);
    var repL = BuildLengths(rep);
    var mainC = Canonical(mainL);
    var distC = Canonical(distL);
    var lowC = Canonical(lowL);
    var repC = Canonical(repL);

    var all = new int[NC + DC + LDC + RC];
    mainL.CopyTo(all, 0);
    distL.CopyTo(all, NC);
    lowL.CopyTo(all, NC + DC);
    repL.CopyTo(all, NC + DC + LDC);
    var rle = Rle(all);
    var bcFreq = new int[BC];
    foreach (var (sym, _, _) in rle) ++bcFreq[sym];
    var bcL = BuildLengths(bcFreq);
    var bcC = Canonical(bcL);

    var w = new BitSink();
    // Bit-length table: 20 nibbles; 15 is escaped as 15,0.
    foreach (var l in bcL) {
      w.Write((uint)l, 4);
      if (l == 15) w.Write(0, 4);
    }
    foreach (var (sym, bits, value) in rle) {
      w.Write(bcC[sym], bcL[sym]);
      if (bits > 0) w.Write((uint)value, bits);
    }

    foreach (var t in this._tokens) {
      switch (t.Kind) {
        case 0:
          w.Write(mainC[t.Literal], mainL[t.Literal]);
          break;
        case 1: {
          var code = LengthCode(t.Length - Bonus(t.Distance), out var lb, out var lx);
          w.Write(mainC[262 + code], mainL[262 + code]);
          if (lb > 0) w.Write((uint)lx, lb);
          var slot = DistSlot(t.Distance - 1, out var dbits, out var extra);
          w.Write(distC[slot], distL[slot]);
          if (dbits >= 4) {
            if (dbits > 4) w.Write((uint)(extra >> 4), dbits - 4);
            w.Write(lowC[extra & 15], lowL[extra & 15]);
          } else if (dbits > 0)
            w.Write((uint)extra, dbits);
          break;
        }
        case 6:
          w.Write(mainC[257], mainL[257]);
          break;
        default: {
          var s = 258 + (t.Kind - 2);
          w.Write(mainC[s], mainL[s]);
          var code = LengthCode(t.Length, out var lb, out var lx);
          w.Write(repC[code], repL[code]);
          if (lb > 0) w.Write((uint)lx, lb);
          break;
        }
      }
    }
    this._tokens.Clear();

    var (data, bitCount) = w.Finish();
    var size = data.Length;
    if (size == 0) { data = [0]; size = 1; bitCount = 8; }
    var byteCount = size <= 0xFF ? 1 : size <= 0xFFFF ? 2 : 3;
    if (size > 0xFFFFFF) throw new InvalidOperationException("RAR 5 block too large.");
    var bitsInLast = bitCount - (size - 1) * 8;
    var flags = (byte)(0x80 | (last ? 0x40 : 0) | ((byteCount - 1) << 3) | ((bitsInLast - 1) & 7));
    var check = (byte)(0x5A ^ flags ^ (byte)size ^ (byte)(size >> 8) ^ (byte)(size >> 16));
    output.WriteByte(flags);
    output.WriteByte(check);
    for (var i = 0; i < byteCount; ++i) output.WriteByte((byte)(size >> (8 * i)));
    output.Write(data, 0, size);
    wroteBlock = true;
  }

  /// <summary>RLE of the code-length table: 0-15 literal, 16/17 repeat previous, 18/19 zeros.</summary>
  private static List<(int Sym, int Bits, int Value)> Rle(int[] lengths) {
    var r = new List<(int, int, int)>();
    var i = 0;
    while (i < lengths.Length) {
      var v = lengths[i];
      var run = 1;
      while (i + run < lengths.Length && lengths[i + run] == v) ++run;
      if (v == 0) {
        var left = run;
        while (left > 0) {
          if (left >= 11) { var n = Math.Min(left, 11 + 127); r.Add((19, 7, n - 11)); left -= n; }
          else if (left >= 3) { var n = Math.Min(left, 3 + 7); r.Add((18, 3, n - 3)); left -= n; }
          else { r.Add((0, 0, 0)); --left; }
        }
      } else {
        r.Add((v, 0, 0));
        var left = run - 1;
        while (left > 0) {
          if (left >= 11) { var n = Math.Min(left, 11 + 127); r.Add((17, 7, n - 11)); left -= n; }
          else if (left >= 3) { var n = Math.Min(left, 3 + 7); r.Add((16, 3, n - 3)); left -= n; }
          else { r.Add((v, 0, 0)); --left; }
        }
      }
      i += run;
    }
    return r;
  }

  private static int[] BuildLengths(int[] freq) {
    var used = 0;
    var only = -1;
    for (var i = 0; i < freq.Length; ++i)
      if (freq[i] > 0) { ++used; only = i; }
    if (used == 0) return new int[freq.Length];
    if (used == 1) {
      // One symbol still needs a one-bit code; give a second symbol the other half.
      var l = new int[freq.Length];
      l[only] = 1;
      l[only == 0 ? 1 : 0] = 1;
      return l;
    }
    var enc = new Rar5HuffmanEncoder();
    enc.Build(freq, freq.Length, 15);
    return enc.CodeLengths;
  }

  private static uint[] Canonical(int[] lengths) {
    var count = new int[17];
    foreach (var l in lengths) if (l > 0) ++count[l];
    var next = new uint[17];
    uint code = 0;
    for (var b = 1; b <= 16; ++b) {
      code = (code + (uint)count[b - 1]) << 1;
      next[b] = code;
    }
    var codes = new uint[lengths.Length];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0) codes[i] = next[lengths[i]]++;
    return codes;
  }

  /// <summary>MSB-first bit sink.</summary>
  private sealed class BitSink {
    private byte[] _data = new byte[1 << 16];
    private int _len;
    private ulong _acc;
    private int _bits;
    private long _total;

    public void Write(uint value, int count) {
      if (count == 0) return;
      this._acc = (this._acc << count) | (value & (uint)((1UL << count) - 1));
      this._bits += count;
      this._total += count;
      while (this._bits >= 8) {
        this._bits -= 8;
        this.Put((byte)(this._acc >> this._bits));
      }
    }

    private void Put(byte b) {
      if (this._len == this._data.Length) Array.Resize(ref this._data, this._data.Length * 2);
      this._data[this._len++] = b;
    }

    public (byte[] Data, int BitCount) Finish() {
      if (this._bits > 0) {
        this.Put((byte)(this._acc << (8 - this._bits)));
        this._bits = 0;
      }
      return (this._data.AsSpan(0, this._len).ToArray(), (int)this._total);
    }
  }
}
