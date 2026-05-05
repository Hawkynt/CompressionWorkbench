#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Apfs;

/// <summary>
/// Fletcher-64 checksum used by APFS to verify the integrity of <c>obj_phys_t</c>
/// blocks. Algorithm from the Apple File System Reference:
/// <code>
/// uint64 c1 = 0, c2 = 0;
/// for each 32-bit LE word u in data[8..end]:
///     c1 = (c1 + u) mod (2^32 - 1)
///     c2 = (c2 + c1) mod (2^32 - 1)
/// sum1 = (~(c1 + c2)) mod (2^32 - 1)
/// sum2 = (~(c1 + sum1)) mod (2^32 - 1)
/// o_cksum = (sum2 &lt;&lt; 32) | sum1
/// </code>
/// Stored at offset 0 of the object.
/// </summary>
internal static class ApfsFletcher64 {
  /// <summary>
  /// Computes the Fletcher-64 checksum over the block content starting at offset 8
  /// (i.e. excluding the 8-byte checksum field itself).
  /// </summary>
  public static ulong Compute(ReadOnlySpan<byte> block) {
    if (block.Length < 8 || (block.Length & 3) != 0)
      throw new ArgumentException("Block must be >= 8 bytes and multiple of 4.", nameof(block));

    const ulong mod = 0xFFFFFFFFUL;
    ulong c1 = 0, c2 = 0;
    var content = block[8..];
    for (var i = 0; i < content.Length; i += 4) {
      ulong u = BinaryPrimitives.ReadUInt32LittleEndian(content[i..]);
      c1 = (c1 + u) % mod;
      c2 = (c2 + c1) % mod;
    }
    var sum1 = (~(c1 + c2)) % mod;
    var sum2 = (~(c1 + sum1)) % mod;
    return (sum2 << 32) | sum1;
  }

  /// <summary>
  /// Computes Fletcher-64 and writes it to bytes [0..8) of <paramref name="block"/>.
  /// </summary>
  public static void Stamp(Span<byte> block) {
    block[..8].Clear();
    var ck = Compute(block);
    BinaryPrimitives.WriteUInt64LittleEndian(block, ck);
  }

  /// <summary>
  /// Verifies the stored Fletcher-64 checksum matches the content.
  /// </summary>
  public static bool Verify(ReadOnlySpan<byte> block) {
    if (block.Length < 8) return false;
    var stored = BinaryPrimitives.ReadUInt64LittleEndian(block);
    // To verify, zero-copy-compute by treating first 8 bytes as zeros: Compute skips first 8.
    var actual = Compute(block);
    return stored == actual;
  }
}
