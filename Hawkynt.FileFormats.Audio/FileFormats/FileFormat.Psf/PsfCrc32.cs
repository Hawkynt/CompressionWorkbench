namespace FileFormat.Psf;

/// <summary>
/// Standard CRC-32 (IEEE 802.3 / zlib polynomial 0xEDB88320). Inlined here because
/// FileFormat.* projects only reference <c>Compression.Registry</c>, not <c>Compression.Core</c>
/// where the shared (and hardware-accelerated) <c>Crc32</c> lives. PSF stores this CRC over
/// the compressed program bytes inside its 16-byte header, so a tiny dependency-free
/// table-driven implementation is sufficient.
/// </summary>
public static class PsfCrc32 {
  private static readonly uint[] Table = BuildTable();

  private static uint[] BuildTable() {
    var t = new uint[256];
    for (var i = 0u; i < 256; ++i) {
      var c = i;
      for (var k = 0; k < 8; ++k)
        c = (c & 1) != 0 ? PsfConstants.Crc32Polynomial ^ (c >> 1) : c >> 1;
      t[i] = c;
    }
    return t;
  }

  /// <summary>Computes the standard CRC-32 of the given bytes.</summary>
  public static uint Compute(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    for (var i = 0; i < data.Length; ++i)
      crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
  }
}
