#pragma warning disable CS1591
namespace FileSystem.Jffs2;

/// <summary>
/// The CRC-32 JFFS2 seals its nodes with.
/// </summary>
/// <remarks>
/// <para>It is the reflected IEEE polynomial, but it is <em>not</em> the CRC-32
/// of ZIP, gzip or PNG. Both the kernel driver and mtd-utils compute every
/// JFFS2 checksum as <c>crc32(0, buffer, length)</c> over a routine that neither
/// pre-loads the register with ones nor inverts the result — so the register
/// starts at the literal seed 0 and the final value is used as it stands.</para>
///
/// <para>Handing those fields the ordinary CRC-32 instead produces a number that
/// is wrong in every node of the image. Nothing here noticed, because this
/// project's own reader does not check them; <c>jffs2dump -c</c> reported "Wrong
/// hdr_crc" on every node of every volume we wrote, and the kernel's driver,
/// which discards a node whose <c>hdr_crc</c> does not recompute, was left with
/// no nodes at all and refused to mount the volume.</para>
/// </remarks>
internal static class Jffs2Crc {

  private static readonly uint[] Table = BuildTable();

  private static uint[] BuildTable() {
    var table = new uint[256];
    for (var i = 0u; i < 256u; ++i) {
      var value = i;
      for (var bit = 0; bit < 8; ++bit)
        value = (value >> 1) ^ (0xEDB88320u & (uint)-(int)(value & 1));
      table[i] = value;
    }

    return table;
  }

  /// <summary>Linux <c>crc32_le</c> seeded with zero, the way JFFS2 calls it.</summary>
  public static uint Compute(ReadOnlySpan<byte> data) => Compute(0u, data);

  /// <summary>Linux <c>crc32_le</c> with an explicit register seed.</summary>
  public static uint Compute(uint seed, ReadOnlySpan<byte> data) {
    var crc = seed;
    foreach (var b in data)
      crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc;
  }
}
