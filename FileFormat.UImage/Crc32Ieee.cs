#pragma warning disable CS1591
namespace FileFormat.UImage;

/// <summary>
/// Minimal CRC-32/IEEE (reflected, polynomial 0xEDB88320) used by the uImage header
/// and data checksums. Implemented locally so that <c>FileFormat.UImage</c> has no
/// dependency on <c>Compression.Core</c>.
/// </summary>
internal static class Crc32Ieee {

  private static readonly uint[] Table = BuildTable();

  private static uint[] BuildTable() {
    var t = new uint[256];
    for (var i = 0u; i < 256u; i++) {
      var c = i;
      for (var k = 0; k < 8; k++)
        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      t[i] = c;
    }
    return t;
  }

  /// <summary>Computes the standard CRC-32/IEEE of <paramref name="data"/>.</summary>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }
}
