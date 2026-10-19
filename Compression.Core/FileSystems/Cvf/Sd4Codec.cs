#pragma warning disable CS1591
using System;
using System.IO;

namespace Compression.Core.FileSystems.Cvf;

/// <summary>
/// Genuine Stacker 4 (SD-4, cluster header <c>0x0081</c>) per-cluster codec, as
/// specified in <c>docs/CVF-CODECS-ON-DISK.md</c>. SD-4 is Huffman-coded LZ77: a
/// helper code (22 symbols: lengths 0..15 and six run tokens) transmits the 0x150
/// code lengths of the main code, whose symbols are the 256 literals, 63 short-match
/// tokens, 16 long-match tokens and the end token.
/// <para>
/// <see cref="Decode"/> reads the complete format — run tokens, short and long
/// matches with length extensions — and is pinned to streams the dmsdos SD-4
/// encoder wrote. <see cref="Encode"/> emits an <b>all-literals</b> stream (main code
/// over the cluster's byte frequencies, no match tokens), which the dmsdos driver
/// reads back byte-exact. The bitstream is MSB-first within little-endian 16-bit
/// words; codes are canonical and MSB-first.
/// </para>
/// </summary>
public static class Sd4Codec {
  private const int MaxLen = 10;   // keep table2 codes within the decoder's fast table (bn<=10)

  // ── MSB-first bit writer into 16-bit little-endian words ───────────────────
  private sealed class BitWriter {
    private readonly System.Collections.Generic.List<ushort> _words = [];
    private uint _cur;
    private int _bits;
    public void Put(int value, int n) {
      for (var i = n - 1; i >= 0; i--) {
        this._cur = (this._cur << 1) | (uint)((value >> i) & 1);
        if (++this._bits == 16) { this._words.Add((ushort)this._cur); this._cur = 0; this._bits = 0; }
      }
    }
    public byte[] ToBytes() {
      if (this._bits > 0) { this._cur <<= 16 - this._bits; this._words.Add((ushort)this._cur); }
      var b = new byte[this._words.Count * 2];
      for (var i = 0; i < this._words.Count; i++) { b[i * 2] = (byte)this._words[i]; b[i * 2 + 1] = (byte)(this._words[i] >> 8); }
      return b;
    }
  }

  // ── MSB-first bit reader (mirror of sd4b_rd*) ──────────────────────────────
  private sealed class BitReader {
    private readonly byte[] _d; private readonly int _len; private long _buf; private int _pb; private int _pd; private readonly int _pe;
    public BitReader(byte[] d, int len) { this._d = d; this._len = len; this._pe = (len + 1) >> 1; }
    private ushort Word(int i) { var o = i * 2; var lo = o < this._len ? this._d[o] : 0; var hi = o + 1 < this._len ? this._d[o + 1] : 0; return (ushort)(lo | (hi << 8)); }
    public int ReadN(int n) {
      if (this._pb < n) { this._buf = (this._buf << 16) | (this._pd < this._pe ? Word(this._pd++) : 0u); this._pb += 16; if (this._pb < n) { this._buf = (this._buf << 16) | (this._pd < this._pe ? Word(this._pd++) : 0u); this._pb += 16; } }
      this._pb -= n;
      return (int)((this._buf >> this._pb) & ((1u << n) - 1));
    }
  }

  // ── canonical Huffman: lengths -> (code,len) per symbol, matching sd4b_rdhufi
  private static (int[] Code, int[] Len) Canonical(byte[] lengths) {
    var cdLn = new int[17];
    foreach (var l in lengths) if (l is > 0 and < 16) cdLn[l]++;
    var firstCode = new int[17];
    int us = 0;
    for (var i = 1; i <= 16; i++) { firstCode[i] = us; us += cdLn[i]; us <<= 1; }
    var next = (int[])firstCode.Clone();
    var code = new int[lengths.Length];
    var len = new int[lengths.Length];
    for (var s = 0; s < lengths.Length; s++) { var l = lengths[s]; if (l == 0) continue; code[s] = next[l]++; len[s] = l; }
    return (code, len);
  }

  // ── length-limited Huffman code lengths from frequencies ───────────────────
  private static byte[] HuffmanLengths(int[] freq, int maxLen) {
    var n = freq.Length;
    var lengths = new byte[n];
    var nodes = new System.Collections.Generic.List<(long F, int Sym, int Left, int Right)>();
    for (var s = 0; s < n; s++) if (freq[s] > 0) nodes.Add((freq[s], s, -1, -1));
    if (nodes.Count == 0) return lengths;
    if (nodes.Count == 1) { lengths[nodes[0].Sym] = 1; FixKraft(lengths, maxLen); return lengths; }

    var all = new System.Collections.Generic.List<(long F, int Sym, int Left, int Right)>(nodes);
    var live = new System.Collections.Generic.List<int>();
    for (var i = 0; i < all.Count; i++) live.Add(i);
    while (live.Count > 1) {
      live.Sort((a, b) => all[a].F.CompareTo(all[b].F));
      var a = live[0]; var b = live[1];
      live.RemoveRange(0, 2);
      all.Add((all[a].F + all[b].F, -1, a, b));
      live.Add(all.Count - 1);
    }
    AssignDepth(all, live[0], 0, lengths);
    FixKraft(lengths, maxLen);
    return lengths;
  }

  private static void AssignDepth(System.Collections.Generic.List<(long F, int Sym, int Left, int Right)> all, int node, int depth, byte[] lengths) {
    var nd = all[node];
    if (nd.Sym >= 0) { lengths[nd.Sym] = (byte)Math.Max(1, depth); return; }
    AssignDepth(all, nd.Left, depth + 1, lengths);
    AssignDepth(all, nd.Right, depth + 1, lengths);
  }

  // Clamp to maxLen and repair the Kraft sum so the code is complete.
  private static void FixKraft(byte[] lengths, int maxLen) {
    for (var i = 0; i < lengths.Length; i++) if (lengths[i] > maxLen) lengths[i] = (byte)maxLen;
    // Kraft sum in units of 2^-maxLen.
    long total = 0;
    foreach (var l in lengths) if (l > 0) total += 1L << (maxLen - l);
    var full = 1L << maxLen;
    // Over-full: lengthen some short codes.
    while (total > full) {
      for (var i = 0; i < lengths.Length && total > full; i++) {
        if (lengths[i] > 0 && lengths[i] < maxLen) { total -= 1L << (maxLen - lengths[i]); lengths[i]++; total += 1L << (maxLen - lengths[i]); }
      }
    }
    // Under-full: shorten codes to fill the space (keeps it complete).
    var guard = 0;
    while (total < full && guard++ < 100000) {
      for (var i = 0; i < lengths.Length && total < full; i++) {
        if (lengths[i] > 1) {
          var delta = 1L << (maxLen - lengths[i]);
          if (total + delta <= full) { total += delta; lengths[i]--; } // shortening adds delta again
        }
      }
      if (guard > 99999) break;
    }
  }

  public static byte[] Encode(ReadOnlySpan<byte> data) {
    // table2: Huffman over 0x150 symbols — 256 literals + the end token 0x14F
    // (sd4_decomp reads an explicit end token when the output fills). A complete
    // code needs >= 2 symbols, so empty/uniform clusters get a nominal literal.
    var freq = new int[0x150];
    foreach (var b in data) freq[b]++;
    freq[0x14F] = Math.Max(1, freq[0x14F]);          // end symbol always present
    var nz = 0; foreach (var f in freq) if (f > 0) nz++;
    for (var i = 0; i < 256 && nz < 2; i++) if (freq[i] == 0) { freq[i] = 1; nz++; }
    var clen = HuffmanLengths(freq, MaxLen);         // 0x150 code-lengths
    var bmax2 = 0; foreach (var l in clen) if (l > bmax2) bmax2 = l;
    if (bmax2 == 0) bmax2 = 1;

    // table1: Huffman over the frequency of each length-VALUE (0..bmax2) in clen.
    var lvFreq = new int[0x16];
    foreach (var v in clen) lvFreq[v]++;
    var l1 = HuffmanLengths(lvFreq, 7);     // table1 bn capped at 7 by the decoder
    var bmax1 = 0; foreach (var l in l1) if (l > bmax1) bmax1 = l;
    if (bmax1 == 0) bmax1 = 1;

    var (t1code, t1len) = Canonical(l1);
    var (t2code, t2len) = Canonical(clen);

    var w = new BitWriter();
    w.Put(0x81, 16);                         // SD-4 magic
    w.Put(0, 3);                             // start = rdn(3)+1 = 1
    w.Put(bmax2, 5);                         // ie = bmax2
    w.Put(l1[0], 4);                         // ca[0] = bmax1 field (length-value 0)
    for (var i = 1; i <= bmax2; i++) w.Put(l1[i], 4);   // ca[1..bmax2]
    for (var i = 0x10; i <= 0x15; i++) w.Put(0, 4);     // RLE tokens unused
    // emit the 0x150 code-lengths as table1 length-value codes.
    foreach (var v in clen) w.Put(t1code[v], t1len[v]);
    // emit data as table2 literal codes, then the end token (read by sd4_decomp
    // via len_150 bits once the output is full).
    foreach (var b in data) w.Put(t2code[b], t2len[b]);
    w.Put(t2code[0x14F], t2len[0x14F]);
    return w.ToBytes();
  }

  // ── decoding (docs/CVF-CODECS-ON-DISK.md, "SD-4") ──────────────────────────

  private const int HelperSymbols = 0x16;     // code lengths 0..15 + run tokens 1..6
  private const int MainSymbols = 0x150;      // literals 0..255 + tokens 1..0x50
  private const int EndToken = 0x50;
  private const int FirstLongToken = 0x40;

  // Short-match classes: token t (1..0x3F) is class (t-1)/3 with length 3 + (t-1)%3; the offset is
  // ShortBase[class] plus ShortBits[class] extra bits.
  private static readonly int[] ShortBits = [0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9];
  private static readonly int[] ShortBase = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536];

  // Long-match offset classes, chosen by a 2-bit selector.
  private static readonly int[] LongBits = [5, 7, 9, 11];
  private static readonly int[] LongBase = [0x1, 0x21, 0xA1, 0x2A1];

  // Escalating length extensions of a long match whose token says 21.
  private static readonly int[] LengthExtensionBits = [4, 8, 12, 16];

  /// <summary>
  /// Decodes one SD-4 cluster payload into <paramref name="outLen"/> bytes. A stream that ends (end
  /// token) before the cluster is full leaves the tail zero.
  /// </summary>
  /// <exception cref="InvalidDataException">Bad magic, an inconsistent code table, or a match that reaches before the cluster start.</exception>
  public static byte[] Decode(byte[] payload, int inLen, int outLen) {
    var r = new BitReader(payload, inLen);
    if (r.ReadN(16) != 0x81) throw new InvalidDataException("SD-4: bad magic.");

    // Helper-table code lengths: symbol 0, symbols first..last, then the six run tokens.
    var first = r.ReadN(3) + 1;
    var last = r.ReadN(5);
    if (first > last || last > 15) throw new InvalidDataException("SD-4: helper table range is invalid.");
    var helperLengths = new byte[HelperSymbols];
    helperLengths[0] = (byte)r.ReadN(4);
    for (var i = first; i <= last; ++i) helperLengths[i] = (byte)r.ReadN(4);
    for (var i = 0x10; i < HelperSymbols; ++i) helperLengths[i] = (byte)r.ReadN(4);
    var helper = new CanonicalDecoder(helperLengths);

    var mainLengths = new byte[MainSymbols];
    for (var n = 0; n < MainSymbols;) {
      var symbol = helper.Decode(r);
      if (symbol < 0x10) { mainLengths[n++] = (byte)symbol; continue; }
      if (n == 0 && symbol >= 0x13) throw new InvalidDataException("SD-4: length repeat with no previous length.");
      var (value, count) = (symbol - 0x0F) switch {
        1 => ((byte)0, 2),
        2 => ((byte)0, 3),
        3 => ((byte)0, ZeroRunLength(r)),
        4 => (mainLengths[n - 1], 2),
        5 => (mainLengths[n - 1], 3),
        _ => (mainLengths[n - 1], RepeatRunLength(r)),
      };
      count = Math.Min(count, MainSymbols - n);
      mainLengths.AsSpan(n, count).Fill(value);
      n += count;
    }
    var main = new CanonicalDecoder(mainLengths);

    var output = new byte[outLen];
    var p = 0;
    while (p < outLen) {
      var symbol = main.Decode(r);
      if (symbol < 0x100) { output[p++] = (byte)symbol; continue; }

      var token = symbol - 0xFF;
      if (token == EndToken) break;

      int length, offset;
      if (token < FirstLongToken) {
        var cls = (token - 1) / 3;
        length = 3 + (token - 1) % 3;
        offset = ShortBase[cls] + r.ReadN(ShortBits[cls]);
      } else {
        var cls = r.ReadN(2);
        offset = LongBase[cls] + r.ReadN(LongBits[cls]);
        length = token - FirstLongToken + 6;
        var saturated = 21;
        foreach (var bits in LengthExtensionBits) {
          if (length != saturated) break;
          var extra = r.ReadN(bits);
          length += extra;
          saturated += (1 << bits) - 1;
        }
      }

      if (offset > p) throw new InvalidDataException("SD-4: match reaches before the start of the cluster.");
      length = Math.Min(length, outLen - p);   // the reference driver clips an overlong final match
      for (var k = 0; k < length; ++k, ++p) output[p] = output[p - offset];
    }
    return output;
  }

  // Run token 3: 4 + 3 bits; a saturated 7 continues with 7-bit groups while they read 0x7F.
  private static int ZeroRunLength(BitReader r) {
    var v = r.ReadN(3);
    var n = 4 + v;
    if (v == 7)
      do { v = r.ReadN(7); n += v; } while (v == 0x7F);
    return n;
  }

  // Run token 6: 4 + 3-bit groups, continuing while a group reads 7.
  private static int RepeatRunLength(BitReader r) {
    var n = 4;
    int v;
    do { v = r.ReadN(3); n += v; } while (v == 7);
    return n;
  }

  /// <summary>
  /// Canonical MSB-first prefix code: codes ascend with length and, within a length, with symbol
  /// number. The code must be complete (Kraft sum exactly one) with lengths of at most 15 bits.
  /// </summary>
  private sealed class CanonicalDecoder {
    private readonly int[] _count = new int[16];
    private readonly int[] _symbols;

    public CanonicalDecoder(ReadOnlySpan<byte> lengths) {
      foreach (var l in lengths) {
        if (l > 15) throw new InvalidDataException("SD-4: code length above 15.");
        ++this._count[l];
      }
      this._count[0] = 0;
      var left = 1;
      for (var len = 1; len <= 15; ++len) {
        left = (left << 1) - this._count[len];
        if (left < 0) throw new InvalidDataException("SD-4: over-subscribed code table.");
      }
      if (left != 0) throw new InvalidDataException("SD-4: incomplete code table.");

      var offsets = new int[16];
      for (var len = 1; len < 15; ++len) offsets[len + 1] = offsets[len] + this._count[len];
      this._symbols = new int[offsets[15] + this._count[15]];
      for (var s = 0; s < lengths.Length; ++s)
        if (lengths[s] != 0) this._symbols[offsets[lengths[s]]++] = s;
    }

    public int Decode(BitReader r) {
      int code = 0, firstCode = 0, index = 0;
      for (var len = 1; len <= 15; ++len) {
        code |= r.ReadN(1);
        var count = this._count[len];
        if (code - firstCode < count) return this._symbols[index + code - firstCode];
        index += count;
        firstCode = (firstCode + count) << 1;
        code <<= 1;
      }
      throw new InvalidDataException("SD-4: undecodable bit sequence.");
    }
  }
}
