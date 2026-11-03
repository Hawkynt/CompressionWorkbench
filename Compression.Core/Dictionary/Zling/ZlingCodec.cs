namespace Compression.Core.Dictionary.Zling;

/// <summary>
/// zling, Zhang Li's ROLZ + Huffman compressor, converted from libzling
/// (https://github.com/richox/libzling, revision <c>6828e31517bf94fda8e1e8e15a583ab58acde637</c>,
/// BSD-3-Clause; see <c>THIRD-PARTY-NOTICE.libzling.txt</c>). Output matches the reference's
/// <c>zling_demo e0</c>..<c>e4</c> byte for byte when it is built with libstdc++.
/// </summary>
/// <remarks>
/// <para>A zling stream is a sequence of input blocks of up to 16 MiB. Each block is a series of
/// sub-blocks, each introduced by the byte 1 and closed by the byte 0 after the last: three
/// big-endian 32-bit values (input position reached, symbol count, payload length) and a payload
/// of two nibble-packed Huffman length tables (514 literal/length symbols, 32 index classes)
/// followed by the symbols, least significant bit first.</para>
/// <para>Symbols: 0..255 are bytes after a per-context move-to-front step, 256 and 257 repeat the
/// two most recent byte pairs seen after the current byte, 258..513 are matches of 4..259 bytes
/// followed by an index into the 4096 most recent positions that share the previous byte (ROLZ).
/// The first two bytes of a block are stored as they are.</para>
/// </remarks>
public static class ZlingCodec {
  private const int BucketItemSize = 4096;
  private const int BucketItemHash = 8192;
  private const int MatchMinLenEnableLazy = 128;
  private const int MatchMinLen = 4;
  private const int MatchMaxLen = 259;
  private const int HuffmanCodes1 = 258 + (MatchMaxLen - MatchMinLen + 1);
  private const int HuffmanCodes2 = 32;
  private const int HuffmanMaxLen1 = 15;
  private const int HuffmanMaxLen2 = 8;
  private const int BlockSizeIn = 16777216;
  private const int BlockSizeRolz = 262144;
  private const int BlockSizeHuffman = 393216;
  private const int SentinelLen = MatchMaxLen + 16;
  private const byte FlagContinue = 1;
  private const byte FlagStop = 0;
  private const ushort Empty = 65535;

  /// <summary>Default level, the reference CLI's <c>zling e</c>.</summary>
  public const int DefaultLevel = 0;

  /// <summary>Highest level, <c>zling e4</c>.</summary>
  public const int MaxLevel = 4;

  private static readonly int[] MatchIdxBase = [0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1280, 1536, 1792, 2048, 2304, 2560, 2816, 3072, 3328, 3584, 3840];
  private static readonly int[] MatchIdxBitLen = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8];
  private static readonly byte[] MatchIdxCode = BuildMatchIdxCode();

  private static readonly byte[] MtfInit = [
    32, 101, 116, 97, 105, 111, 110, 114, 115, 108, 104, 100, 99, 117, 93, 91, 109, 112, 103, 102, 10, 121, 98, 39, 119, 46, 44, 118, 59, 38, 124, 47,
    49, 107, 61, 48, 67, 65, 58, 45, 84, 83, 60, 62, 50, 113, 73, 57, 42, 120, 41, 40, 66, 77, 80, 69, 68, 53, 51, 72, 70, 56, 52, 71,
    82, 54, 76, 55, 78, 87, 122, 125, 123, 79, 106, 85, 74, 75, 208, 95, 195, 35, 86, 215, 90, 34, 89, 209, 128, 224, 184, 131, 92, 227, 37, 33,
    176, 169, 206, 226, 130, 63, 88, 81, 161, 153, 43, 129, 188, 179, 216, 164, 181, 189, 148, 190, 173, 187, 186, 229, 225, 167, 217, 177, 178, 168, 149, 185,
    197, 144, 147, 196, 207, 194, 180, 156, 132, 170, 166, 136, 182, 191, 9, 230, 141, 160, 175, 36, 152, 140, 165, 145, 94, 133, 163, 183, 171, 157, 137, 174,
    134, 135, 236, 151, 231, 155, 201, 158, 138, 143, 150, 162, 159, 139, 172, 154, 126, 232, 235, 146, 233, 228, 202, 203, 142, 214, 237, 204, 219, 234, 213, 96,
    218, 199, 64, 210, 239, 198, 211, 205, 212, 240, 222, 220, 200, 0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, 17, 18,
    19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 127, 192, 193, 221, 223, 238, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255,
  ];

  private static readonly byte[] MtfNext = [
    0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
    30, 31, 32, 33, 34, 35, 36, 37, 38, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 57,
    58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86,
    87, 88, 89, 90, 91, 92, 93, 94, 95, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 114, 115, 116,
    117, 118, 119, 120, 70, 70, 71, 72, 72, 73, 73, 74, 74, 75, 75, 76, 77, 77, 78, 78, 79, 79, 80, 80, 81, 81, 82, 83, 83, 84, 84, 85,
    85, 86, 86, 87, 88, 88, 89, 89, 90, 90, 91, 91, 92, 92, 93, 94, 94, 95, 95, 96, 96, 97, 97, 98, 99, 99, 100, 100, 101, 101, 102, 102,
    103, 103, 104, 105, 105, 106, 106, 107, 107, 108, 108, 109, 110, 110, 111, 111, 112, 112, 113, 113, 114, 114, 115, 116, 116, 117, 117, 118, 118, 119, 119, 120,
    121, 121, 122, 122, 123, 123, 124, 124, 125, 125, 126, 127, 127, 128, 128, 129, 129, 130, 130, 131, 132, 132, 133, 133, 134, 134, 135, 135, 136, 136, 137, 138,
    138, 139, 139, 140,
  ];

  private static byte[] BuildMatchIdxCode() {
    var codes = new byte[BucketItemSize];
    for (var code = 0; code < HuffmanCodes2; ++code)
      for (var i = 0; i < 1 << MatchIdxBitLen[code]; ++i)
        codes[MatchIdxBase[code] + i] = (byte)code;
    return codes;
  }

  // ── Compression ──────────────────────────────────────────────────────────

  /// <summary>Compresses <paramref name="data"/> into a zling stream.</summary>
  /// <param name="data">The bytes to compress.</param>
  /// <param name="level">0 (<c>e0</c>, fastest) to 4 (<c>e4</c>).</param>
  /// <returns>The zling stream; empty input gives an empty stream, as with the reference.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, int level = DefaultLevel) {
    ArgumentOutOfRangeException.ThrowIfNegative(level);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxLevel);
    using var output = new MemoryStream(data.Length / 2 + 16);
    var encoder = new RolzEncoder();
    var ibuf = new byte[Math.Min(data.Length, BlockSizeIn) + SentinelLen];
    var tbuf = new ushort[BlockSizeRolz + SentinelLen];
    var obuf = new byte[BlockSizeHuffman + SentinelLen];
    var currentLevel = level;
    Span<byte> word = stackalloc byte[4];

    for (var start = 0; start < data.Length; start += BlockSizeIn) {
      var ilen = Math.Min(BlockSizeIn, data.Length - start);
      data.Slice(start, ilen).CopyTo(ibuf);
      encoder.Reset();
      var encpos = 0;
      while (encpos < ilen) {
        output.WriteByte(FlagContinue);
        var encposOld = encpos;
        var rlen = encoder.Encode(currentLevel, ibuf, tbuf, ilen, BlockSizeRolz, ref encpos);
        var olen = Huffman.Encode(tbuf, rlen, obuf);

        // The reference drops to level 0 for data that does not compress.
        currentLevel = 1.0 * olen / (encpos - encposOld + 1) > 0.95 ? 0 : level;

        WriteUInt32BigEndian(output, (uint)encpos);
        WriteUInt32BigEndian(output, (uint)rlen);
        WriteUInt32BigEndian(output, (uint)olen);
        output.Write(obuf, 0, olen);
      }

      output.WriteByte(FlagStop);
    }

    return output.ToArray();
  }

  private static void WriteUInt32BigEndian(Stream output, uint value) {
    output.WriteByte((byte)(value >> 24));
    output.WriteByte((byte)(value >> 16));
    output.WriteByte((byte)(value >> 8));
    output.WriteByte((byte)value);
  }

  // ── Decompression ────────────────────────────────────────────────────────

  /// <summary>Decompresses a zling stream.</summary>
  /// <param name="data">The whole stream.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">The stream is truncated or corrupt.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream(data.Length * 3 + 16);
    var decoder = new RolzDecoder();
    byte[]? ibuf = null;
    var tbuf = new ushort[BlockSizeRolz + SentinelLen];
    var pos = 0;
    while (pos < data.Length) {
      decoder.Reset();
      var decpos = 0;
      while (pos < data.Length) {
        var flag = data[pos++];
        if (flag != FlagStop && flag != FlagContinue)
          throw new InvalidDataException("zling: invalid block flag.");
        if (flag == FlagStop)
          break;
        if (pos + 12 > data.Length)
          throw new InvalidDataException("zling: truncated block header.");
        var encpos = ReadUInt32BigEndian(data[pos..]);
        var rlen = ReadUInt32BigEndian(data[(pos + 4)..]);
        var olen = ReadUInt32BigEndian(data[(pos + 8)..]);
        pos += 12;
        if (rlen > BlockSizeRolz || olen > BlockSizeHuffman || encpos > BlockSizeIn)
          throw new InvalidDataException("zling: invalid block size.");
        if (pos + (int)olen > data.Length)
          throw new InvalidDataException("zling: truncated block.");

        Huffman.Decode(data.Slice(pos, (int)olen), tbuf, (int)rlen);
        pos += (int)olen;
        ibuf ??= new byte[BlockSizeIn + SentinelLen];
        decoder.Decode(tbuf, ibuf, (int)rlen, (int)encpos, ref decpos);
      }

      if (ibuf != null)
        output.Write(ibuf, 0, decpos);
    }

    return output.ToArray();
  }

  private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data)
    => (uint)data[0] << 24 | (uint)data[1] << 16 | (uint)data[2] << 8 | data[3];

  // ── ROLZ ─────────────────────────────────────────────────────────────────

  private static uint HashContext(byte[] buf, int pos)
    => (uint)(buf[pos] | buf[pos + 1] << 8 | buf[pos + 2] << 16 | buf[pos + 3] << 24) + buf[pos + 2] * 137u + buf[pos + 3] * 13337u;

  private static uint Read32(byte[] buf, int pos) => (uint)(buf[pos] | buf[pos + 1] << 8 | buf[pos + 2] << 16 | buf[pos + 3] << 24);

  private static int GetCommonLength(byte[] buf, int p1, int p2, int maxlen) {
    if (Read32(buf, p1) != Read32(buf, p2))
      return 0;
    var start = p1;
    while (maxlen >= 4 && Read32(buf, p1) == Read32(buf, p2)) {
      p1 += 4;
      p2 += 4;
      maxlen -= 4;
    }

    if (maxlen >= 2 && buf[p1] == buf[p2] && buf[p1 + 1] == buf[p2 + 1]) {
      p1 += 2;
      p2 += 2;
      maxlen -= 2;
    }

    if (maxlen >= 1 && buf[p1] == buf[p2])
      ++p1;
    return p1 - start;
  }

  private sealed class Mtf {
    private readonly byte[] _table = (byte[])MtfInit.Clone();
    private readonly byte[] _index = new byte[256];

    public Mtf() {
      for (var i = 0; i < 256; ++i)
        this._index[this._table[i]] = (byte)i;
    }

    public byte Encode(byte c) {
      var i = this._index[c];
      var next = MtfNext[i];
      (this._index[c], this._index[this._table[next]]) = (this._index[this._table[next]], this._index[c]);
      (this._table[i], this._table[next]) = (this._table[next], this._table[i]);
      return i;
    }

    public byte Decode(byte i) {
      var c = this._table[i];
      var next = MtfNext[i];
      (this._table[i], this._table[next]) = (this._table[next], this._table[i]);
      return c;
    }
  }

  private sealed class RolzEncoder {
    private readonly ushort[][] _suffix = new ushort[256][];
    private readonly uint[][] _offset = new uint[256][];
    private readonly ushort[][] _hash = new ushort[256][];
    private readonly int[] _head = new int[256];
    private readonly Mtf[] _mtf = new Mtf[256];

    public RolzEncoder() {
      for (var c = 0; c < 256; ++c) {
        this._suffix[c] = new ushort[BucketItemSize];
        this._offset[c] = new uint[BucketItemSize];
        this._hash[c] = new ushort[BucketItemHash];
        this._mtf[c] = new Mtf();
      }
    }

    public void Reset() {
      for (var c = 0; c < 256; ++c) {
        this._offset[c].AsSpan().Clear();
        this._suffix[c].AsSpan().Fill(Empty);
        this._hash[c].AsSpan().Fill(Empty);
        this._head[c] = 0;
      }
    }

    public int Encode(int level, byte[] ibuf, ushort[] obuf, int ilen, int olen, ref int encpos) => level switch {
      0 => this.EncodeImpl(2, 1, 0, ibuf, obuf, ilen, olen, ref encpos),
      1 => this.EncodeImpl(4, 1, 0, ibuf, obuf, ilen, olen, ref encpos),
      2 => this.EncodeImpl(6, 2, 0, ibuf, obuf, ilen, olen, ref encpos),
      3 => this.EncodeImpl(8, 3, 1, ibuf, obuf, ilen, olen, ref encpos),
      _ => this.EncodeImpl(16, 4, 2, ibuf, obuf, ilen, olen, ref encpos),
    };

    private int EncodeImpl(int matchDepth, int lazy1, int lazy2, byte[] ibuf, ushort[] obuf, int ilen, int olen, ref int encpos) {
      var ipos = encpos;
      var opos = 0;
      var wordMru = new ushort[256, 2];

      if (ipos == 0 && opos < olen && ipos < ilen)
        obuf[opos++] = ibuf[ipos++];
      if (ipos == 1 && opos < olen && ipos < ilen)
        obuf[opos++] = ibuf[ipos++];

      while (opos + 1 < olen && ipos < ilen) {
        if (ipos + MatchMaxLen + 16 < ilen && this.MatchAndUpdate(matchDepth, lazy1, lazy2, ibuf, ipos, out var matchIdx, out var matchLen)) {
          obuf[opos++] = (ushort)(258 + matchLen - MatchMinLen);
          obuf[opos++] = (ushort)matchIdx;
          ipos += matchLen;
          var ctx = ibuf[ipos - 3];
          var pair = (ushort)(ibuf[ipos - 2] << 8 | ibuf[ipos - 1]);
          if (wordMru[ctx, 0] != pair) {
            wordMru[ctx, 1] = wordMru[ctx, 0];
            wordMru[ctx, 0] = pair;
          }

          continue;
        }

        if (ipos + 1 < ilen) {
          var pair = (ushort)(ibuf[ipos] << 8 | ibuf[ipos + 1]);
          var ctx = ibuf[ipos - 1];
          if (wordMru[ctx, 0] == pair) {
            obuf[opos++] = 256;
            ipos += 2;
            continue;
          }

          if (wordMru[ctx, 1] == pair) {
            obuf[opos++] = 257;
            ipos += 2;
            wordMru[ctx, 1] = wordMru[ctx, 0];
            wordMru[ctx, 0] = pair;
            continue;
          }
        }

        obuf[opos++] = this._mtf[ibuf[ipos - 1]].Encode(ibuf[ipos]);
        ++ipos;
        var context = ibuf[ipos - 3];
        wordMru[context, 1] = wordMru[context, 0];
        wordMru[context, 0] = (ushort)(ibuf[ipos - 2] << 8 | ibuf[ipos - 1]);
      }

      encpos = ipos;
      return opos;
    }

    private bool MatchAndUpdate(int matchDepth, int lazy1, int lazy2, byte[] buf, int pos, out int matchIdx, out int matchLen) {
      matchIdx = matchLen = 0;
      var maxlen = MatchMinLen - 1;
      var maxnode = 0;
      var hash = HashContext(buf, pos);
      var hashCheck = (byte)(hash / BucketItemHash % 256);
      var hashContext = (int)(hash % BucketItemHash);

      var bucket = buf[pos - 1];
      var suffix = this._suffix[bucket];
      var offsets = this._offset[bucket];
      var heads = this._hash[bucket];
      int node = heads[hashContext];

      var head = this._head[bucket] = (this._head[bucket] + 1) & (BucketItemSize - 1);
      suffix[head] = heads[hashContext];
      offsets[head] = (uint)pos | (uint)hashCheck << 24;
      heads[hashContext] = (ushort)head;

      if (node == Empty || node == head)
        return false;

      for (var i = 0; i < matchDepth; ++i) {
        var offset = (int)(offsets[node] & 0xFFFFFF);
        var check = (byte)(offsets[node] >> 24);
        if (check == hashCheck && buf[pos + maxlen] == buf[offset + maxlen]) {
          var len = GetCommonLength(buf, pos, offset, MatchMaxLen);
          if (len > maxlen) {
            maxnode = node;
            maxlen = len;
            if (maxlen == MatchMaxLen)
              break;
          }
        }

        node = suffix[node];
        if (node == Empty || offset <= (int)(offsets[node] & 0xFFFFFF))
          break;
      }

      if (maxlen < MatchMinLen)
        return false;
      if (maxlen < MatchMinLenEnableLazy) {
        if (lazy1 > 0 && this.MatchLazy(buf, pos + 1, maxlen, lazy1))
          return false;
        if (lazy2 > 0 && this.MatchLazy(buf, pos + 2, maxlen, lazy2))
          return false;
      }

      matchLen = maxlen;
      matchIdx = (head - maxnode) & (BucketItemSize - 1);
      return true;
    }

    private bool MatchLazy(byte[] buf, int pos, int maxlen, int depth) {
      var bucket = buf[pos - 1];
      var suffix = this._suffix[bucket];
      var offsets = this._offset[bucket];
      var hashContext = (int)(HashContext(buf, pos) % BucketItemHash);
      int node = this._hash[bucket][hashContext];
      if (node == Empty)
        return false;
      maxlen -= 3;
      for (var i = 0; i < depth; ++i) {
        var offset = (int)(offsets[node] & 0xFFFFFF);
        if (Read32(buf, pos + maxlen) == Read32(buf, offset + maxlen))
          return true;
        node = suffix[node];
        if (node == Empty || offset <= (int)(offsets[node] & 0xFFFFFF))
          break;
      }

      return false;
    }
  }

  private sealed class RolzDecoder {
    private readonly int[][] _offset = new int[256][];
    private readonly int[] _head = new int[256];
    private readonly Mtf[] _mtf = new Mtf[256];

    public RolzDecoder() {
      for (var c = 0; c < 256; ++c) {
        this._offset[c] = new int[BucketItemSize];
        this._mtf[c] = new Mtf();
      }
    }

    public void Reset() {
      for (var c = 0; c < 256; ++c) {
        this._offset[c].AsSpan().Clear();
        this._head[c] = 0;
      }
    }

    private int GetMatchAndUpdate(byte[] buf, int pos, int idx) {
      var bucket = buf[pos - 1];
      var head = this._head[bucket] = (this._head[bucket] + 1) & (BucketItemSize - 1);
      this._offset[bucket][head] = pos;
      return this._offset[bucket][(head - idx) & (BucketItemSize - 1)];
    }

    public void Decode(ushort[] ibuf, byte[] obuf, int ilen, int encpos, ref int decpos) {
      var opos = decpos;
      var ipos = 0;
      var wordMru = new ushort[256, 2];

      if (opos == 0 && ipos < ilen)
        obuf[opos++] = Literal(ibuf[ipos++]);
      if (opos == 1 && ipos < ilen)
        obuf[opos++] = Literal(ibuf[ipos++]);

      while (ipos < ilen) {
        var symbol = ibuf[ipos];
        if (opos + (symbol is 256 or 257 ? 2 : 1) > encpos)
          throw new InvalidDataException("zling: block decodes past its declared position.");
        if (symbol < 256) {
          obuf[opos] = this._mtf[obuf[opos - 1]].Decode((byte)symbol);
          ++ipos;
          this.GetMatchAndUpdate(obuf, opos++, 0);
          UpdateMru(wordMru, obuf, opos);
        } else if (symbol == 256) {
          var word = wordMru[obuf[opos - 1], 0];
          ++ipos;
          obuf[opos] = (byte)(word >> 8);
          this.GetMatchAndUpdate(obuf, opos++, 0);
          obuf[opos++] = (byte)word;
        } else if (symbol == 257) {
          var word = wordMru[obuf[opos - 1], 1];
          ++ipos;
          obuf[opos] = (byte)(word >> 8);
          this.GetMatchAndUpdate(obuf, opos++, 0);
          obuf[opos++] = (byte)word;
          UpdateMru(wordMru, obuf, opos);
        } else {
          var matchLen = ibuf[ipos++] - 258 + MatchMinLen;
          if (ipos >= ilen)
            throw new InvalidDataException("zling: match without an index.");
          var matchIdx = ibuf[ipos++];
          var matchOffset = this.GetMatchAndUpdate(obuf, opos, matchIdx);
          if (matchOffset >= opos || opos + matchLen > encpos)
            throw new InvalidDataException("zling: match points outside the decoded data.");
          for (var i = 0; i < matchLen; ++i)
            obuf[opos + i] = obuf[matchOffset + i];
          opos += matchLen;
          var ctx = obuf[opos - 3];
          var pair = (ushort)(obuf[opos - 2] << 8 | obuf[opos - 1]);
          if (wordMru[ctx, 0] != pair) {
            wordMru[ctx, 1] = wordMru[ctx, 0];
            wordMru[ctx, 0] = pair;
          }
        }

        if (opos > encpos)
          throw new InvalidDataException("zling: block decodes past its declared position.");
      }

      if (opos != encpos)
        throw new InvalidDataException("zling: block does not end at its declared position.");
      decpos = opos;
    }

    private static byte Literal(ushort symbol)
      => symbol < 256 ? (byte)symbol : throw new InvalidDataException("zling: a block must start with plain bytes.");

    private static void UpdateMru(ushort[,] wordMru, byte[] obuf, int opos) {
      var ctx = obuf[opos - 3];
      wordMru[ctx, 1] = wordMru[ctx, 0];
      wordMru[ctx, 0] = (ushort)(obuf[opos - 2] << 8 | obuf[opos - 1]);
    }
  }

  // ── Huffman ──────────────────────────────────────────────────────────────

  private static class Huffman {
    public static int Encode(ushort[] tbuf, int rlen, byte[] obuf) {
      var freq1 = new uint[HuffmanCodes1];
      var freq2 = new uint[HuffmanCodes2];
      for (var i = 0; i < rlen; ++i) {
        ++freq1[tbuf[i]];
        if (tbuf[i] >= 258)
          ++freq2[MatchIdxCode[tbuf[++i]]];
      }

      var length1 = MakeLengthTable(freq1, HuffmanCodes1, HuffmanMaxLen1);
      var length2 = MakeLengthTable(freq2, HuffmanCodes2, HuffmanMaxLen2);
      var encode1 = MakeEncodeTable(length1, HuffmanCodes1, HuffmanMaxLen1);
      var encode2 = MakeEncodeTable(length2, HuffmanCodes2, HuffmanMaxLen2);

      var opos = 0;
      for (var i = 0; i < HuffmanCodes1; i += 2)
        obuf[opos++] = (byte)(length1[i] * 16 + length1[i + 1]);
      for (var i = 0; i < HuffmanCodes2; i += 2)
        obuf[opos++] = (byte)(length2[i] * 16 + length2[i + 1]);

      ulong buffer = 0;
      var length = 0;
      for (var i = 0; i < rlen; ++i) {
        var symbol = tbuf[i];
        buffer |= (ulong)encode1[symbol] << length;
        length += length1[symbol];
        if (symbol >= 258) {
          var index = tbuf[++i];
          var code = MatchIdxCode[index];
          buffer |= (ulong)encode2[code] << length;
          length += length2[code];
          buffer |= (ulong)(index - MatchIdxBase[code]) << length;
          length += MatchIdxBitLen[code];
        }

        if (length >= 32) {
          for (var b = 0; b < 4; ++b) {
            obuf[opos++] = (byte)buffer;
            buffer >>= 8;
          }

          length -= 32;
        }
      }

      while (length > 0) {
        obuf[opos++] = (byte)buffer;
        buffer >>= 8;
        length -= 8;
      }

      return opos;
    }

    public static void Decode(ReadOnlySpan<byte> payload, ushort[] tbuf, int rlen) {
      const int tableBytes = HuffmanCodes1 / 2 + HuffmanCodes2 / 2;
      if (payload.Length < tableBytes)
        throw new InvalidDataException("zling: block is shorter than its code tables.");
      var length1 = new int[HuffmanCodes1];
      var length2 = new int[HuffmanCodes2];
      var opos = 0;
      for (var i = 0; i < HuffmanCodes1; i += 2, ++opos) {
        length1[i] = payload[opos] / 16;
        length1[i + 1] = payload[opos] % 16;
      }

      for (var i = 0; i < HuffmanCodes2; i += 2, ++opos) {
        length2[i] = payload[opos] / 16;
        length2[i + 1] = payload[opos] % 16;
      }

      var encode1 = MakeEncodeTable(length1, HuffmanCodes1, HuffmanMaxLen1);
      var encode2 = MakeEncodeTable(length2, HuffmanCodes2, HuffmanMaxLen2);
      var decode1 = MakeDecodeTable(length1, encode1, HuffmanCodes1, HuffmanMaxLen1);
      var decode2 = MakeDecodeTable(length2, encode2, HuffmanCodes2, HuffmanMaxLen2);

      ulong buffer = 0;
      var length = 0;
      for (var i = 0; i < rlen; ++i) {
        if (length < 32) {
          for (var b = 0; b < 4; ++b, ++opos) {
            // The reference reads whole words; bytes past the payload never reach a symbol.
            buffer |= (ulong)(opos < payload.Length ? payload[opos] : 0) << length;
            length += 8;
          }
        }

        var symbol = decode1[(int)(buffer & ((1UL << HuffmanMaxLen1) - 1))];
        if (symbol >= HuffmanCodes1)
          throw new InvalidDataException("zling: invalid Huffman stream (bad code1).");
        buffer >>= length1[symbol];
        length -= length1[symbol];
        tbuf[i] = symbol;

        if (symbol >= 258) {
          var code = decode2[(int)(buffer & ((1UL << HuffmanMaxLen2) - 1))];
          if (code >= HuffmanCodes2)
            throw new InvalidDataException("zling: invalid Huffman stream (bad code2).");
          buffer >>= length2[code];
          length -= length2[code];
          var bits = (int)(buffer & ((1UL << MatchIdxBitLen[code]) - 1));
          buffer >>= MatchIdxBitLen[code];
          length -= MatchIdxBitLen[code];
          var index = MatchIdxBase[code] + bits;
          if (index >= BucketItemSize || ++i >= rlen)
            throw new InvalidDataException("zling: invalid Huffman stream (bad ex-bits).");
          tbuf[i] = (ushort)index;
        }

        if (length < 0)
          throw new InvalidDataException("zling: Huffman stream is truncated.");
      }
    }

    /// <summary>
    /// The reference's length-limited code lengths: a Huffman tree built with libstdc++'s
    /// <c>std::priority_queue</c> (so ties break the same way), its weights halved (rounding up)
    /// until no code is longer than <paramref name="maxCodeLength"/>.
    /// </summary>
    private static int[] MakeLengthTable(uint[] freq, int maxCodes, int maxCodeLength) {
      var lengths = new int[maxCodes];
      for (var scaling = 0; ; ++scaling) {
        Array.Clear(lengths);
        var heap = new List<Node>();
        for (var i = 0; i < maxCodes; ++i)
          if (freq[i] > 0)
            heap.Add(new Node(i, (int)((freq[i] + ((1u << scaling) - 1)) >> scaling), null, null));
        if (heap.Count == 0)
          return lengths;

        StlHeap.Make(heap);
        while (heap.Count > 1) {
          var min1 = StlHeap.Pop(heap);
          var min2 = StlHeap.Pop(heap);
          StlHeap.Push(heap, new Node(-1, min1.Weight + min2.Weight, min1, min2));
        }

        var stack = new Stack<(Node Node, int Depth)>();
        stack.Push((heap[0], 0));
        var longest = 0;
        while (stack.Count > 0) {
          var (node, depth) = stack.Pop();
          if (node.Id >= 0) {
            lengths[node.Id] = Math.Max(depth, 1);
            longest = Math.Max(longest, lengths[node.Id]);
          } else {
            stack.Push((node.Child2!, depth + 1));
            stack.Push((node.Child1!, depth + 1));
          }
        }

        if (longest <= maxCodeLength)
          return lengths;
      }
    }

    private static ushort[] MakeEncodeTable(int[] lengths, int maxCodes, int maxCodeLength) {
      var table = new ushort[maxCodes];
      var code = 0;
      for (var len = 1; len <= maxCodeLength; ++len) {
        for (var i = 0; i < maxCodes; ++i)
          if (lengths[i] == len)
            table[i] = (ushort)code++;
        code *= 2;
      }

      for (var i = 0; i < maxCodes; ++i) {
        uint v = table[i];
        v = (v & 0xFF00) >> 8 | (v & 0x00FF) << 8;
        v = (v & 0xF0F0) >> 4 | (v & 0x0F0F) << 4;
        v = (v & 0xCCCC) >> 2 | (v & 0x3333) << 2;
        v = (v & 0xAAAA) >> 1 | (v & 0x5555) << 1;
        table[i] = (ushort)((ushort)v >> (16 - lengths[i]));
      }

      return table;
    }

    private static ushort[] MakeDecodeTable(int[] lengths, ushort[] encode, int maxCodes, int maxCodeLength) {
      var table = new ushort[1 << maxCodeLength];
      table.AsSpan().Fill(Empty);
      for (var c = 0; c < maxCodes; ++c)
        if (lengths[c] > 0 && lengths[c] <= maxCodeLength)
          for (var i = (int)encode[c]; i < 1 << maxCodeLength; i += 1 << lengths[c])
            table[i] = (ushort)c;
      return table;
    }

    private sealed record Node(int Id, int Weight, Node? Child1, Node? Child2);

    /// <summary>libstdc++'s binary-heap algorithms with <c>a.Weight &gt; b.Weight</c> as "less".</summary>
    private static class StlHeap {
      private static bool Less(Node a, Node b) => a.Weight > b.Weight;

      public static void Make(List<Node> heap) {
        var len = heap.Count;
        if (len < 2)
          return;
        for (var parent = (len - 2) / 2; ; --parent) {
          AdjustHeap(heap, parent, len, heap[parent]);
          if (parent == 0)
            return;
        }
      }

      public static Node Pop(List<Node> heap) {
        var top = heap[0];
        var last = heap.Count - 1;
        if (last > 0) {
          var value = heap[last];
          heap[last] = heap[0];
          AdjustHeap(heap, 0, last, value);
        }

        heap.RemoveAt(last);
        return top;
      }

      public static void Push(List<Node> heap, Node value) {
        heap.Add(value);
        PushHeap(heap, heap.Count - 1, 0, value);
      }

      private static void AdjustHeap(List<Node> heap, int hole, int len, Node value) {
        var top = hole;
        var child = hole;
        while (child < (len - 1) / 2) {
          child = 2 * (child + 1);
          if (Less(heap[child], heap[child - 1]))
            --child;
          heap[hole] = heap[child];
          hole = child;
        }

        if ((len & 1) == 0 && child == (len - 2) / 2) {
          child = 2 * (child + 1);
          heap[hole] = heap[child - 1];
          hole = child - 1;
        }

        PushHeap(heap, hole, top, value);
      }

      private static void PushHeap(List<Node> heap, int hole, int top, Node value) {
        var parent = (hole - 1) / 2;
        while (hole > top && Less(heap[parent], value)) {
          heap[hole] = heap[parent];
          hole = parent;
          parent = (hole - 1) / 2;
        }

        heap[hole] = value;
      }
    }
  }
}
