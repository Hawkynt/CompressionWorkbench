using System.Buffers.Binary;
using Compression.Core.Transforms;
using Compression.Registry;

namespace Compression.Core.Entropy.Grzip;

/// <summary>
/// GRZipII (<c>grzip</c> 0.3.0, <c>.grz</c>): block-sorting compression — optional LZP,
/// then BWT or Schindler ST4, then MTF or weighted-frequency-count ranking coded by an
/// adaptive binary/multi-symbol range coder, with an optional record (delta) pre-filter.
/// </summary>
/// <remarks>
/// <para>
/// File: the 12-byte signature <c>GRZipII\0\x02\x04:)</c>, then blocks. Block header (seven
/// little-endian 32-bit words): original size; mode (−1 stored, −2 record-filtered, else
/// the method flags); LZP size or stored sub-mode; BWT/ST4 index; payload size; payload
/// CRC-32; CRC-32 of the first 24 header bytes.
/// </para>
/// <para>
/// Converted from grzip 0.3.0 / libGRZip (© 2002-2004 Ilya Grebnov, LGPL-2.1-or-later):
/// libgrzip.c, LZP.c, BWT.c (decoders), ST4.c (decoder), MTF_Ari.c, WFC_Ari.c (decoder),
/// Rec_Flt.c (decoder), WFC_MTF.h. See THIRD-PARTY-NOTICE.grzip.txt. The encoder takes the
/// LZP + BWT + MTF path; its BWT is this repository's own.
/// </para>
/// </remarks>
public static class GrzipCodec {

  /// <summary>The 12-byte file signature.</summary>
  public static ReadOnlySpan<byte> Signature => [0x47, 0x52, 0x5A, 0x69, 0x70, 0x49, 0x49, 0x00, 0x02, 0x04, 0x3A, 0x29];

  private const int HeaderSize = 28;
  private const int MaxBlockSize = 8 * 1024 * 1024 - 512;
  private const int Log2MaxBlockSize = 23;
  private const int ModeSt4 = 0x100;
  private const int ModeMtf = 0x200;
  private const int ModeDisableDelta = 0x800;
  private const int StrongBwtFlag = 0x40000000;
  /// <summary>Encoder LZP: hash table 2^15 entries (HTS15), minimum match 32 (MML32).</summary>
  private const int EncoderLzpMode = 0x0C | 0xA0;
  /// <summary>Encoder block size: grzip's default of 5 MiB.</summary>
  private const int EncoderBlockSize = 5 * 1024 * 1024;

  // ── file level ───────────────────────────────────────────────────────

  /// <summary>Decodes a complete <c>.grz</c> file.</summary>
  /// <exception cref="InvalidDataException">Bad signature, CRC mismatch or malformed block.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
      throw new InvalidDataException("GRZip: missing GRZipII signature.");
    using var output = new MemoryStream();
    var pos = Signature.Length;
    while (pos < data.Length) {
      if (pos + HeaderSize > data.Length) throw new InvalidDataException("GRZip: truncated block header.");
      var payload = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 16)..]);
      if (payload < 0 || pos + HeaderSize + (long)payload > data.Length)
        throw new InvalidDataException("GRZip: truncated block.");
      output.Write(DecodeBlock(data.Slice(pos, HeaderSize + payload)));
      pos += HeaderSize + payload;
    }
    return output.ToArray();
  }

  /// <summary>Encodes <paramref name="data"/> as a <c>.grz</c> file (LZP + BWT + MTF blocks).</summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    output.Write(Signature);
    for (var pos = 0; pos < data.Length; pos += EncoderBlockSize)
      output.Write(EncodeBlock(data.Slice(pos, Math.Min(EncoderBlockSize, data.Length - pos))));
    return output.ToArray();
  }

  // ── block level ──────────────────────────────────────────────────────

  private static int Field(ReadOnlySpan<byte> block, int index) => BinaryPrimitives.ReadInt32LittleEndian(block[(index * 4)..]);

  private static byte[] DecodeBlock(ReadOnlySpan<byte> block) {
    if (block.Length < HeaderSize) throw new InvalidDataException("GRZip: truncated block header.");
    if ((uint)Field(block, 6) != Crc32(block[..24])) throw new InvalidDataException("GRZip: block header CRC mismatch.");
    var payloadSize = Field(block, 4);
    if (payloadSize < 0 || HeaderSize + payloadSize > block.Length) throw new InvalidDataException("GRZip: truncated block.");
    var payload = block.Slice(HeaderSize, payloadSize);
    if ((uint)Field(block, 5) != Crc32(payload)) throw new InvalidDataException("GRZip: block CRC mismatch.");

    var originalSize = Field(block, 0);
    if (originalSize < 0 || originalSize > MaxBlockSize) throw new InvalidDataException("GRZip: block size out of range.");
    var mode = Field(block, 1);

    if (mode == -1) {
      var sub = Field(block, 2);
      return sub == 0 ? payload.ToArray() : LzpDecode(payload, sub & 0xFF, originalSize);
    }

    if (mode == -2) {
      var recMode = Field(block, 2);
      var parts = (recMode & 1) == 1 ? 2 : 4;
      using var joined = new MemoryStream(originalSize);
      var tmp = payload;
      for (var p = 0; p < parts; ++p) {
        if (tmp.Length < HeaderSize) throw new InvalidDataException("GRZip: truncated record sub-block.");
        var size = HeaderSize + BinaryPrimitives.ReadInt32LittleEndian(tmp[16..]);
        if (size < HeaderSize || size > tmp.Length) throw new InvalidDataException("GRZip: truncated record sub-block.");
        joined.Write(DecodeBlock(tmp[..size]));
        tmp = tmp[size..];
      }
      if (joined.Length != originalSize) throw new InvalidDataException("GRZip: record sub-blocks disagree with the block size.");
      return RecDecode(joined.GetBuffer().AsSpan(0, originalSize), recMode);
    }

    var lzpSize = Field(block, 2);
    if (lzpSize < 0 || lzpSize > MaxBlockSize + 8) throw new InvalidDataException("GRZip: LZP size out of range.");
    var ranked = (mode & ModeMtf) != 0
      ? AriDecode(payload, wfc: false, lzpSize)
      : AriDecode(payload, wfc: true, lzpSize);
    var index = Field(block, 3);
    if ((mode & ModeSt4) != 0) St4Decode(ranked, index);
    else BwtDecode(ranked, index);
    if (lzpSize > ranked.Length) throw new InvalidDataException("GRZip: LZP size exceeds the decoded block.");
    var unsorted = ranked.AsSpan(0, lzpSize);
    var result = (mode & 0x0F) != 0 ? LzpDecode(unsorted, mode & 0xFF, originalSize) : unsorted.ToArray();
    if (result.Length != originalSize) throw new InvalidDataException("GRZip: decoded block size mismatch.");
    return result;
  }

  private static byte[] StoreBlock(ReadOnlySpan<byte> input, int subMode, int originalSize) {
    var block = new byte[HeaderSize + input.Length];
    BinaryPrimitives.WriteInt32LittleEndian(block, originalSize);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), -1);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), subMode & 0xFF);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(16), input.Length);
    input.CopyTo(block.AsSpan(HeaderSize));
    FinishHeader(block);
    return block;
  }

  private static void FinishHeader(byte[] block) {
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(20), Crc32(block.AsSpan(HeaderSize)));
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), Crc32(block.AsSpan(0, 24)));
  }

  private static byte[] EncodeBlock(ReadOnlySpan<byte> input) {
    if (input.Length < 32) return StoreBlock(input, 0, input.Length);

    var mode = ModeMtf | ModeDisableDelta | EncoderLzpMode;
    var lzp = LzpEncode(input, EncoderLzpMode);
    if (lzp == null) {
      mode &= ~0xFF;
      lzp = input.ToArray();
    }
    var lzpSize = lzp.Length;
    var padded = new byte[(lzpSize + 7) & ~7];
    lzp.CopyTo(padded, 0);
    var (sorted, index) = BurrowsWheelerTransform.Forward(padded);
    var payload = MtfAriEncode(sorted);
    if (payload == null)
      return (mode & 0x0F) != 0 ? StoreBlock(lzp, EncoderLzpMode, input.Length) : StoreBlock(input, 0, input.Length);

    var block = new byte[HeaderSize + payload.Length];
    BinaryPrimitives.WriteInt32LittleEndian(block, input.Length);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), mode);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), lzpSize);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(12), index);
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(16), payload.Length);
    payload.CopyTo(block, HeaderSize);
    FinishHeader(block);
    return block;
  }

  // ── CRC-32 (reflected 0xEDB88320) ─────────────────────────────────────

  private static readonly uint[] CrcTable = BuildCrcTable();

  private static uint[] BuildCrcTable() {
    var t = new uint[256];
    for (uint i = 0; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
      t[i] = c;
    }
    return t;
  }

  private static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) crc = CrcTable[(byte)(crc ^ b)] ^ (crc >> 8);
    return ~crc;
  }

  // ── LZP ──────────────────────────────────────────────────────────────

  private const byte LzpMatchFlag = 0xF2;
  private const byte LzpRunFlag = 0xF3;
  private const byte LzpXorFlag = 0xFF ^ LzpRunFlag;

  /// <summary>LZP-encodes, or returns null when the output would not be smaller.</summary>
  private static byte[]? LzpEncode(ReadOnlySpan<byte> input, int mode) {
    var htSize = (1 << (3 + (mode & 0xF))) - 1;
    var minLen = 2 + 3 * (mode >> 4);
    var contexts = new int[htSize + 1]; // position + 1, 0 = none
    var size = input.Length;
    var output = new byte[size];
    var outputEnd = size - 1;
    input[..4].CopyTo(output);
    var ctx = (uint)(input[3] | (input[2] << 8) | (input[1] << 16) | (input[0] << 24));
    int ip = 4, op = 4;
    while (ip < size && op < outputEnd) {
      var hash = (int)(((ctx >> 15) ^ ctx ^ (ctx >> 3)) & (uint)htSize);
      var pointer = contexts[hash] - 1;
      contexts[hash] = ip + 1;
      if (pointer >= 0) {
        var common = 0;
        while (ip + common < size && input[ip + common] == input[pointer + common]) ++common;
        if (common < minLen) common = 0;
        if (common > 0) {
          ip += common;
          ctx = (uint)(input[ip - 1] | (input[ip - 2] << 8) | (input[ip - 3] << 16) | (input[ip - 4] << 24));
          var code = common - minLen + 1;
          output[op++] = LzpMatchFlag;
          while (code > 254) {
            output[op++] = LzpRunFlag;
            if (op >= outputEnd) return null;
            code -= 255;
          }
          output[op++] = (byte)(code ^ LzpXorFlag);
        } else {
          var ch = input[ip++];
          output[op++] = ch;
          ctx = (ctx << 8) | ch;
          if (ch == LzpMatchFlag) {
            if (op >= output.Length) return null;
            output[op++] = LzpXorFlag;
          }
        }
      } else {
        var ch = input[ip++];
        output[op++] = ch;
        ctx = (ctx << 8) | ch;
      }
    }
    if (op >= outputEnd) return null;
    return output.AsSpan(0, op).ToArray();
  }

  private static byte[] LzpDecode(ReadOnlySpan<byte> input, int mode, int expectedSize) {
    if (input.Length < 4) throw new InvalidDataException("GRZip: LZP data too short.");
    var htSize = (1 << (3 + (mode & 0xF))) - 1;
    var minLen = 2 + 3 * (mode >> 4);
    var contexts = new int[htSize + 1];
    var output = new byte[expectedSize];
    if (expectedSize < 4) throw new InvalidDataException("GRZip: LZP block too small.");
    input[..4].CopyTo(output);
    var ctx = (uint)(input[3] | (input[2] << 8) | (input[1] << 16) | (input[0] << 24));
    int ip = 4, op = 4;
    void Put(byte b) {
      if (op >= output.Length) throw new InvalidDataException("GRZip: LZP output exceeds the block size.");
      output[op++] = b;
    }
    while (ip < input.Length) {
      var hash = (int)(((ctx >> 15) ^ ctx ^ (ctx >> 3)) & (uint)htSize);
      var pointer = contexts[hash] - 1;
      contexts[hash] = op + 1;
      if (pointer >= 0) {
        var b = input[ip++];
        if (b != LzpMatchFlag) {
          Put(b);
          ctx = (ctx << 8) | b;
        } else {
          var common = 0;
          byte c;
          do {
            if (ip >= input.Length) throw new InvalidDataException("GRZip: LZP match length truncated.");
            c = input[ip++];
            common += c ^ LzpXorFlag;
          } while (c == LzpRunFlag);
          if (common > 0) {
            common += minLen - 1;
            while (common-- > 0) Put(output[pointer++]);
            ctx = (uint)(output[op - 1] | (output[op - 2] << 8) | (output[op - 3] << 16) | (output[op - 4] << 24));
          } else {
            Put(LzpMatchFlag);
            ctx = (ctx << 8) | LzpMatchFlag;
          }
        }
      } else {
        var b = input[ip++];
        Put(b);
        ctx = (ctx << 8) | b;
      }
    }
    if (op != expectedSize) throw new InvalidDataException("GRZip: LZP output size mismatch.");
    return output;
  }

  // ── BWT / ST4 inverses ───────────────────────────────────────────────

  private static void BwtDecode(byte[] data, int index) {
    var size = data.Length;
    var strong = (index & StrongBwtFlag) != 0;
    var fbp = index & ~StrongBwtFlag;
    if (fbp < 0 || fbp > size) throw new InvalidDataException("GRZip: BWT index out of range.");
    var count = new uint[256];
    var t = new uint[size + 1];
    if (strong) {
      for (var i = 0; i < fbp; ++i) { var c = data[i]; t[i] = (count[c]++ << 8) | c; }
      for (var i = fbp; i < size; ++i) { var c = data[i]; t[i + 1] = (count[c]++ << 8) | c; }
      uint sum = 1;
      for (var i = 0; i < 256; ++i) { sum += count[i]; count[i] = sum - count[i]; }
      var p = 0u;
      for (var i = size - 1; i >= 0; --i) {
        if (p > size) throw new InvalidDataException("GRZip: corrupt BWT block.");
        var u = t[p];
        var c = (byte)u;
        p = (u >> 8) + count[c];
        data[i] = c;
      }
    } else {
      if (size > 0 && fbp >= size) throw new InvalidDataException("GRZip: BWT index out of range.");
      for (var i = 0; i < size; ++i) { var c = data[i]; t[i] = (count[c]++ << 8) | c; }
      uint sum = 0;
      for (var i = 0; i < 256; ++i) { sum += count[i]; count[i] = sum - count[i]; }
      var p = (uint)fbp;
      for (var i = size - 1; i >= 0; --i) {
        var u = t[p];
        var c = (byte)u;
        p = (u >> 8) + count[c];
        data[i] = c;
      }
    }
  }

  private static void St4Decode(byte[] input, int fbp) {
    const int indirect = 0x800000;
    var size = input.Length;
    if (fbp < 0 || fbp >= Math.Max(1, size)) throw new InvalidDataException("GRZip: ST4 index out of range.");
    var lastSeen = new int[256];
    var t = new int[256];
    var s = new int[256];
    var context2 = new int[65536];
    var flag = new byte[(size + 8) >> 3];
    var table = new uint[size + 1];
    Array.Fill(lastSeen, -1);
    for (var i = 0; i < size; ++i) ++t[input[i]];
    for (int i = 0, j = 0, sum = 0; i < 256; ++i) {
      sum += t[i];
      t[i] = sum - t[i];
      for (; j < sum; ++j) ++context2[(input[j] << 8) | i];
    }
    Array.Copy(t, s, 256);
    for (int i = 0, j = 0, sum = 0; i < 65536; ++i) {
      var cStart = sum;
      sum += context2[i];
      for (; j < sum; ++j) {
        var c = input[j];
        if (lastSeen[c] != cStart) {
          lastSeen[c] = cStart;
          flag[t[c] >> 3] |= (byte)(1 << (t[c] & 7));
        }
        ++t[c];
      }
    }
    Array.Clear(lastSeen);
    for (int i = 0, cStart = 0; i < size; ++i) {
      var c = input[i];
      if ((flag[i >> 3] & (1 << (i & 7))) != 0) cStart = i;
      if (lastSeen[c] <= cStart) {
        table[i] = (uint)s[c];
        lastSeen[c] = i + 1;
      } else
        table[i] = (uint)((lastSeen[c] - 1) | indirect);
      ++s[c];
      table[i] |= (uint)c << 24;
    }
    table[size] = indirect;
    uint value = table[fbp];
    var jj = fbp;
    for (var i = 0; i < size; ++i) {
      if ((value & indirect) != 0) {
        var at = (int)(value & (indirect - 1));
        if (at > size) throw new InvalidDataException("GRZip: corrupt ST4 block.");
        jj = (int)(table[at]++ & (indirect - 1));
        if (jj > size) throw new InvalidDataException("GRZip: corrupt ST4 block.");
        value = table[jj];
      } else {
        ++table[jj];
        jj = (int)(value & (indirect - 1));
        if (jj > size) throw new InvalidDataException("GRZip: corrupt ST4 block.");
        value = table[jj];
      }
      input[i] = (byte)(value >> 24);
    }
  }

  // ── record filter (inverse) ──────────────────────────────────────────

  private static byte[] RecDecode(ReadOnlySpan<byte> input, int mode) {
    var size = input.Length;
    var output = new byte[size];
    var ip = 0;
    switch (mode) {
      case 3: {
        var n = size >> 1;
        ushort pred = 0;
        for (var r = 0; r < n; ++r, ++ip) {
          var delta = (ushort)((input[ip] << 8) | input[ip + n]);
          delta = (delta & 1) != 0 ? (ushort)~(delta >> 1) : (ushort)(delta >> 1);
          pred = (ushort)(delta + pred);
          BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2 * r), pred);
        }
        for (var i = 2 * n; i < size; ++i, ++ip) output[i] = input[ip + n];
        break;
      }
      case 4: {
        var n = size >> 2;
        uint pred = 0;
        for (var r = 0; r < n; ++r, ++ip) {
          var delta = ((uint)input[ip] << 24) | ((uint)input[ip + 3 * n] << 16) | ((uint)input[ip + 2 * n] << 8) | input[ip + n];
          delta = (delta & 1) != 0 ? ~(delta >> 1) : delta >> 1;
          pred = delta + pred;
          BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4 * r), pred);
        }
        for (var i = 4 * n; i < size; ++i, ++ip) output[i] = input[ip + 3 * n];
        break;
      }
      case 1:
        for (var i = 0; i < size; i += 2) output[i] = input[ip++];
        for (var i = 1; i < size; i += 2) output[i] = input[ip++];
        break;
      case 2:
        for (var start = 0; start < 4; ++start)
          for (var i = start; i < size; i += 4) output[i] = input[ip++];
        break;
      default:
        throw new InvalidDataException($"GRZip: unknown record filter mode {mode}.");
    }
    return output;
  }

  // ── rank model + range coder ─────────────────────────────────────────

  private const int ModelMaxFreq = 1 << 11;
  private const int RleWidth = Log2MaxBlockSize + 1;
  private static readonly int[] GrBegin = [3, 5, 9, 17, 33, 65, 129];
  private static readonly int[] Log2RleSize = [
    1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384,
    32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304, 0,
  ];

  /// <summary>Adaptive models shared by the MTF and WFC rank coders.</summary>
  private sealed class Models {
    public readonly uint[] L0_0 = [1, 1, 1, 1, 4];
    public readonly uint[] L0_1 = new uint[256 * 5];
    public readonly uint[] L0_2 = new uint[4 * 256 * 5];
    public readonly uint[] L1_0 = new uint[8];
    public readonly uint[] L1_1 = new uint[8 * 8];
    public readonly uint[] L2_0 = new uint[8 * 128];
    public readonly uint[] Rle0 = new uint[64 * RleWidth];
    public readonly uint[] Rle1 = new uint[256 * RleWidth];
    public readonly uint[] Rle2 = new uint[RleWidth * RleWidth];
    public int CtxRle, CtxL0, CtxL1;

    public Models() {
      Array.Fill(this.L1_0, (uint)(ModelMaxFreq >> 1));
      Array.Fill(this.L1_1, (uint)(ModelMaxFreq >> 1), 0, 7 * 8); // row 7 is never used
      Array.Fill(this.L2_0, (uint)(ModelMaxFreq >> 1));
      Array.Fill(this.Rle0, (uint)(ModelMaxFreq >> 1));
      Array.Fill(this.Rle1, (uint)(ModelMaxFreq >> 1));
      Array.Fill(this.Rle2, (uint)(ModelMaxFreq >> 1));
    }

    /// <summary>Rescales the three level-0 frequency sets after an update.</summary>
    public void UpdateL0(int v, int u) {
      if (this.L0_0[4] > 58) {
        uint sum = 0;
        for (var k = 0; k < 4; ++k) sum += this.L0_0[k] = (this.L0_0[k] + 1) >> 1;
        this.L0_0[4] = sum;
      }
      if (this.L0_1[v + 4] > 62) {
        uint sum = 0;
        for (var k = 0; k < 4; ++k) sum += this.L0_1[v + k] >>= 1;
        this.L0_1[v + 4] = sum;
      }
      if (this.L0_2[u + 4] > 204) {
        uint sum = 0;
        for (var k = 0; k < 4; ++k) sum += this.L0_2[u + k] >>= 1;
        this.L0_2[u + 4] = sum;
      }
    }

    public void Bump(int v, int u, int rank) {
      this.L0_0[rank] += 2; this.L0_1[v + rank] += 2; this.L0_2[u + rank] += 2;
      this.L0_0[4] += 2; this.L0_1[v + 4] += 2; this.L0_2[u + 4] += 2;
      this.UpdateL0(v, u);
    }

    public uint Freq(int v, int u, int k) => this.L0_0[k] + this.L0_1[v + k] + this.L0_2[u + k];
  }

  private static void Up1(uint[] a, int i, int shift) => a[i] -= a[i] >> shift;
  private static void Up0(uint[] a, int i, int shift) => a[i] += (ModelMaxFreq - a[i]) >> shift;

  private ref struct RangeDecoder {
    private readonly ReadOnlySpan<byte> _in;
    private int _pos;
    private uint _code;
    private uint _range;

    public RangeDecoder(ReadOnlySpan<byte> input) {
      this._in = input;
      this._pos = 0;
      this._code = 0;
      this._range = uint.MaxValue;
      for (var i = 0; i < 5; ++i) this._code = (this._code << 8) | this.Next();
    }

    private uint Next() => this._pos < this._in.Length ? this._in[this._pos++] : 0u;

    public uint GetFreq(uint total) {
      this._range /= total;
      if (this._range == 0) throw new InvalidDataException("GRZip: range coder underflow.");
      return this._code / this._range;
    }

    public void Decode(uint freq, uint cum) {
      this._code -= cum * this._range;
      this._range *= freq;
      while (this._range < 1u << 24) {
        this._code = (this._code << 8) | this.Next();
        this._range <<= 8;
      }
    }

    /// <summary>Binary decision against probability-of-zero <paramref name="p0"/> (of 2048).</summary>
    public bool Bit(uint p0) {
      if (this.GetFreq(ModelMaxFreq) < p0) {
        this.Decode(p0, 0);
        return false;
      }
      this.Decode(ModelMaxFreq - p0, p0);
      return true;
    }
  }

  /// <summary>
  /// Decodes the rank stream (MTF or weighted-frequency-count ranking) back to the sorted
  /// block, stopping at the end symbol; <paramref name="capacity"/> bounds the output.
  /// </summary>
  private static byte[] AriDecode(ReadOnlySpan<byte> input, bool wfc, int capacity) {
    var limit = (capacity + 7) & ~7;
    var output = new byte[limit];
    var op = 0;
    var m = new Models();
    var rc = new RangeDecoder(input);

    // MTF list, or WFC list with weights (index 256 is a sentinel).
    var list = new int[257];
    var char2Index = new int[257];
    var weight = new int[257];
    for (var i = 0; i <= 256; ++i) list[i] = char2Index[i] = i;
    weight[256] = -1;
    var wfcBuf = wfc ? new byte[limit + 1] : [];
    var wfcPos = 0;
    int[] wfcVals = [114688, 7272, 4240, 2364, 1263, 649, 320, 153, 70, 31, 14, 8];

    int ch = 0;
    while (true) {
      var pred = ch;
      var v = pred * 5;
      var u = (4 * m.CtxL0 + (m.CtxRle & 3)) * 5;
      var frq = rc.GetFreq(m.L0_0[4] + m.L0_1[v + 4] + m.L0_2[u + 4]);
      uint cum = 0;
      var rank = 0;
      while (frq >= cum) {
        if (rank > 3) throw new InvalidDataException("GRZip: corrupt rank stream.");
        cum += m.Freq(v, u, rank);
        ++rank;
      }
      --rank;
      cum -= m.Freq(v, u, rank);
      rc.Decode(m.Freq(v, u, rank), cum);
      m.Bump(v, u, rank);

      if (rank == 3) {
        var groupNum = 0;
        var l1 = m.CtxL1 * 8;
        while (groupNum != 6) {
          var p = (m.L1_0[groupNum] + m.L1_1[l1 + groupNum]) >> 1;
          if (!rc.Bit(p)) {
            Up0(m.L1_0, groupNum, 4);
            Up0(m.L1_1, l1 + groupNum, 6);
            break;
          }
          Up1(m.L1_0, groupNum, 4);
          Up1(m.L1_1, l1 + groupNum, 6);
          ++groupNum;
        }
        m.CtxL1 = groupNum;
        var l2 = groupNum * 128;
        var ctx = 1;
        var groupPos = 0;
        for (var i = 0; i <= groupNum; ++i) {
          if (!rc.Bit(m.L2_0[l2 + ctx])) {
            Up0(m.L2_0, l2 + ctx, 7);
            ctx <<= 1;
            groupPos <<= 1;
          } else {
            Up1(m.L2_0, l2 + ctx, 7);
            ctx = (ctx << 1) | 1;
            groupPos = (groupPos << 1) | 1;
          }
        }
        if (groupNum == 6 && groupPos == 127) break;
        rank = GrBegin[groupNum] + groupPos;
      }

      rank = (rank + 1) & 0xFF;
      if (wfc) {
        ch = list[rank];
        wfcBuf[wfcPos] = (byte)ch;
        // Weight update: the new character jumps to the front with a fresh weight; the
        // characters seen 1, 2, 4 … 2048 symbols ago lose weight and sink.
        weight[ch] += 131072;
        for (var j = char2Index[ch]; j > 0; --j) char2Index[list[j] = list[j - 1]] = j;
        list[0] = ch;
        char2Index[ch] = 0;
        for (var k = 0; k < 12; ++k) {
          var back = 1 << k;
          if (wfcPos < back) break;
          int c = wfcBuf[wfcPos - back];
          var w = weight[c] -= wfcVals[k];
          if (c == ch) continue;
          var j = char2Index[c];
          var at = j + 1;
          while (weight[list[at]] > w) {
            char2Index[list[at - 1] = list[at]] = j++;
            ++at;
          }
          list[j] = c;
          char2Index[c] = j;
        }
        ++wfcPos;
      } else {
        ch = list[rank];
        for (var j = rank; j > 0; --j) list[j] = list[j - 1];
        list[0] = ch;
      }

      rank = (rank - 1) & 0xFF;
      if (rank > 3) rank = 3;
      m.CtxL0 = ((m.CtxL0 << 2) | rank) & 0xFF;

      var r0 = (m.CtxRle + 16 * rank) * RleWidth;
      var r1 = ch * RleWidth;
      var log2Run = 0;
      while (true) {
        if (log2Run >= RleWidth) throw new InvalidDataException("GRZip: run length out of range.");
        var p = (m.Rle0[r0 + log2Run] + m.Rle1[r1 + log2Run]) >> 1;
        if (!rc.Bit(p)) {
          Up0(m.Rle0, r0 + log2Run, 6);
          Up0(m.Rle1, r1 + log2Run, 3);
          break;
        }
        Up1(m.Rle0, r0 + log2Run, 6);
        Up1(m.Rle1, r1 + log2Run, 3);
        ++log2Run;
      }
      m.CtxRle = log2Run < 2 ? (m.CtxRle << 1) & 0xF : ((m.CtxRle << 1) | 1) & 0xF;
      var r2 = log2Run * RleWidth;
      var run = 0;
      for (var i = 0; i < log2Run; ++i) {
        if (!rc.Bit(m.Rle2[r2 + i])) {
          Up0(m.Rle2, r2 + i, 6);
          run <<= 1;
        } else {
          Up1(m.Rle2, r2 + i, 6);
          run = (run << 1) | 1;
        }
      }
      run += Log2RleSize[log2Run];
      if (op + run > output.Length) throw new InvalidDataException("GRZip: decoded block exceeds its size.");
      output.AsSpan(op, run).Fill((byte)ch);
      op += run;
    }
    return op == output.Length ? output : output.AsSpan(0, op).ToArray();
  }

  /// <summary>The MTF rank coder; null when the output would not be smaller than the input.</summary>
  private static byte[]? MtfAriEncode(ReadOnlySpan<byte> input) {
    var size = input.Length;
    var outputLimit = size - 24;
    var output = new List<byte>(size / 2 + 64);
    long low = 0;
    uint ffNum = 0, cache = 0, range = uint.MaxValue;

    void ShiftLow(List<byte> o) {
      if ((low ^ 0xFF000000) > 0xFFFFFF) {
        o.Add((byte)(cache + (low >> 32)));
        var c = (byte)(0xFF + (low >> 32));
        for (; ffNum > 0; --ffNum) o.Add(c);
        cache = (uint)low >> 24;
      } else
        ++ffNum;
      low = (uint)((uint)low << 8);
    }
    void Encode(List<byte> o, uint freq, uint cum, uint total) {
      range /= total;
      low += cum * range;
      range *= freq;
      while (range < 1u << 24) {
        ShiftLow(o);
        range <<= 8;
      }
    }
    void Bit(List<byte> o, uint p0, bool one) {
      if (one) Encode(o, ModelMaxFreq - p0, p0, ModelMaxFreq);
      else Encode(o, p0, 0, ModelMaxFreq);
    }

    var m = new Models();
    var list = new int[256];
    for (var i = 0; i < 256; ++i) list[i] = i;
    var ip = 0;
    int ch = 0;
    while (ip < size) {
      if (output.Count >= outputLimit) return null;
      var pred = ch;
      ch = input[ip++];
      var run = 1;
      while (ip < size && input[ip] == ch) { ++run; ++ip; }
      var rank = 0;
      while (list[rank] != ch) ++rank;
      for (var j = rank; j > 0; --j) list[j] = list[j - 1];
      list[0] = ch;
      rank = (rank - 1) & 0xFF;

      var v = pred * 5;
      var u = (4 * m.CtxL0 + (m.CtxRle & 3)) * 5;
      var total = m.L0_0[4] + m.L0_1[v + 4] + m.L0_2[u + 4];
      if (rank < 3) {
        uint cum = 0;
        for (var k = 0; k < rank; ++k) cum += m.Freq(v, u, k);
        Encode(output, m.Freq(v, u, rank), cum, total);
        m.Bump(v, u, rank);
      } else {
        Encode(output, m.Freq(v, u, 3), total - m.Freq(v, u, 3), total);
        m.Bump(v, u, 3);
        var groupNum = 6;
        while (groupNum > 0 && rank < GrBegin[groupNum]) --groupNum;
        var groupPos = rank - GrBegin[groupNum];
        var l1 = m.CtxL1 * 8;
        m.CtxL1 = groupNum;
        for (var i = 0; i < groupNum; ++i) {
          Bit(output, (m.L1_0[i] + m.L1_1[l1 + i]) >> 1, true);
          Up1(m.L1_0, i, 4);
          Up1(m.L1_1, l1 + i, 6);
        }
        if (groupNum != 6) {
          Bit(output, (m.L1_0[groupNum] + m.L1_1[l1 + groupNum]) >> 1, false);
          Up0(m.L1_0, groupNum, 4);
          Up0(m.L1_1, l1 + groupNum, 6);
        }
        var l2 = groupNum * 128;
        var ctx = 1;
        for (var mask = 1 << groupNum; mask > 0; mask >>= 1) {
          if ((groupPos & mask) != 0) {
            Bit(output, m.L2_0[l2 + ctx], true);
            Up1(m.L2_0, l2 + ctx, 7);
            ctx = (ctx << 1) | 1;
          } else {
            Bit(output, m.L2_0[l2 + ctx], false);
            Up0(m.L2_0, l2 + ctx, 7);
            ctx <<= 1;
          }
        }
      }
      if (rank > 3) rank = 3;
      m.CtxL0 = ((m.CtxL0 << 2) | rank) & 0xFF;

      var log2Run = 0;
      var runMask = 0;
      if (run > 1) {
        log2Run = 1;
        runMask = 1;
        for (var t = run; (t >>= 1) != 1;) {
          ++log2Run;
          runMask <<= 1;
        }
      }
      var r0 = (m.CtxRle + 16 * rank) * RleWidth;
      var r1 = ch * RleWidth;
      for (var i = 0; i < log2Run; ++i) {
        Bit(output, (m.Rle0[r0 + i] + m.Rle1[r1 + i]) >> 1, true);
        Up1(m.Rle0, r0 + i, 6);
        Up1(m.Rle1, r1 + i, 3);
      }
      Bit(output, (m.Rle0[r0 + log2Run] + m.Rle1[r1 + log2Run]) >> 1, false);
      Up0(m.Rle0, r0 + log2Run, 6);
      Up0(m.Rle1, r1 + log2Run, 3);
      m.CtxRle = log2Run < 2 ? (m.CtxRle << 1) & 0xF : ((m.CtxRle << 1) | 1) & 0xF;
      var r2 = log2Run * RleWidth;
      for (var i = 0; i < log2Run; ++i, runMask >>= 1) {
        if ((run & runMask) != 0) {
          Bit(output, m.Rle2[r2 + i], true);
          Up1(m.Rle2, r2 + i, 6);
        } else {
          Bit(output, m.Rle2[r2 + i], false);
          Up0(m.Rle2, r2 + i, 6);
        }
      }
    }

    // End symbol: rank 3, group 6, position 127.
    {
      var v = ch * 5;
      var u = (4 * m.CtxL0 + (m.CtxRle & 3)) * 5;
      var total = m.L0_0[4] + m.L0_1[v + 4] + m.L0_2[u + 4];
      Encode(output, m.Freq(v, u, 3), total - m.Freq(v, u, 3), total);
      m.Bump(v, u, 3);
      var l1 = m.CtxL1 * 8;
      for (var i = 0; i < 6; ++i) {
        Bit(output, (m.L1_0[i] + m.L1_1[l1 + i]) >> 1, true);
        Up1(m.L1_0, i, 4);
        Up1(m.L1_1, l1 + i, 6);
      }
      var ctx = 1;
      for (var i = 0; i <= 6; ++i) {
        Bit(output, m.L2_0[6 * 128 + ctx], true);
        Up1(m.L2_0, 6 * 128 + ctx, 7);
        ctx = (ctx << 1) | 1;
      }
    }
    low += range >>= 1;
    for (var i = 0; i < 5; ++i) ShiftLow(output);
    return output.Count >= outputLimit + 24 ? null : [.. output];
  }
}

/// <summary>GRZipII as a building block: a complete <c>.grz</c> stream.</summary>
public sealed class GrzipBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Grzip";
  /// <inheritdoc/>
  public string DisplayName => "GRZipII";
  /// <inheritdoc/>
  public string Description => "GRZipII: LZP + BWT/ST4 + MTF/WFC ranking with an adaptive range coder";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => GrzipCodec.Encode(data);
  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => GrzipCodec.Decode(data);
}
