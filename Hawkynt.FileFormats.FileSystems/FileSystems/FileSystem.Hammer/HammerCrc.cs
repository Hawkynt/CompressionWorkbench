namespace FileSystem.Hammer;

/// <summary>
/// The two CRC-32 variants used by HAMMER (DragonFly BSD), mirroring
/// <c>sys/libkern/crc32.c</c> and <c>sys/libkern/icrc32.c</c>.
///
/// HAMMER metadata CRCs are version-gated by <c>hammer_datacrc()</c> in
/// <c>sys/vfs/hammer/hammer_crc.h</c>:
/// <list type="bullet">
///   <item><description><c>vol_version &lt;= 6</c> → <see cref="Crc32"/>, the
///   classic reflected CRC-32 (polynomial <c>0xEDB88320</c>, init/xorout
///   <c>0xFFFFFFFF</c>).</description></item>
///   <item><description><c>vol_version &gt;= 7</c> → <see cref="IscsiCrc32"/>, the
///   iSCSI / Castagnoli reflected CRC-32C (polynomial <c>0x82F63B78</c>,
///   init/xorout <c>0xFFFFFFFF</c>).</description></item>
/// </list>
///
/// Note: <c>newfs_hammer</c> never sets the volume-header <c>vol_crc</c> field
/// (it stays zero), and the kernel does not validate it at mount/info time;
/// only the freemap/undomap/B-Tree metadata CRCs below are load-bearing.
/// </summary>
internal static class HammerCrc {
  private static readonly uint[] Table32 = BuildTable(0xEDB88320u);
  private static readonly uint[] Table32C = BuildTable(0x82F63B78u);

  private static uint[] BuildTable(uint poly) {
    var table = new uint[256];
    for (var n = 0u; n < 256; ++n) {
      var c = n;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
      table[n] = c;
    }
    return table;
  }

  /// <summary>Classic reflected CRC-32 (zlib/Ethernet), HAMMER vol_version &lt;= 6.</summary>
  public static uint Crc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = Table32[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }

  /// <summary>iSCSI/Castagnoli reflected CRC-32C, HAMMER vol_version &gt;= 7.</summary>
  public static uint IscsiCrc32(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
      crc = Table32C[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }

  /// <summary>
  /// HAMMER's version-gated data CRC (<c>hammer_datacrc</c>): CRC-32 for
  /// <paramref name="volVersion"/> &lt;= 6, CRC-32C for &gt;= 7.
  /// </summary>
  public static uint DataCrc(uint volVersion, ReadOnlySpan<byte> data)
    => volVersion >= 7 ? IscsiCrc32(data) : Crc32(data);
}
