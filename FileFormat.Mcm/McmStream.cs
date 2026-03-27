#pragma warning disable CS1591

namespace FileFormat.Mcm;

public static class McmStream {
  private static readonly byte[] Magic = "MCMARCHIVE"u8.ToArray();

  // LEB128 helpers
  private static void WriteLeb128(Stream s, ulong value) {
    do {
      byte b = (byte)(value & 0x7F);
      value >>= 7;
      if (value > 0) b |= 0x80;
      s.WriteByte(b);
    } while (value > 0);
  }

  private static ulong ReadLeb128(Stream s) {
    ulong result = 0;
    int shift = 0;
    byte b;
    do {
      int r = s.ReadByte();
      if (r < 0) throw new EndOfStreamException("Unexpected end of stream reading LEB128");
      b = (byte)r;
      result |= (ulong)(b & 0x7F) << shift;
      shift += 7;
    } while ((b & 0x80) != 0);
    return result;
  }

  public static void Compress(Stream input, Stream output) {
    // Read all input
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    byte[] data = ms.ToArray();

    // Write MCM archive header
    output.Write(Magic);                                    // "MCMARCHIVE" (10 bytes)
    output.WriteByte(0); output.WriteByte(0);               // major version = 0 (uint16 LE)
    output.WriteByte(84); output.WriteByte(0);              // minor version = 84 (uint16 LE)

    // Block metadata: mem_usage=5, algorithm=0, lzp_enabled=0, filter=0, profile=0
    output.WriteByte(5);  // mem_usage
    output.WriteByte(0);  // algorithm
    output.WriteByte(0);  // lzp_enabled
    output.WriteByte(0);  // filter
    output.WriteByte(0);  // profile

    // LEB128 segment count = 1
    WriteLeb128(output, 1);

    // Segment: original size (uint64 LE)
    byte[] sizeBytes = BitConverter.GetBytes((ulong)data.Length);
    if (!BitConverter.IsLittleEndian) Array.Reverse(sizeBytes);
    output.Write(sizeBytes);

    // Compress data using adaptive arithmetic coding with bit-tree byte encoding
    byte[] compressed = ArithmeticCompress(data);
    output.Write(compressed);
  }

  public static void Decompress(Stream input, Stream output) {
    // Read and verify magic
    byte[] magic = new byte[10];
    input.ReadExactly(magic);
    for (int i = 0; i < Magic.Length; i++) {
      if (magic[i] != Magic[i])
        throw new InvalidDataException("Not an MCM archive: invalid magic");
    }

    // Read version (uint16 LE × 2)
    byte[] ver = new byte[4];
    input.ReadExactly(ver);
    // major = ver[0] | (ver[1] << 8), minor = ver[2] | (ver[3] << 8) — not validated strictly

    // Read block metadata (5 bytes)
    byte[] meta = new byte[5];
    input.ReadExactly(meta);

    // Read LEB128 segment count
    ulong segCount = ReadLeb128(input);
    if (segCount == 0) return;

    // Read first segment
    byte[] sizeBuf = new byte[8];
    input.ReadExactly(sizeBuf);
    if (!BitConverter.IsLittleEndian) Array.Reverse(sizeBuf);
    ulong originalSize = BitConverter.ToUInt64(sizeBuf, 0);

    // Read the rest as compressed data
    using var compMs = new MemoryStream();
    input.CopyTo(compMs);
    byte[] compressed = compMs.ToArray();

    byte[] decompressed = ArithmeticDecompress(compressed, (int)originalSize);
    output.Write(decompressed);
  }

  // PAQ8-style adaptive arithmetic coder with bit-tree byte encoding (255 nodes).
  // probs[node] = probability of bit=1 in [1..4095]; p0 = 4096 - probs[node].
  // Encoder uses _low/_high style with 0x01000000 normalization threshold.
  // Decoder maintains _code register; normalization reads one byte at a time.

  private const int NumNodes = 256;  // nodes 1..255 used (1-indexed binary tree)
  private const int ProbScale = 4096;

  private static byte[] ArithmeticCompress(byte[] data) {
    // probs[node] = prob of bit=1, initialized to 2048 (50%)
    int[] probs = new int[NumNodes];
    for (int i = 1; i < NumNodes; i++) probs[i] = 2048;

    var buf = new List<byte>();
    uint low = 0;
    uint high = uint.MaxValue;

    void EncodeBit(int node, int bit) {
      uint range = high - low;
      int p0 = ProbScale - probs[node];
      if (p0 < 1) p0 = 1;
      if (p0 > ProbScale - 1) p0 = ProbScale - 1;
      uint mid = low + (uint)((ulong)range * (uint)p0 >> 12);

      if (bit == 0) {
        high = mid;
      } else {
        low = mid + 1;
      }

      // Update probability after encoding
      if (bit == 1) {
        probs[node] += (ProbScale - probs[node]) >> 5;
      } else {
        probs[node] -= probs[node] >> 5;
      }

      // Normalize
      while ((low ^ high) < 0x01000000u) {
        buf.Add((byte)(low >> 24));
        low <<= 8;
        high = (high << 8) | 0xFF;
      }
    }

    foreach (byte b in data) {
      int node = 1;
      for (int i = 7; i >= 0; i--) {
        int bit = (b >> i) & 1;
        EncodeBit(node, bit);
        node = node * 2 + bit;
      }
    }

    // Flush: emit at least 4 bytes to fully specify low
    buf.Add((byte)(low >> 24));
    buf.Add((byte)(low >> 16));
    buf.Add((byte)(low >> 8));
    buf.Add((byte)low);

    return buf.ToArray();
  }

  private static byte[] ArithmeticDecompress(byte[] compressed, int originalSize) {
    if (originalSize == 0) return [];

    int[] probs = new int[NumNodes];
    for (int i = 1; i < NumNodes; i++) probs[i] = 2048;

    int pos = 0;
    uint low = 0;
    uint high = uint.MaxValue;
    uint code = 0;

    // Prime the code register with exactly 4 bytes
    for (int i = 0; i < 4; i++) {
      code = (code << 8) | (pos < compressed.Length ? compressed[pos++] : 0u);
    }

    byte[] output = new byte[originalSize];

    int DecodeBit(int node) {
      uint range = high - low;
      int p0 = ProbScale - probs[node];
      if (p0 < 1) p0 = 1;
      if (p0 > ProbScale - 1) p0 = ProbScale - 1;
      uint mid = low + (uint)((ulong)range * (uint)p0 >> 12);

      int bit;
      if (code <= mid) {
        bit = 0;
        high = mid;
      } else {
        bit = 1;
        low = mid + 1;
      }

      // Update probability after decoding
      if (bit == 1) {
        probs[node] += (ProbScale - probs[node]) >> 5;
      } else {
        probs[node] -= probs[node] >> 5;
      }

      // Normalize
      while ((low ^ high) < 0x01000000u) {
        code = (code << 8) | (pos < compressed.Length ? compressed[pos++] : 0u);
        low <<= 8;
        high = (high << 8) | 0xFF;
      }

      return bit;
    }

    for (int i = 0; i < originalSize; i++) {
      int node = 1;
      int b = 0;
      for (int j = 7; j >= 0; j--) {
        int d = DecodeBit(node);
        b = (b << 1) | d;
        node = node * 2 + d;
      }
      output[i] = (byte)b;
    }

    return output;
  }
}
