#pragma warning disable CS1591
namespace FileFormat.Cmix;

/// <summary>
/// cmix file format by Byron Knoll.
/// Format:
///   Byte 0: bit7=dict flag (0), bits 0-6 = upper 7 bits of 39-bit file size
///   Bytes 1-4: lower 32 bits of file size (big-endian)
///   If size >= 10000: 32-byte vocabulary bitmap
///   Then: arithmetic-coded bitstream (order-0 adaptive model, bit-tree 255 nodes)
/// </summary>
public static class CmixStream {

  private const long SizeThreshold = 10000;

  // Arithmetic coder range
  private const uint Top = 0x01000000u;
  private const int ProbBits = 12;
  private const int ProbMax = 1 << ProbBits; // 4096
  private const int ProbInit = ProbMax / 2;  // 2048

  // ── Public API ────────────────────────────────────────────────────────────

  public static void Compress(Stream input, Stream output) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();
    var size = (long)data.Length;

    // Write 5-byte header (39-bit size, big-endian, no dict flag)
    // Byte 0: bits 0-6 of the upper 7 bits of the 39-bit size (bit7=0, dict=false)
    var upper7 = (byte)((size >> 32) & 0x7F);
    var lower32 = (uint)(size & 0xFFFFFFFF);
    output.WriteByte(upper7);
    output.WriteByte((byte)(lower32 >> 24));
    output.WriteByte((byte)(lower32 >> 16));
    output.WriteByte((byte)(lower32 >> 8));
    output.WriteByte((byte)lower32);

    // If size >= 10000, write 32-byte vocabulary bitmap
    if (size >= SizeThreshold) {
      var vocab = BuildVocabBitmap(data);
      output.Write(vocab);
    }

    if (data.Length == 0) return;

    // Encode using bit-tree (255 nodes) arithmetic coder with order-0 adaptive model
    var enc = new ArithEncoder(output);
    var probs = new int[256]; // 256-entry order-0 model (one prob per symbol)
    Array.Fill(probs, ProbInit);

    // Bit-tree: 255 nodes for 8-bit symbols (binary tree 1..255, leaves 256..511)
    var bitTree = new int[256]; // nodes 1..255
    Array.Fill(bitTree, ProbInit);

    foreach (var b in data) {
      EncodeByte(enc, b, bitTree);
    }

    enc.Flush();
  }

  public static void Decompress(Stream input, Stream output) {
    // Read 5-byte header
    var h0 = input.ReadByte();
    var h1 = input.ReadByte();
    var h2 = input.ReadByte();
    var h3 = input.ReadByte();
    var h4 = input.ReadByte();
    if (h0 < 0 || h1 < 0 || h2 < 0 || h3 < 0 || h4 < 0)
      throw new InvalidDataException("Truncated cmix header.");

    var upper7 = (long)(h0 & 0x7F);
    var lower32 = (long)(((uint)h1 << 24) | ((uint)h2 << 16) | ((uint)h3 << 8) | (uint)h4);
    var size = (upper7 << 32) | lower32;

    // Read vocabulary bitmap if present
    if (size >= SizeThreshold) {
      var vocab = new byte[32];
      input.ReadExactly(vocab);
      // vocab bitmap is read but not used in our order-0 model
    }

    if (size == 0) return;

    var dec = new ArithDecoder(input);
    var bitTree = new int[256];
    Array.Fill(bitTree, ProbInit);

    var result = new byte[size];
    for (long i = 0; i < size; i++) {
      result[i] = DecodeByte(dec, bitTree);
    }

    output.Write(result);
  }

  // ── Bit-tree byte encoding ────────────────────────────────────────────────
  // 255 nodes: node 1 = root. For symbol b, path from root:
  //   Start at node=1, for each bit from MSB to LSB:
  //     bit=0 → node = node*2, bit=1 → node = node*2+1
  //   Node indices 1..255 (binary tree of depth 8, nodes at depth 8 are leaves)

  private static void EncodeByte(ArithEncoder enc, byte val, int[] tree) {
    var node = 1;
    for (var i = 7; i >= 0; i--) {
      var bit = (val >> i) & 1;
      enc.EncodeBit(bit, ref tree[node]);
      node = (node << 1) | bit;
      if (node >= 256) break; // leaf level reached
    }
  }

  private static byte DecodeByte(ArithDecoder dec, int[] tree) {
    var node = 1;
    for (var i = 0; i < 8; i++) {
      var bit = dec.DecodeBit(ref tree[node]);
      node = (node << 1) | bit;
      if (node >= 256) break;
    }
    return (byte)(node - 256);
  }

  // ── Vocabulary bitmap ─────────────────────────────────────────────────────

  private static byte[] BuildVocabBitmap(byte[] data) {
    var bitmap = new byte[32];
    foreach (var b in data)
      bitmap[b >> 3] |= (byte)(1 << (b & 7));
    return bitmap;
  }

  // ── Arithmetic Coder ─────────────────────────────────────────────────────

  private sealed class ArithEncoder {
    private uint _low;
    private uint _high = 0xFFFFFFFFu;
    private readonly Stream _out;

    public ArithEncoder(Stream output) => _out = output;

    public void EncodeBit(int bit, ref int prob) {
      var range = _high - _low + 1;
      var mid = _low + (uint)((ulong)range * (uint)prob >> ProbBits) - 1;
      if (mid >= _high) mid = _high - 1;

      if (bit == 0) {
        _high = mid;
        prob += (ProbMax - prob) >> 5;
      } else {
        _low = mid + 1;
        prob -= prob >> 5;
      }

      Normalize();
    }

    private void Normalize() {
      while ((_low ^ _high) < Top) {
        _out.WriteByte((byte)(_high >> 24));
        _low <<= 8;
        _high = (_high << 8) | 0xFFu;
      }
    }

    public void Flush() {
      for (var i = 0; i < 4; i++) {
        _out.WriteByte((byte)(_high >> 24));
        _high <<= 8;
      }
    }
  }

  private sealed class ArithDecoder {
    private uint _low;
    private uint _high = 0xFFFFFFFFu;
    private uint _code;
    private readonly Stream _in;

    public ArithDecoder(Stream input) {
      _in = input;
      for (var i = 0; i < 4; i++)
        _code = (_code << 8) | (uint)Math.Max(0, input.ReadByte());
    }

    public int DecodeBit(ref int prob) {
      var range = _high - _low + 1;
      var mid = _low + (uint)((ulong)range * (uint)prob >> ProbBits) - 1;
      if (mid >= _high) mid = _high - 1;

      int bit;
      if (_code <= mid) {
        bit = 0;
        _high = mid;
        prob += (ProbMax - prob) >> 5;
      } else {
        bit = 1;
        _low = mid + 1;
        prob -= prob >> 5;
      }

      Normalize();
      return bit;
    }

    private void Normalize() {
      while ((_low ^ _high) < Top) {
        var b = _in.ReadByte();
        _code = (_code << 8) | (uint)(b < 0 ? 0xFF : b);
        _low <<= 8;
        _high = (_high << 8) | 0xFFu;
      }
    }
  }
}
