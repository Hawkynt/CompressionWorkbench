#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Compression.Registry.Cvf;

/// <summary>
/// Compression methods for the MS-DOS DoubleSpace/DriveSpace CVF cluster codec
/// family, byte-compatible with the dmsdos driver's <c>ds_dec</c>/<c>jm_dec</c>.
/// </summary>
public enum CvfLzMethod { Stored, Ds, Jm, Auto, Sq }

/// <summary>
/// Genuine DoubleSpace/DriveSpace per-cluster compression codec (DS-0-x and
/// JM-0-x), verified byte-exact against the independent dmsdos decoder. The
/// bitstream packs bits LSB-first into little-endian 16-bit words; a cluster
/// payload is a 16-bit magic (<c>"DS"</c>=0x5344 / <c>"MJ"</c>=0x4D4A) + 16-bit
/// version + an LZ77 token stream terminated by the 0x113f sync.
/// </summary>
public static class CvfLzCodec {
  public const uint DS_0_0 = 0x00005344;
  public const uint JM_0_0 = 0x00004D4A;
  public const uint SQ_0_0 = 0x00005153;

  private const int DsMagic = 0x5344;
  private const int JmMagic = 0x4D4A;
  private const int SqMagic = 0x5153;
  private const int Sync = 0x113f;
  private const int MaxLen = 512;          // safe for both (DS replen<=512, JM<=513)

  // ── bit writer: bit i -> word[i/16] bit i%16, words stored little-endian ───
  private sealed class BitWriter {
    private readonly List<ushort> _words = [];
    private int _bit;
    public void Put(int value, int n) {
      for (var i = 0; i < n; i++, this._bit++) {
        var w = this._bit >> 4;
        while (this._words.Count <= w) this._words.Add(0);
        if (((value >> i) & 1) != 0) this._words[w] |= (ushort)(1 << (this._bit & 15));
      }
    }
    public byte[] ToBytes() {
      var b = new byte[this._words.Count * 2];
      for (var i = 0; i < this._words.Count; i++) {
        b[i * 2] = (byte)this._words[i];
        b[i * 2 + 1] = (byte)(this._words[i] >> 8);
      }
      return b;
    }
  }

  // ── bit reader: faithful port of dmsdos dblb_* ─────────────────────────────
  private sealed class BitReader {
    private static readonly uint[] Mask =
      [0x0, 0x1, 0x3, 0x7, 0xF, 0x1F, 0x3F, 0x7F, 0xFF,
       0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF, 0x3FFF, 0x7FFF, 0xFFFF];
    private readonly byte[] _d;
    private readonly int _len;
    private uint _buf;
    private int _pb = 32;
    private int _pd;
    private readonly int _pe;
    public BitReader(byte[] d, int len) { this._d = d; this._len = len; this._pe = (len + 1) >> 1; }
    private ushort Word(int i) {
      var o = i * 2;
      var lo = o < this._len ? this._d[o] : 0;
      var hi = o + 1 < this._len ? this._d[o + 1] : 0;
      return (ushort)(lo | (hi << 8));
    }
    public uint Peek() {
      if (this._pb >= 16) {
        this._buf >>= 16; this._pb -= 16;
        if (this._pd < this._pe) this._buf |= (uint)Word(this._pd++) << 16;
      }
      return this._buf >> this._pb;
    }
    public void Skip(int n) => this._pb += n;
    public uint ReadN(int n) { var u = this.Peek(); this._pb += n; return u & Mask[n]; }

    public int RdOffs() {
      var u = this.Peek();
      switch (u & 3) {
        case 0: case 2: this._pb += 1 + 6; return (int)(63 & (u >> 1));
        case 1: this._pb += 2 + 8; return (int)((255 & (u >> 2)) + 64);
        default: this._pb += 2 + 12; return (int)((4095 & (u >> 2)) + 320);
      }
    }
    public int RdLen() {
      var u = this.Peek();
      switch (u & 15) {
        case 1: case 3: case 5: case 7: case 9: case 11: case 13: case 15: this._pb += 1; return 3;
        case 2: case 6: case 10: case 14: this._pb += 3; return (int)((1 & (u >> 2)) + 4);
        case 4: case 12: this._pb += 5; return (int)((3 & (u >> 3)) + 6);
        case 8: this._pb += 7; return (int)((7 & (u >> 4)) + 10);
      }
      switch ((u >> 4) & 15) {
        case 1: case 3: case 5: case 7: case 9: case 11: case 13: case 15: this._pb += 9; return (int)((15 & (u >> 5)) + 18);
        case 2: case 6: case 10: case 14: this._pb += 11; return (int)((31 & (u >> 6)) + 34);
        case 4: case 12: this._pb += 13; return (int)((63 & (u >> 7)) + 66);
        case 8: this._pb += 15; return (int)((127 & (u >> 8)) + 130);
      }
      this._pb += 9;
      if ((u & 256) != 0) return (int)this.ReadN(8) + 258;
      return -1;
    }
  }

  // ── length / offset encoders (inverse of RdLen / RdOffs) ───────────────────
  private static void PutLen(BitWriter w, int v) {  // v = rdlen value, 3..513
    var x = v - 2;
    var c = 0;
    while ((1 << (c + 1)) <= x) c++;
    w.Put(1 << c, c + 1);     // c zero bits then a 1
    w.Put(x - (1 << c), c);   // c data bits
  }

  private static void PutOffs(BitWriter w, int off) {
    if (off <= 63) { w.Put(0, 1); w.Put(off, 6); }
    else if (off <= 319) { w.Put(1, 2); w.Put(off - 64, 8); }
    else { w.Put(3, 2); w.Put(off - 320, 12); }
  }

  // ===========================================================================
  //                               Compression
  // ===========================================================================

  /// <summary>
  /// Compresses one cluster. Returns the payload (4-byte method header + token
  /// stream, padded to a 2-byte word), or <c>null</c> if it would not be smaller
  /// than the raw cluster (caller stores raw instead).
  /// </summary>
  public static byte[]? Compress(ReadOnlySpan<byte> data, CvfLzMethod method, int level) {
    var bytes = Encode(data, method, level);
    if (bytes is null) return null;
    return bytes.Length < data.Length ? bytes : null;
  }

  /// <summary>Encodes a cluster with the given method, always returning the
  /// payload (4-byte header + token stream), or <c>null</c> for
  /// <see cref="CvfLzMethod.Stored"/> / unsupported methods. The caller decides
  /// whether the result fits the cluster's sector budget.</summary>
  public static byte[]? Encode(ReadOnlySpan<byte> data, CvfLzMethod method, int level) {
    switch (method) {
      case CvfLzMethod.Ds: return CompressDs(data, level);
      case CvfLzMethod.Jm: return CompressJm(data, level);
      case CvfLzMethod.Sq: return CompressSq(data);
      case CvfLzMethod.Auto:
        // Per-cluster best-of: each cluster carries its own method header, so we
        // pick whichever codec yields the smallest payload for this cluster.
        byte[]?[] cands = [CompressDs(data, level), CompressJm(data, level), CompressSq(data)];
        byte[]? best = null;
        foreach (var c in cands) if (c is not null && (best is null || c.Length < best.Length)) best = c;
        return best;
      default: return null;
    }
  }

  private static byte[] CompressDs(ReadOnlySpan<byte> data, int level) {
    var w = new BitWriter();
    w.Put(DsMagic, 16);
    w.Put(0, 16);                 // version 0 (DS-0-0)
    var src = data.ToArray();
    var pos = 0;
    while (pos < src.Length) {
      var (mlen, moff) = FindMatch(src, pos, 4415, 2, level);
      if (mlen >= 2) {
        if (moff <= 63) w.Put(0 | (moff << 2), 8);
        else if (moff <= 319) w.Put(3 | ((moff - 64) << 3), 11);
        else w.Put(7 | ((moff - 320) << 3), 15);
        PutLen(w, mlen + 1);      // DS: copy length = rdlen - 1
        pos += mlen;
      } else {
        var b = src[pos++];
        if (b < 128) w.Put(2 | (b << 2), 9);
        else w.Put(1 | ((b & 127) << 2), 9);
      }
    }
    w.Put(7, 3); w.Put(Sync - 320, 12);   // final sync
    return w.ToBytes();
  }

  private static byte[] CompressJm(ReadOnlySpan<byte> data, int level) {
    var w = new BitWriter();
    w.Put(JmMagic, 16);
    w.Put(0, 16);                 // version 0 (JM-0-0)
    var src = data.ToArray();
    var pos = 0;
    while (pos < src.Length) {
      var (mlen, moff) = FindMatch(src, pos, 4415, 3, level);
      if (mlen >= 3) {
        w.Put(1, 2);              // match token
        PutOffs(w, moff);
        PutLen(w, mlen);          // JM: copy length = rdlen
        pos += mlen;
      } else {
        var b = src[pos++];
        if (b < 128) w.Put(b << 1, 8);
        else w.Put(3 | ((b & 127) << 2), 9);
      }
    }
    w.Put(1, 2); w.Put(3, 2); w.Put(Sync - 320, 12);   // final sync (rdn(2)=1 + rdoffs 0x113f)
    return w.ToBytes();
  }

  // SQ-0-0 (DriveSpace 3 "Ultra") is, byte-for-byte, "SQ" + 16-bit version + a
  // raw RFC-1951 DEFLATE stream: dmsdos sq_dec's fixed-Huffman table, length and
  // distance base/extra tables, and bit-reversed LSB-first packing all match
  // DEFLATE exactly. So .NET's DeflateStream produces a genuine SQ body.
  private static byte[] CompressSq(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    ms.WriteByte(0x53); ms.WriteByte(0x51); ms.WriteByte(0); ms.WriteByte(0); // "SQ", version 0
    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      ds.Write(data);
    return ms.ToArray();
  }

  private static byte[] DecompressSq(byte[] payload, int inLen, int outLen) {
    using var ins = new MemoryStream(payload, 4, inLen - 4);
    using var ds = new DeflateStream(ins, CompressionMode.Decompress);
    var outp = new byte[outLen];
    var n = 0; int r;
    while (n < outLen && (r = ds.Read(outp, n, outLen - n)) > 0) n += r;
    return outp;
  }

  // Greedy longest-match finder. level scales the search depth (chain length).
  private static (int Len, int Off) FindMatch(byte[] src, int pos, int maxOff, int minLen, int level) {
    var bestLen = 0; var bestOff = 0;
    var start = Math.Max(0, pos - maxOff);
    var limit = Math.Min(MaxLen, src.Length - pos);
    if (limit < minLen) return (0, 0);
    var probes = level <= 0 ? 256 : level * 1024;
    for (var cand = pos - 1; cand >= start && probes-- > 0; cand--) {
      if (src[cand] != src[pos]) continue;
      var l = 1;
      while (l < limit && src[cand + l] == src[pos + l]) l++;
      if (l > bestLen) { bestLen = l; bestOff = pos - cand; if (l >= limit) break; }
    }
    return bestLen >= minLen ? (bestLen, bestOff) : (0, 0);
  }

  // ===========================================================================
  //                              Decompression
  // ===========================================================================

  /// <summary>Decompresses a cluster payload to exactly <paramref name="outLen"/> bytes.</summary>
  public static byte[] Decompress(byte[] payload, int inLen, int outLen) {
    var method = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
    var magic16 = (int)(method & 0xFFFF);
    if (magic16 == SqMagic) return DecompressSq(payload, inLen, outLen);
    var br = new BitReader(payload, inLen);
    var read = br.ReadN(16);
    if (read != (uint)magic16) throw new InvalidDataException($"CVF codec: bad magic 0x{read:X4}");
    br.ReadN(16); // version
    var outp = new byte[outLen];
    var p = 0;
    if (magic16 == DsMagic) DecodeDs(br, outp, ref p);
    else if (magic16 == JmMagic) DecodeJm(br, outp, ref p);
    else throw new InvalidDataException($"CVF codec: unsupported method 0x{method:X8}");
    return outp;
  }

  private static void DecodeDs(BitReader br, byte[] outp, ref int p) {
    while (p < outp.Length) {
      var u = br.Peek();
      switch (u & 3) {
        case 0: br.Skip(8); Copy(outp, ref p, (int)((u >> 2) & 63), br, -1); break;
        case 1: br.Skip(9); outp[p++] = (byte)((u >> 2) | 128); break;
        case 2: br.Skip(9); outp[p++] = (byte)((u >> 2) & 127); break;
        default:
          int off;
          if ((u & 4) != 0) { br.Skip(15); off = (int)((u >> 3) & 4095) + 320; }
          else { br.Skip(11); off = (int)((u >> 3) & 255) + 64; }
          Copy(outp, ref p, off, br, -1);
          break;
      }
    }
  }

  private static void DecodeJm(BitReader br, byte[] outp, ref int p) {
    while (p < outp.Length) {
      var u = br.Peek();
      switch (u & 3) {
        case 0: case 2: br.Skip(8); outp[p++] = (byte)((u >> 1) & 127); break;
        case 1: br.Skip(2); Copy(outp, ref p, br.RdOffs(), br, 0); break;
        default: br.Skip(9); outp[p++] = (byte)(((u >> 2) & 127) | 128); break;
      }
    }
  }

  private static void Copy(byte[] outp, ref int p, int off, BitReader br, int k) {
    if (off == Sync) return;          // sync token mid-stream (shouldn't fill)
    var len = br.RdLen() + k;
    var src = p - off;
    for (var i = 0; i < len && p < outp.Length; i++) outp[p++] = outp[src + i];
  }
}
