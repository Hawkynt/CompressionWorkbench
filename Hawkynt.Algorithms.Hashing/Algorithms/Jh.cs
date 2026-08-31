using System.Buffers.Binary;
using System.Numerics;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// JH variant carried by the JavaScript algorithm registry.
/// </summary>
/// <remarks>
/// The source registry intentionally contains an educational JH-like permutation rather than the
/// standardized SHA-3 finalist. This managed counterpart preserves that source-specific byte
/// behavior and is named/documented accordingly instead of pretending to be standard JH.
/// </remarks>
public static class Jh {
  /// <summary>
  /// Gets the supported hash-output sizes, in bits.
  /// </summary>
  public static IReadOnlyList<HashSizeRange> SupportedHashSizes { get; } = [
    new(224, 256, 32),
    new(384, 512, 128)
  ];

  private static readonly uint[] InitialState = [
    0x17AA003EU,0x10E8B833U,0x6B8A92DDU,0xBB4F5D87U,
    0x6E1E0A6FU,0x8F647871U,0x65F0B83FU,0xA3277999U,
    0x07D3B531U,0x63D98F2AU,0x88B273E3U,0x98C93BB0U,
    0x5A1F1A59U,0x9893AE1BU,0x44693FD4U,0x8F0F7C3EU,
    0x9FA606ECU,0x55F6B6A3U,0xED4D5371U,0x06D2D5EBU,
    0x8C8F7F0BU,0x7729F33FU,0x0965DD0CU,0x3E4A6ECFU,
    0x9AAE8B6EU,0x7F5C89CDU,0x69C99F91U,0x8F1C2F1BU,
    0x5F6DAAD6U,0x3DBEAEB8U,0x68DB8BC8U,0x3A9D3C9FU
  ];

  /// <summary>
  /// Computes the Jh hash of the supplied data.
  /// </summary>
  public static byte[] Compute(ReadOnlySpan<byte> data, int hashSizeBits = 512) {
    if (!SupportedHashSizes.Supports(hashSizeBits))
      throw new ArgumentOutOfRangeException(nameof(hashSizeBits));

    var state = InitialState.ToArray();
    var offset = 0;
    while (offset + 64 <= data.Length) {
      Compress(state, data.Slice(offset, 64));
      offset += 64;
    }

    var remaining = data.Length - offset;
    var withMarker = remaining + 1;
    var zeroes = (56 - withMarker % 64 + 64) % 64;
    var final = new byte[withMarker + zeroes + 8];
    data[offset..].CopyTo(final);
    final[remaining] = 0x80;

    // Preserve the JavaScript registry's 32-bit shift semantics: its nominal 64-bit footer is
    // the low 32-bit bit count written big-endian twice because JS shifts are modulo 32.
    var bitLength = unchecked((uint)data.Length * 8U);
    BinaryPrimitives.WriteUInt32BigEndian(final.AsSpan(final.Length - 8, 4), bitLength);
    BinaryPrimitives.WriteUInt32BigEndian(final.AsSpan(final.Length - 4, 4), bitLength);

    for (var block = 0; block < final.Length; block += 64)
      Compress(state, final.AsSpan(block, 64));

    var outputBytes = hashSizeBits / 8;
    var result = new byte[outputBytes];
    for (var i = 0; i < outputBytes; ++i) {
      var word = state[16 + i / 4];
      result[i] = (byte)(word >> (24 - (i & 3) * 8));
    }
    return result;
  }

  private static void Compress(uint[] state, ReadOnlySpan<byte> block) {
    Span<uint> message = stackalloc uint[16];
    for (var i = 0; i < 16; ++i) {
      message[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4, 4));
      state[i] ^= message[i];
    }

    for (var round = 0; round < 42; ++round) {
      var roundConstant = unchecked((uint)round * 0x9E3779B9U);
      for (var i = 0; i < 32; ++i) {
        var value = BitOperations.RotateLeft(state[i], 7) ^ roundConstant;
        value ^= state[(i + 1) & 31];
        state[i] = value;
      }

      for (var i = 0; i < 16; ++i)
        (state[i], state[i + 16]) = (state[i + 16], state[i]);
    }

    for (var i = 0; i < 16; ++i)
      state[i + 16] ^= message[i];
  }
}
