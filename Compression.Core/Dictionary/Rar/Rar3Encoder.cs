using Compression.Core.Simd;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Encoder for the RAR 2.9 compression format (RAR 3.x/4.x archives, unpack version 29), the
/// counterpart of <see cref="Rar3Decoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Emits literals, new matches (symbols 271-298) and matches on the four repeat distances
/// (259-262) in blocks of at most <see cref="BlockTokens"/> tokens. Every block brings its own
/// Huffman tables: the first at the start of the file's data, the later ones after symbol 256
/// with the "new tables" bit, byte aligned as the decoder reads them. The file ends with symbol
/// 256 and the "new file" bit. Filters and PPMd are not used.
/// </para>
/// <para>
/// Input streams through a sliding buffer of the dictionary plus 1 MB, so memory stays bounded
/// whatever the file size. Successive calls continue a solid stream: the window and the repeat
/// distances carry over, and each file announces fresh tables for the next one.
/// </para>
/// </remarks>
public sealed class Rar3Encoder {
  private const int MainCodeSize = 299;
  private const int OffsetCodeSize = 60;
  private const int LowOffsetCodeSize = 17;
  private const int LengthCodeSize = 28;
  private const int PrecodeSize = 20;
  private const int MaxNewLength = 258;
  private const int MaxRepeatLength = 257;
  private const int MinMatch = 3;
  private const int HashBits = 16;
  private const int MaxChain = 128;
  private const int ChunkSize = 1 << 20;
  // The longest token is a new match: MaxNewLength plus a bonus of two.
  private const int Lookahead = MaxNewLength + 2 + 8;

  /// <summary>Tokens per block; each block carries its own tables.</summary>
  internal const int BlockTokens = 1 << 17;

  private static readonly int[] LengthBases = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
  private static readonly int[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
  private static readonly int[] OffsetBases = [
    0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072,
    4096, 6144, 8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752,
    524288, 589824, 655360, 720896, 786432, 851968, 917504, 983040, 1048576, 1310720, 1572864, 1835008, 2097152, 2359296,
    2621440, 2883584, 3145728, 3407872, 3670016, 3932160];
  private static readonly int[] OffsetBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
    15, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18];

  private readonly int _windowSize;
  private readonly int[] _oldOffsets = new int[4];

  private byte[] _buf;
  private int _bufLen;          // valid bytes in _buf
  private long _bufBase;        // stream offset of _buf[0]
  private readonly int[] _head = new int[1 << HashBits];
  private readonly int[] _prev;

  private readonly List<Token> _tokens = new(BlockTokens);

  private readonly record struct Token(int Symbol, int Length, int Offset);

  /// <summary>Creates an encoder for a dictionary of 2^<paramref name="windowBits"/> bytes.</summary>
  /// <param name="windowBits">Dictionary size as a power of two, 16-22.</param>
  public Rar3Encoder(int windowBits = 22) {
    this._windowSize = 1 << Math.Clamp(windowBits, 16, 22);
    this._prev = new int[this._windowSize];
    this._buf = new byte[this._windowSize + ChunkSize];
    Array.Fill(this._head, -1);
  }

  /// <summary>Compresses one file; call again for the next file of a solid archive.</summary>
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
    var w = new Rar3BitWriter();
    Rar3HuffmanEncoder? previousMain = null;
    var pos = this._bufLen;
    var left = length;
    while (true) {
      while (left > 0 && this._bufLen < this._buf.Length) {
        var n = input.Read(this._buf, this._bufLen, (int)Math.Min(this._buf.Length - this._bufLen, left));
        if (n <= 0) throw new EndOfStreamException("Input ended before the declared length.");
        this._bufLen += n;
        left -= n;
      }
      var final = left == 0;
      var parseEnd = final ? this._bufLen : this._bufLen - Lookahead;
      pos = this.Parse(pos, parseEnd, final ? this._bufLen : parseEnd + Lookahead, w, output, ref previousMain);
      if (final) break;
      this.Slide(ref pos);
    }
    if (this._tokens.Count > 0) previousMain = this.WriteBlock(w, previousMain);

    // End of file; the next file brings its own tables.
    previousMain!.EncodeSymbol(w, 256);
    w.WriteBit(0);
    w.WriteBit(1);
    w.AlignToByte();
    w.DrainTo(output);
  }

  // ── window ───────────────────────────────────────────────────────────

  private void Slide(ref int pos) {
    var keepFrom = Math.Max(0, pos - this._windowSize);
    if (keepFrom == 0) {
      if (this._bufLen < this._buf.Length) return;
      Array.Resize(ref this._buf, this._buf.Length + ChunkSize);
      return;
    }
    var keep = this._bufLen - keepFrom;
    Buffer.BlockCopy(this._buf, keepFrom, this._buf, 0, keep);
    this._bufLen = keep;
    this._bufBase += keepFrom;
    pos -= keepFrom;
    for (var i = 0; i < this._head.Length; ++i)
      this._head[i] = this._head[i] >= keepFrom ? this._head[i] - keepFrom : -1;
    // _prev is indexed by stream offset masked to the window, so its slots stay put; the buffer
    // indices it stores move with the data.
    for (var i = 0; i < this._prev.Length; ++i)
      this._prev[i] = this._prev[i] >= keepFrom ? this._prev[i] - keepFrom : -1;
  }

  private int Hash(int p) => (int)(((uint)(this._buf[p] | (this._buf[p + 1] << 8) | (this._buf[p + 2] << 16)) * 2654435761u) >> (32 - HashBits));

  private void Insert(int p, int limit) {
    if (p + MinMatch > limit) return;
    var h = this.Hash(p);
    this._prev[(this._bufBase + p) & (this._windowSize - 1)] = this._head[h];
    this._head[h] = p;
  }

  /// <summary>Longest earlier match for <paramref name="p"/> within the window; inserts <paramref name="p"/>.</summary>
  private (int Length, int Offset) FindMatch(int p, int limit) {
    if (p + MinMatch > limit) return (0, 0);
    var maxLength = Math.Min(MaxNewLength + 2, limit - p);
    var maxOffset = this._windowSize - 1;
    var data = this._buf.AsSpan(0, limit);
    var h = this.Hash(p);
    var candidate = this._head[h];
    int best = 0, bestOffset = 0;
    for (var chain = MaxChain; candidate >= 0 && chain > 0; --chain) {
      var offset = p - candidate;
      if (offset <= 0 || offset > maxOffset) break;
      if (data[candidate + best] == data[p + best]) {
        var l = SimdMatchLength.GetMatchLength(data, candidate, p, maxLength);
        if (l > best) {
          best = l;
          bestOffset = offset;
          if (l >= maxLength) break;
        }
      }
      var next = this._prev[(this._bufBase + candidate) & (this._windowSize - 1)];
      if (next >= candidate) break;
      candidate = next;
    }
    this._prev[(this._bufBase + p) & (this._windowSize - 1)] = this._head[h];
    this._head[h] = p;
    return best >= MinMatch ? (best, bestOffset) : (0, 0);
  }

  private (int Index, int Length) BestRepeat(int p, int limit) {
    var best = (Index: -1, Length: 0);
    var max = Math.Min(MaxRepeatLength, limit - p);
    for (var k = 0; k < 4; ++k) {
      var offset = this._oldOffsets[k];
      if (offset <= 0 || offset > p) continue;
      var length = SimdMatchLength.GetMatchLength(this._buf.AsSpan(0, limit), p - offset, p, max);
      if (length > best.Length) best = (k, length);
    }
    return best;
  }

  // ── parsing ──────────────────────────────────────────────────────────

  private int Parse(int p, int end, int limit, Rar3BitWriter w, Stream output, ref Rar3HuffmanEncoder? previousMain) {
    while (p < end) {
      if (this._tokens.Count >= BlockTokens) {
        previousMain = this.WriteBlock(w, previousMain);
        w.DrainTo(output);
      }
      var rep = this.BestRepeat(p, limit);
      var match = this.FindMatch(p, limit);
      var newLength = match.Length - Bonus(match.Offset);
      if (rep.Length >= 2 && rep.Length + 1 >= newLength) {
        var offset = this._oldOffsets[rep.Index];
        this._tokens.Add(new(259 + rep.Index, rep.Length, offset));
        for (var i = rep.Index; i > 0; --i) this._oldOffsets[i] = this._oldOffsets[i - 1];
        this._oldOffsets[0] = offset;
        for (var i = 1; i < rep.Length; ++i) this.Insert(p + i, limit);
        p += rep.Length;
      } else if (match.Length > 0 && newLength >= 3) {
        var length = Math.Min(match.Length, MaxNewLength + Bonus(match.Offset));
        var coded = length - Bonus(match.Offset);
        this._tokens.Add(new(271 + LengthSlot(coded - 3), length, match.Offset));
        this._oldOffsets[3] = this._oldOffsets[2];
        this._oldOffsets[2] = this._oldOffsets[1];
        this._oldOffsets[1] = this._oldOffsets[0];
        this._oldOffsets[0] = match.Offset;
        for (var i = 1; i < length; ++i) this.Insert(p + i, limit);
        p += length;
      } else {
        this._tokens.Add(new(this._buf[p], 1, 0));
        ++p;
      }
    }
    return p;
  }

  // ── block coding ─────────────────────────────────────────────────────

  /// <summary>
  /// Codes the pending tokens as one block. A block after the first is announced by symbol 256
  /// in the previous block's code followed by the "new tables" bit.
  /// </summary>
  private Rar3HuffmanEncoder WriteBlock(Rar3BitWriter w, Rar3HuffmanEncoder? previousMain) {
    var main = new int[MainCodeSize];
    var offsets = new int[OffsetCodeSize];
    var lows = new int[LowOffsetCodeSize];
    var lengths = new int[LengthCodeSize];
    foreach (var t in this._tokens) {
      ++main[t.Symbol];
      if (t.Symbol >= 271) {
        var (slot, _) = OffsetSlot(t.Offset);
        ++offsets[slot];
        if (slot > 9) ++lows[(t.Offset - 1 - OffsetBases[slot]) & 0xF];
      } else if (t.Symbol is >= 259 and <= 262)
        ++lengths[LengthSlot(t.Length - 2)];
    }
    ++main[256];
    foreach (var f in (int[][])[main, offsets, lows, lengths]) AtLeastTwo(f);

    var mainCode = new Rar3HuffmanEncoder();
    var offsetCode = new Rar3HuffmanEncoder();
    var lowCode = new Rar3HuffmanEncoder();
    var lengthCode = new Rar3HuffmanEncoder();
    mainCode.Build(main, MainCodeSize);
    offsetCode.Build(offsets, OffsetCodeSize);
    lowCode.Build(lows, LowOffsetCodeSize);
    lengthCode.Build(lengths, LengthCodeSize);

    if (previousMain != null) {
      previousMain.EncodeSymbol(w, 256);
      w.WriteBit(1);
      w.AlignToByte();
    }
    WriteTables(w, [.. mainCode.CodeLengths, .. offsetCode.CodeLengths, .. lowCode.CodeLengths, .. lengthCode.CodeLengths]);

    foreach (var t in this._tokens) {
      mainCode.EncodeSymbol(w, t.Symbol);
      if (t.Symbol < 256) continue;
      if (t.Symbol <= 262) {
        var slot = LengthSlot(t.Length - 2);
        lengthCode.EncodeSymbol(w, slot);
        w.WriteBits((uint)(t.Length - 2 - LengthBases[slot]), LengthBits[slot]);
        continue;
      }
      var lengthSlot = t.Symbol - 271;
      w.WriteBits((uint)(t.Length - Bonus(t.Offset) - 3 - LengthBases[lengthSlot]), LengthBits[lengthSlot]);
      var (offsetSlot, bits) = OffsetSlot(t.Offset);
      offsetCode.EncodeSymbol(w, offsetSlot);
      var extra = (uint)(t.Offset - 1 - OffsetBases[offsetSlot]);
      if (bits == 0) continue;
      if (offsetSlot > 9) {
        if (bits > 4) w.WriteBits(extra >> 4, bits - 4);
        lowCode.EncodeSymbol(w, (int)(extra & 0xF));
      } else
        w.WriteBits(extra, bits);
    }
    this._tokens.Clear();
    return mainCode;
  }

  /// <summary>Keeps every code a proper prefix code: a table with fewer than two symbols gets dummies.</summary>
  private static void AtLeastTwo(int[] frequencies) {
    var used = frequencies.Count(f => f > 0);
    for (var i = 0; used < 2 && i < frequencies.Length; ++i)
      if (frequencies[i] == 0) {
        frequencies[i] = 1;
        ++used;
      }
  }

  /// <summary>Lengths the decoder adds for far matches.</summary>
  private static int Bonus(int offset) => (offset >= 0x2000 ? 1 : 0) + (offset >= 0x40000 ? 1 : 0);

  private static int LengthSlot(int value) {
    var slot = LengthBases.Length - 1;
    while (LengthBases[slot] > value) --slot;
    return slot;
  }

  private static (int Slot, int Bits) OffsetSlot(int offset) {
    var slot = OffsetBases.Length - 1;
    while (OffsetBases[slot] > offset - 1) --slot;
    return (slot, OffsetBits[slot]);
  }

  /// <summary>Tables as the decoder reads them: LZ flag, fresh lengths, precode, then run-length coded lengths.</summary>
  private static void WriteTables(Rar3BitWriter w, int[] lengths) {
    var symbols = new List<(int Symbol, int Extra, int ExtraBits)>();
    for (var i = 0; i < lengths.Length;) {
      if (lengths[i] == 0) {
        var run = 1;
        while (i + run < lengths.Length && lengths[i + run] == 0 && run < 138) ++run;
        if (run >= 11) {
          symbols.Add((19, run - 11, 7));
          i += run;
          continue;
        }
        if (run >= 3) {
          symbols.Add((18, run - 3, 3));
          i += run;
          continue;
        }
      } else if (i > 0 && lengths[i] == lengths[i - 1]) {
        var run = 0;
        while (i + run < lengths.Length && lengths[i + run] == lengths[i - 1] && run < 138) ++run;
        if (run >= 11) {
          symbols.Add((17, run - 11, 7));
          i += run;
          continue;
        }
        if (run >= 3) {
          symbols.Add((16, Math.Min(run, 10) - 3, 3));
          i += Math.Min(run, 10);
          continue;
        }
      }
      symbols.Add((lengths[i], 0, 0));
      ++i;
    }

    var frequencies = new int[PrecodeSize];
    foreach (var s in symbols) ++frequencies[s.Symbol];
    AtLeastTwo(frequencies);
    var precode = new Rar3HuffmanEncoder();
    precode.Build(frequencies, PrecodeSize);

    w.WriteBit(0);   // LZ block
    w.WriteBit(0);   // lengths are not deltas of the previous tables
    foreach (var l in precode.CodeLengths) {
      w.WriteBits((uint)l, 4);
      if (l == 15) w.WriteBits(0, 4);
    }
    foreach (var s in symbols) {
      precode.EncodeSymbol(w, s.Symbol);
      if (s.ExtraBits > 0) w.WriteBits((uint)s.Extra, s.ExtraBits);
    }
  }
}
