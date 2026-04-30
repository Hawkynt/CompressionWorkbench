#pragma warning disable CS1591
namespace FileFormat.TfRecord;

/// <summary>
/// CRC-32C (Castagnoli) implementation used by TFRecord framing.
/// </summary>
/// <remarks>
/// Inlined here because FileFormat.* projects may only reference Compression.Registry,
/// not Compression.Core (which exposes the hardware-accelerated implementation).
/// Uses the reflected polynomial 0x82F63B78 — distinct from IEEE CRC-32 (0xEDB88320).
/// </remarks>
internal static class Crc32C {
  private const uint ReflectedPolynomial = 0x82F63B78u;
  private static readonly uint[] _Table = BuildTable();

  private static uint[] BuildTable() {
    var table = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? (c >> 1) ^ ReflectedPolynomial : c >> 1;
      table[i] = c;
    }
    return table;
  }

  /// <summary>Computes the CRC-32C of <paramref name="data"/>.</summary>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    for (var i = 0; i < data.Length; ++i)
      crc = _Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
    return ~crc;
  }

  /// <summary>
  /// Applies the TFRecord-specific masking transform to a raw CRC-32C.
  /// </summary>
  /// <remarks>
  /// Defined as <c>((crc &gt;&gt; 15) | (crc &lt;&lt; 17)) + 0xa282ead8</c> — rotates the CRC
  /// by 15 bits to break the algebraic property that <c>crc(data ^ crc) == 0</c>, which
  /// would otherwise let an attacker forge zero-CRC payloads.
  /// </remarks>
  public static uint Mask(uint crc) => ((crc >> 15) | (crc << 17)) + TfRecordConstants.CrcMaskDelta;
}
