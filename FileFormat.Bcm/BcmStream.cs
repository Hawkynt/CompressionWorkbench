#pragma warning disable CS1591
using Compression.Core.Transforms;

namespace FileFormat.Bcm;

/// <summary>
/// BCM: Ilya Muravyov's BWT + MTF + Context Mixing compressor.
/// Format: 4-byte magic "BCM!" (raw), then all data (block sizes, BWT primary index, bytes,
/// EOF marker, CRC32) encoded through an arithmetic coder.
/// </summary>
public static class BcmStream {

  private static readonly byte[] Magic = [0x42, 0x43, 0x4D, 0x21]; // "BCM!"
  private const int BlockSize = 64 * 1024; // 64 KB blocks (larger blocks make BWT very slow)

  // ── Public API ────────────────────────────────────────────────────────────

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();

    output.Write(Magic);

    var enc = new BcmEncoder(output);
    uint rawCrc = 0xFFFFFFFFu; // incremental raw CRC (not yet finalized)
    var pos = 0;

    while (pos < data.Length) {
      var len = Math.Min(BlockSize, data.Length - pos);
      var block = data.AsSpan(pos, len);

      rawCrc = Crc32Accumulate(rawCrc, block);

      var (bwtData, primaryIndex) = BurrowsWheelerTransform.Forward(block);
      var mtfData = MtfEncode(bwtData);

      enc.EncodeUint32((uint)len);
      enc.EncodeUint32((uint)primaryIndex);
      foreach (var b in mtfData)
        enc.EncodeByte(b);

      pos += len;
    }

    // EOF marker + finalized CRC32 of the original data
    enc.EncodeUint32(0u);
    enc.EncodeUint32(Crc32Finalize(rawCrc));
    enc.Flush();
  }

  public static void Decompress(Stream input, Stream output) {
    Span<byte> magicBuf = stackalloc byte[4];
    input.ReadExactly(magicBuf);
    if (magicBuf[0] != Magic[0] || magicBuf[1] != Magic[1] ||
        magicBuf[2] != Magic[2] || magicBuf[3] != Magic[3])
      throw new InvalidDataException("Not a BCM stream (bad magic).");

    var dec = new BcmDecoder(input);
    uint rawCrc = 0xFFFFFFFFu; // incremental raw CRC (not yet finalized)

    using var result = new MemoryStream();

    while (true) {
      var blockLen = dec.DecodeUint32();
      if (blockLen == 0)
        break;

      var primaryIndex = (int)dec.DecodeUint32();

      var mtfData = new byte[blockLen];
      for (var i = 0; i < (int)blockLen; i++)
        mtfData[i] = dec.DecodeByte();

      var bwtData = MtfDecode(mtfData);
      var block = BurrowsWheelerTransform.Inverse(bwtData, primaryIndex);

      rawCrc = Crc32Accumulate(rawCrc, block);
      result.Write(block);
    }

    var storedCrc = dec.DecodeUint32();
    var computedCrc = Crc32Finalize(rawCrc);
    if (storedCrc != computedCrc)
      throw new InvalidDataException($"BCM CRC32 mismatch: stored=0x{storedCrc:X8}, computed=0x{computedCrc:X8}.");

    result.Position = 0;
    result.CopyTo(output);
  }

  // ── MTF (Move-to-Front) ───────────────────────────────────────────────────

  private static byte[] MtfEncode(ReadOnlySpan<byte> data) {
    var table = new byte[256];
    for (var i = 0; i < 256; i++) table[i] = (byte)i;
    var result = new byte[data.Length];
    for (var i = 0; i < data.Length; i++) {
      var sym = data[i];
      int rank = 0;
      while (table[rank] != sym) rank++;
      result[i] = (byte)rank;
      for (var j = rank; j > 0; j--)
        table[j] = table[j - 1];
      table[0] = sym;
    }
    return result;
  }

  private static byte[] MtfDecode(byte[] data) {
    var table = new byte[256];
    for (var i = 0; i < 256; i++) table[i] = (byte)i;
    var result = new byte[data.Length];
    for (var i = 0; i < data.Length; i++) {
      var rank = data[i];
      var sym = table[rank];
      result[i] = sym;
      for (var j = rank; j > 0; j--)
        table[j] = table[j - 1];
      table[0] = sym;
    }
    return result;
  }

  // ── CRC32 ─────────────────────────────────────────────────────────────────
  // Incremental CRC: pass raw (unfinalized) crc into Crc32Accumulate, then call
  // Crc32Finalize once at the end.  Starting raw value is 0xFFFFFFFF.

  private static readonly uint[] Crc32Table = BuildCrc32Table();

  private static uint[] BuildCrc32Table() {
    var table = new uint[256];
    for (uint i = 0; i < 256; i++) {
      var c = i;
      for (var j = 0; j < 8; j++)
        c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
      table[i] = c;
    }
    return table;
  }

  // Accumulate bytes into a running raw CRC (no XOR wrap applied here).
  private static uint Crc32Accumulate(uint rawCrc, ReadOnlySpan<byte> data) {
    foreach (var b in data)
      rawCrc = Crc32Table[(rawCrc ^ b) & 0xFF] ^ (rawCrc >> 8);
    return rawCrc;
  }

  // Finalize a raw CRC into the standard IEEE CRC32 value.
  private static uint Crc32Finalize(uint rawCrc) => rawCrc ^ 0xFFFFFFFFu;
}

// ── Arithmetic Encoder ────────────────────────────────────────────────────────
// 32-bit carryless range coder. Uses (low, high) representation with
// normalization: while top byte of low == top byte of high, emit low>>24 and shift.
//
// Context model for data bytes: 256 contexts (prev byte), each with a 255-node
// bit-tree for adaptive byte-level probability estimation (8-bit binary trie).
//
// uint32 fields are encoded using a flat bit-tree (single context, balanced init).

internal sealed class BcmEncoder {
  private readonly Stream _stream;
  private uint _low;
  private uint _high = 0xFFFFFFFF;

  // context bit-trees: _btree[ctx] is an array of 256 nodes (index 1..255 used)
  // for encoding a byte in context ctx.
  private readonly int[,] _btree = new int[256, 256]; // [ctx, node]

  // Flat bit-tree for raw uint32 fields (32 independent bits, each with own prob node)
  private readonly int[] _u32tree = new int[33]; // nodes 1..32 used

  private byte _prevByte;

  public BcmEncoder(Stream stream) {
    _stream = stream;
    // Initialise bit-tree probabilities to balanced (2048 = 0.5 at 12-bit scale)
    for (var c = 0; c < 256; c++)
      for (var n = 1; n < 256; n++)
        _btree[c, n] = 2048;
    for (var n = 1; n <= 32; n++)
      _u32tree[n] = 2048;
  }

  public void EncodeByte(byte b) {
    var ctx = _prevByte;
    _prevByte = b;

    // Walk the bit-tree from MSB to LSB
    var node = 1;
    for (var i = 7; i >= 0; i--) {
      var bit = (b >> i) & 1;
      EncodeBit(bit, _btree[ctx, node]);
      _btree[ctx, node] = UpdateProb(_btree[ctx, node], bit);
      node = (node << 1) | bit;
    }
  }

  public void EncodeUint32(uint value) {
    // Each bit of the uint32 uses its own independent probability node
    for (var i = 0; i < 32; i++) {
      var bit = (int)(value >> (31 - i)) & 1;
      EncodeBit(bit, _u32tree[i + 1]);
      _u32tree[i + 1] = UpdateProb(_u32tree[i + 1], bit);
    }
  }

  private void EncodeBit(int bit, int p0) {
    // p0: 12-bit probability that bit == 0
    var range = (ulong)(_high - _low) + 1;
    var mid = _low + (uint)((range * (ulong)p0) >> 12) - 1;
    if (mid < _low) mid = _low; // clamp when p0 near 0

    if (bit == 0)
      _high = (uint)mid;
    else
      _low = (uint)mid + 1;

    Normalize();
  }

  private void Normalize() {
    while ((_low ^ _high) < 0x01000000u) {
      _stream.WriteByte((byte)(_low >> 24));
      _low <<= 8;
      _high = (_high << 8) | 0xFF;
    }
  }

  public void Flush() {
    for (var i = 0; i < 4; i++) {
      _stream.WriteByte((byte)(_low >> 24));
      _low <<= 8;
    }
  }

  private static int UpdateProb(int prob, int bit) {
    // Exponential moving average: fast convergence
    return bit == 0 ? prob + ((4096 - prob) >> 5) : prob - (prob >> 5);
  }
}

// ── Arithmetic Decoder ────────────────────────────────────────────────────────

internal sealed class BcmDecoder {
  private readonly Stream _stream;
  private uint _low;
  private uint _high = 0xFFFFFFFF;
  private uint _code;

  private readonly int[,] _btree = new int[256, 256];
  private readonly int[] _u32tree = new int[33];

  private byte _prevByte;

  public BcmDecoder(Stream stream) {
    _stream = stream;
    for (var c = 0; c < 256; c++)
      for (var n = 1; n < 256; n++)
        _btree[c, n] = 2048;
    for (var n = 1; n <= 32; n++)
      _u32tree[n] = 2048;

    // Prime code with 4 bytes
    for (var i = 0; i < 4; i++)
      _code = (_code << 8) | (uint)ReadByte();
  }

  public byte DecodeByte() {
    var ctx = _prevByte;
    var node = 1;
    for (var i = 7; i >= 0; i--) {
      var bit = DecodeBit(_btree[ctx, node]);
      _btree[ctx, node] = UpdateProb(_btree[ctx, node], bit);
      node = (node << 1) | bit;
    }
    var sym = (byte)(node - 256);
    _prevByte = sym;
    return sym;
  }

  public uint DecodeUint32() {
    uint value = 0;
    for (var i = 0; i < 32; i++) {
      var bit = DecodeBit(_u32tree[i + 1]);
      _u32tree[i + 1] = UpdateProb(_u32tree[i + 1], bit);
      value = (value << 1) | (uint)bit;
    }
    return value;
  }

  private int DecodeBit(int p0) {
    var range = (ulong)(_high - _low) + 1;
    var mid = _low + (uint)((range * (ulong)p0) >> 12) - 1;
    if (mid < _low) mid = _low;

    int bit;
    if (_code <= (uint)mid) {
      bit = 0;
      _high = (uint)mid;
    } else {
      bit = 1;
      _low = (uint)mid + 1;
    }

    Normalize();
    return bit;
  }

  private void Normalize() {
    while ((_low ^ _high) < 0x01000000u) {
      _low <<= 8;
      _high = (_high << 8) | 0xFF;
      _code = (_code << 8) | (uint)ReadByte();
    }
  }

  private int ReadByte() {
    var b = _stream.ReadByte();
    return b < 0 ? 0xFF : b;
  }

  private static int UpdateProb(int prob, int bit) {
    return bit == 0 ? prob + ((4096 - prob) >> 5) : prob - (prob >> 5);
  }
}
