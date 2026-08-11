namespace FileFormat.BitRock;

/// <summary>
/// Clean-room DEFLATE inflater that follows BitRock / InstallBuilder's private
/// chunk framing so the application-payload tars decode past the point at which a
/// stock zlib/<see cref="System.IO.Compression.GZipStream"/> gives up.
///
/// <para><b>Recovered framing (by binary inspection of real installers).</b> A payload
/// component is a gzip member (<c>1f 8b 08 08</c> + FNAME = the tar's original name)
/// wrapping one long DEFLATE stream. The leading region is ordinary DEFLATE that any
/// decoder handles. Deeper in — wherever the tar embeds an already-compressed file, so
/// the encoder is emitting back-to-back <b>stored</b> blocks — the stream carries a
/// single stray <b>flush-marker byte</b> at a byte-aligned DEFLATE block boundary,
/// roughly every 256&#160;KiB of stored output. The marker byte's value is arbitrary and
/// its low three bits do <i>not</i> form a valid block header (observed 0xEA, 0x2C,
/// 0x5F, 0xB0, …), so a stock decoder aborts. Skipping exactly one byte re-aligns onto
/// the next block, which in these regions is a normal stored block whose 5-byte header
/// <c>00 &lt;LEN&#8322;&gt; &lt;~LEN&#8322;&gt;</c> (LEN&#160;≈&#160;0x8000) validates. The
/// 32&#160;KiB sliding window is preserved across the marker (it is not a
/// dictionary-resetting full flush; no <c>00 00 FF FF</c> sync marker precedes it).</para>
///
/// <para><b>Semantics.</b> This inflater therefore inflates block-by-block, keeping the
/// full window, and on an invalid block that begins on a byte boundary it discards that
/// one marker byte and resumes (bounded run of consecutive skips as a safety valve).
/// The stored-block <c>LEN == ~LEN</c> check is self-validating, so extended stored
/// regions are byte-exact. The private framing does not embed the encoder's exact
/// marker positions, and compressed (Huffman) blocks are not self-validating, so a
/// long stream can still eventually de-synchronise; downstream recovery is gated by the
/// TAR per-entry header checksum, which rejects any output produced after a
/// de-sync.</para>
/// </summary>
internal sealed class BitRockChunkedInflate {

  // DEFLATE (RFC 1951) length/distance base + extra-bit tables.
  private static readonly int[] LenBase = [
    3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
  private static readonly int[] LenExtra = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
  private static readonly int[] DistBase = [
    1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769,
    1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
  private static readonly int[] DistExtra = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
  private static readonly int[] ClcOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

  /// <summary>Raised when a DEFLATE block cannot be decoded (used to trigger marker resync).</summary>
  private sealed class BlockException : Exception;

  private readonly byte[] _in;
  private int _bytePos;
  private int _bit;
  private readonly int _end;

  private byte[] _out;
  private int _outLen;
  private readonly int _cap;

  private BitRockChunkedInflate(byte[] input, int start, int end, int cap) {
    this._in = input;
    this._bytePos = start;
    this._end = end;
    this._cap = cap;
    this._out = new byte[Math.Min(1 << 20, cap + 16)];
  }

  /// <summary>
  /// Decodes the payload of a gzip member at <paramref name="memberOffset"/> in
  /// <paramref name="data"/>, following the flush-marker framing. Stops at a final
  /// block, at <paramref name="maxDecoded"/>, or when the stream can no longer be
  /// re-aligned. <paramref name="markerSkips"/> reports how many marker bytes were
  /// discarded; <paramref name="cleanEnd"/> is true when a final DEFLATE block was
  /// reached without any forced stop.
  /// </summary>
  public static byte[] Decode(
      byte[] data, long memberOffset, int maxDecoded, out int markerSkips, out bool cleanEnd) {
    markerSkips = 0;
    cleanEnd = false;

    var deflateStart = GzipDeflateOffset(data, memberOffset, out var ok);
    if (!ok)
      return [];

    var self = new BitRockChunkedInflate(data, deflateStart, data.Length, maxDecoded);
    return self.Run(out markerSkips, out cleanEnd);
  }

  private byte[] Run(out int markerSkips, out bool cleanEnd) {
    markerSkips = 0;
    cleanEnd = false;
    var consecutiveSkips = 0;
    const int MaxConsecutiveSkips = 4;

    while (this._bytePos < this._end - 1 && this._outLen < this._cap) {
      var saveByte = this._bytePos;
      var saveBit = this._bit;
      var saveLen = this._outLen;
      try {
        var bfinal = this.DecodeOneBlock();
        consecutiveSkips = 0;
        if (bfinal) {
          cleanEnd = true;
          break;
        }
      } catch (BlockException) {
        // A flush marker only ever sits on a byte boundary; only there can we discard it.
        if (saveBit != 0 || consecutiveSkips >= MaxConsecutiveSkips)
          break;
        this._outLen = saveLen;          // roll back any partial output from the failed block
        this._bytePos = saveByte + 1;    // drop the one marker byte
        this._bit = 0;
        ++consecutiveSkips;
        ++markerSkips;
      } catch (IndexOutOfRangeException) {
        this._outLen = saveLen;
        break;
      }
    }

    var result = new byte[this._outLen];
    Array.Copy(this._out, result, this._outLen);
    return result;
  }

  // ── DEFLATE core ─────────────────────────────────────────────────────────────

  private bool DecodeOneBlock() {
    var bfinal = this.ReadBit();
    var btype = this.ReadBits(2);
    switch (btype) {
      case 0: this.DecodeStored(); break;
      case 1: this.DecodeHuffman(FixedLitLen, FixedDist); break;
      case 2: this.DecodeDynamic(); break;
      default: throw new BlockException();     // btype 3 is reserved → treat as marker desync
    }
    return bfinal != 0;
  }

  private void DecodeStored() {
    if (this._bit != 0) { this._bit = 0; ++this._bytePos; }
    if (this._bytePos + 4 > this._end)
      throw new BlockException();
    var len = this._in[this._bytePos] | (this._in[this._bytePos + 1] << 8);
    var nlen = this._in[this._bytePos + 2] | (this._in[this._bytePos + 3] << 8);
    this._bytePos += 4;
    if ((len ^ 0xffff) != nlen)
      throw new BlockException();
    if (this._bytePos + len > this._end)
      throw new BlockException();
    this.Ensure(len);
    Array.Copy(this._in, this._bytePos, this._out, this._outLen, len);
    this._outLen += len;
    this._bytePos += len;
  }

  private void DecodeDynamic() {
    var hlit = this.ReadBits(5) + 257;
    var hdist = this.ReadBits(5) + 1;
    var hclen = this.ReadBits(4) + 4;
    if (hlit > 286 || hdist > 30)
      throw new BlockException();

    var clcLengths = new int[19];
    for (var i = 0; i < hclen; ++i)
      clcLengths[ClcOrder[i]] = this.ReadBits(3);
    var clcTable = new HuffmanTable(clcLengths);

    var lengths = new int[hlit + hdist];
    var n = 0;
    while (n < lengths.Length) {
      var sym = clcTable.Decode(this);
      switch (sym) {
        case < 16:
          lengths[n++] = sym;
          break;
        case 16: {
          if (n == 0) throw new BlockException();
          var rep = this.ReadBits(2) + 3;
          var prev = lengths[n - 1];
          while (rep-- > 0 && n < lengths.Length) lengths[n++] = prev;
          break;
        }
        case 17: {
          var rep = this.ReadBits(3) + 3;
          while (rep-- > 0 && n < lengths.Length) lengths[n++] = 0;
          break;
        }
        default: {
          var rep = this.ReadBits(7) + 11;
          while (rep-- > 0 && n < lengths.Length) lengths[n++] = 0;
          break;
        }
      }
    }

    var litLen = new int[hlit];
    var dist = new int[hdist];
    Array.Copy(lengths, 0, litLen, 0, hlit);
    Array.Copy(lengths, hlit, dist, 0, hdist);
    this.DecodeHuffman(new HuffmanTable(litLen), new HuffmanTable(dist));
  }

  private void DecodeHuffman(HuffmanTable litLen, HuffmanTable dist) {
    while (true) {
      var sym = litLen.Decode(this);
      if (sym < 256) {
        this.Ensure(1);
        this._out[this._outLen++] = (byte)sym;
        if (this._outLen >= this._cap)
          return;
        continue;
      }
      if (sym == 256)
        return;
      if (sym > 285)
        throw new BlockException();
      var length = LenBase[sym - 257] + this.ReadBits(LenExtra[sym - 257]);
      var dsym = dist.Decode(this);
      if (dsym > 29)
        throw new BlockException();
      var distance = DistBase[dsym] + this.ReadBits(DistExtra[dsym]);
      if (distance > this._outLen)
        throw new BlockException();
      this.Ensure(length);
      var src = this._outLen - distance;
      for (var i = 0; i < length; ++i)
        this._out[this._outLen + i] = this._out[src + i];
      this._outLen += length;
      if (this._outLen >= this._cap)
        return;
    }
  }

  // ── bit / buffer helpers ───────────────────────────────────────────────────────

  internal int ReadBit() {
    if (this._bytePos >= this._end)
      throw new BlockException();
    var v = (this._in[this._bytePos] >> this._bit) & 1;
    if (++this._bit == 8) { this._bit = 0; ++this._bytePos; }
    return v;
  }

  internal int ReadBits(int count) {
    var v = 0;
    for (var i = 0; i < count; ++i)
      v |= this.ReadBit() << i;
    return v;
  }

  private void Ensure(int extra) {
    if (this._outLen + extra <= this._out.Length)
      return;
    var want = Math.Max(this._out.Length * 2, this._outLen + extra + 16);
    Array.Resize(ref this._out, want);
  }

  // ── gzip header ────────────────────────────────────────────────────────────────

  private static int GzipDeflateOffset(byte[] data, long memberOffset, out bool ok) {
    ok = false;
    var p = (int)memberOffset;
    if (p < 0 || p + 10 > data.Length || data[p] != 0x1f || data[p + 1] != 0x8b || data[p + 2] != 0x08)
      return 0;
    var flg = data[p + 3];
    p += 10;
    if ((flg & 0x04) != 0) {                 // FEXTRA
      if (p + 2 > data.Length) return 0;
      var xlen = data[p] | (data[p + 1] << 8);
      p += 2 + xlen;
    }
    if ((flg & 0x08) != 0)                    // FNAME
      p = SkipCString(data, p);
    if ((flg & 0x10) != 0)                    // FCOMMENT
      p = SkipCString(data, p);
    if ((flg & 0x02) != 0)                    // FHCRC
      p += 2;
    if (p < 0 || p >= data.Length)
      return 0;
    ok = true;
    return p;
  }

  private static int SkipCString(byte[] data, int p) {
    while (p < data.Length && data[p] != 0)
      ++p;
    return p + 1;
  }

  // ── canonical Huffman ────────────────────────────────────────────────────────────

  private static readonly HuffmanTable FixedLitLen = BuildFixedLitLen();
  private static readonly HuffmanTable FixedDist = BuildFixedDist();

  private static HuffmanTable BuildFixedLitLen() {
    var l = new int[288];
    for (var i = 0; i < 144; ++i) l[i] = 8;
    for (var i = 144; i < 256; ++i) l[i] = 9;
    for (var i = 256; i < 280; ++i) l[i] = 7;
    for (var i = 280; i < 288; ++i) l[i] = 8;
    return new HuffmanTable(l);
  }

  private static HuffmanTable BuildFixedDist() {
    var l = new int[30];
    for (var i = 0; i < 30; ++i) l[i] = 5;
    return new HuffmanTable(l);
  }

  /// <summary>Canonical Huffman decode table built from a per-symbol code-length list.</summary>
  private sealed class HuffmanTable {
    private readonly int _maxLen;
    private readonly Dictionary<int, int> _codes = [];   // key = (len << 16) | code

    public HuffmanTable(int[] lengths) {
      var max = 0;
      foreach (var l in lengths)
        if (l > max) max = l;
      this._maxLen = max;
      if (max == 0)
        return;

      var blCount = new int[max + 1];
      foreach (var l in lengths)
        if (l != 0) ++blCount[l];

      var nextCode = new int[max + 1];
      var code = 0;
      for (var bits = 1; bits <= max; ++bits) {
        code = (code + blCount[bits - 1]) << 1;
        nextCode[bits] = code;
      }
      for (var sym = 0; sym < lengths.Length; ++sym) {
        var len = lengths[sym];
        if (len == 0)
          continue;
        this._codes[(len << 16) | nextCode[len]] = sym;
        ++nextCode[len];
      }
    }

    public int Decode(BitRockChunkedInflate br) {
      if (this._maxLen == 0)
        throw new BlockException();
      var code = 0;
      for (var len = 1; len <= this._maxLen; ++len) {
        code = (code << 1) | br.ReadBit();
        if (this._codes.TryGetValue((len << 16) | code, out var sym))
          return sym;
      }
      throw new BlockException();
    }
  }
}
