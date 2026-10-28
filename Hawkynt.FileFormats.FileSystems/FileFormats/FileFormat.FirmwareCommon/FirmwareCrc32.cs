#pragma warning disable CS1591
namespace FileFormat.FirmwareCommon;

/// <summary>
/// The reflected CRC-32 (polynomial 0xEDB88320) firmware headers use, exposed raw: initial value
/// and final XOR are the caller's, because TRX for one stores the register without the final
/// inversion.
/// </summary>
public static class FirmwareCrc32 {

  private static readonly uint[] Table = BuildTable();

  private static uint[] BuildTable() {
    var table = new uint[256];
    for (uint i = 0; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[i] = c;
    }
    return table;
  }

  public static uint Update(uint crc, ReadOnlySpan<byte> data) {
    foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc;
  }

  /// <summary>The standard CRC-32 (zlib, IEEE 802.3).</summary>
  public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFFu, data);
}
