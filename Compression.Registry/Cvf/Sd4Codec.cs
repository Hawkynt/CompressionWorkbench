#pragma warning disable CS1591
using System;
using System.IO;

namespace Compression.Registry.Cvf;

/// <summary>
/// Genuine Stacker 4 (SD-4, cluster header <c>0x0081</c>) per-cluster codec,
/// byte-compatible with the dmsdos <c>sd4_decomp</c> decoder. SD-4 is a bespoke
/// dynamic-Huffman format: a helper Huffman table (table1) encodes the 0x150
/// code-lengths of the main table (table2), which then Huffman-codes the data.
/// <para>
/// We emit an <b>all-literals</b> SD-4 stream — table2 is a 256-symbol Huffman
/// over the cluster's byte frequencies (genuine entropy compression), with no LZ
/// reps/prog tokens; the decoder terminates on output-full. The bitstream is
/// MSB-first packed into little-endian 16-bit words; Huffman codes are canonical
/// (first-code-per-length, not bit-reversed) exactly as <c>sd4b_rdhufi</c> builds
/// them.
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

  public static byte[] Decode(byte[] payload, int inLen, int outLen) {
    var r = new BitReader(payload, inLen);
    if (r.ReadN(16) != 0x81) throw new InvalidDataException("SD-4: bad magic.");
    var start = r.ReadN(3) + 1;
    var bmax2 = r.ReadN(5);
    var l1 = new byte[0x16];
    l1[0] = (byte)r.ReadN(4);
    for (var i = start; i <= bmax2; i++) l1[i] = (byte)r.ReadN(4);
    for (var i = 0x10; i <= 0x15; i++) l1[i] = (byte)r.ReadN(4);
    var (t1c, t1l) = Canonical(l1);
    var dec1 = BuildDecode(l1, t1c, t1l);

    var clen = new byte[0x150];
    for (var k = 0; k < 0x150; k++) clen[k] = (byte)DecodeSym(r, dec1);

    var (t2c, t2l) = Canonical(clen);
    var dec2 = BuildDecode(clen, t2c, t2l);

    var outp = new byte[outLen];
    for (var p = 0; p < outLen; p++) outp[p] = (byte)DecodeSym(r, dec2);
    return outp;
  }

  // Simple (code<<? ) decode map keyed by (len,code).
  private static System.Collections.Generic.Dictionary<(int, int), int> BuildDecode(byte[] lengths, int[] code, int[] len) {
    var m = new System.Collections.Generic.Dictionary<(int, int), int>();
    for (var s = 0; s < lengths.Length; s++) if (len[s] > 0) m[(len[s], code[s])] = s;
    return m;
  }

  private static int DecodeSym(BitReader r, System.Collections.Generic.Dictionary<(int, int), int> map) {
    var code = 0;
    for (var l = 1; l <= 16; l++) { code = (code << 1) | r.ReadN(1); if (map.TryGetValue((l, code), out var s)) return s; }
    throw new InvalidDataException("SD-4: bad code.");
  }
}
