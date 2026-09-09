#pragma warning disable CS1591

using Compression.Core.Entropy.ContextMixing.Mcm;

namespace FileFormat.Mcm;

/// <summary>
/// Compression modes available to the managed MCM stream writer.
/// </summary>
/// <remarks>
/// <see cref="Legacy"/> preserves the byte stream emitted before profile
/// optimization was added. The remaining modes use the independent reduced MCM
/// model graph from <see cref="McmCompressor"/> and are self-described in the
/// stream metadata so every optimizer candidate remains decodable.
/// </remarks>
public enum McmCompressionMode : byte {
  Legacy = 0,
  Turbo = (byte)McmCompressionProfile.Turbo,
  Fast = (byte)McmCompressionProfile.Fast,
  Mid = (byte)McmCompressionProfile.Mid,
  High = (byte)McmCompressionProfile.High,
  Max = (byte)McmCompressionProfile.Max,
}

/// <summary>
/// Represents a mcm stream.
/// </summary>
public static class McmStream {
  private static readonly byte[] Magic = "MCMARCHIVE"u8.ToArray();

  // Algorithm 0 is the historical managed arithmetic payload already emitted
  // by CompressionWorkbench. 0x80 is deliberately outside upstream MCM's small
  // algorithm-id range and identifies our reduced clean-room profile payload.
  private const byte LegacyAlgorithm = 0;
  private const byte ReducedMcmAlgorithm = 0x80;

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
      if (shift >= 64 || (shift == 63 && (b & 0x7E) != 0))
        throw new InvalidDataException("Invalid MCM LEB128 value.");
      result |= (ulong)(b & 0x7F) << shift;
      shift += 7;
    } while ((b & 0x80) != 0);
    return result;
  }

  /// <summary>
  /// Encodes the supplied input using the historical managed payload.
  /// </summary>
  public static void Compress(Stream input, Stream output) => Compress(input, output, McmCompressionMode.Legacy);

  /// <summary>
  /// Encodes the supplied input using the selected managed MCM mode.
  /// </summary>
  public static void Compress(Stream input, Stream output, McmCompressionMode mode) {
    if (!Enum.IsDefined(mode))
      throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown MCM compression mode.");

    // Read all input
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    byte[] data = ms.ToArray();

    // Write MCM archive header
    output.Write(Magic);                                    // "MCMARCHIVE" (10 bytes)
    output.WriteByte(0); output.WriteByte(0);               // major version = 0 (uint16 LE)
    output.WriteByte(84); output.WriteByte(0);              // minor version = 84 (uint16 LE)

    var isLegacy = mode == McmCompressionMode.Legacy;

    // Block metadata. The legacy tuple is kept byte-for-byte for compatibility;
    // reduced profiles use a private algorithm id plus the profile byte.
    output.WriteByte(5);                                    // mem_usage
    output.WriteByte(isLegacy ? LegacyAlgorithm : ReducedMcmAlgorithm);
    output.WriteByte(0);                                    // lzp_enabled
    output.WriteByte(0);                                    // filter
    output.WriteByte(isLegacy ? (byte)0 : (byte)mode);      // profile

    // LEB128 segment count = 1
    WriteLeb128(output, 1);

    // Segment: original size (uint64 LE)
    byte[] sizeBytes = BitConverter.GetBytes((ulong)data.Length);
    if (!BitConverter.IsLittleEndian) Array.Reverse(sizeBytes);
    output.Write(sizeBytes);

    byte[] compressed = isLegacy
      ? ArithmeticCompress(data)
      : McmCompressor.Compress(data, (McmCompressionProfile)(byte)mode);
    output.Write(compressed);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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
    var algorithm = meta[1];
    var profile = meta[4];

    // Read LEB128 segment count
    ulong segCount = ReadLeb128(input);
    if (segCount == 0) return;

    // Read first segment
    byte[] sizeBuf = new byte[8];
    input.ReadExactly(sizeBuf);
    if (!BitConverter.IsLittleEndian) Array.Reverse(sizeBuf);
    ulong originalSize = BitConverter.ToUInt64(sizeBuf, 0);
    if (originalSize > int.MaxValue)
      throw new InvalidDataException("MCM stream is too large for the managed decoder.");

    // Read the rest as compressed data
    using var compMs = new MemoryStream();
    input.CopyTo(compMs);
    byte[] compressed = compMs.ToArray();

    byte[] decompressed = algorithm switch {
      LegacyAlgorithm => ArithmeticDecompress(compressed, (int)originalSize),
      ReducedMcmAlgorithm => DecompressReduced(compressed, profile),
      _ => throw new InvalidDataException($"Unsupported MCM algorithm id {algorithm}.")
    };

    if ((ulong)decompressed.LongLength != originalSize)
      throw new InvalidDataException(
        $"MCM payload length mismatch: header declares {originalSize} bytes, payload declares {decompressed.LongLength}.");

    output.Write(decompressed);
  }

  private static byte[] DecompressReduced(byte[] compressed, byte profile) {
    var mode = (McmCompressionMode)profile;
    if (mode is McmCompressionMode.Legacy || !Enum.IsDefined(mode))
      throw new InvalidDataException($"Unsupported reduced MCM profile id {profile}.");

    return McmCompressor.Decompress(compressed, (McmCompressionProfile)profile);
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
