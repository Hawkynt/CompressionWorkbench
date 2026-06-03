#pragma warning disable CS1591

namespace Codec.Tta;

/// <summary>
/// CRC-32/ISO-HDLC (the zlib / PKZIP polynomial 0xEDB88320, reflected input and
/// output, init 0xFFFFFFFF, final XOR 0xFFFFFFFF). TTA1 protects its 18-byte
/// header, its seek table and every coded frame with this checksum.
/// </summary>
internal static class TtaCrc {

  private static readonly uint[] _table = BuildTable();

  private static uint[] BuildTable() {
    var table = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[i] = c;
    }
    return table;
  }

  /// <summary>Computes the CRC-32 of <paramref name="data"/>.</summary>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }
}
