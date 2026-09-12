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
    var crc = Update(0xFFFFFFFFu, data);
    return crc ^ 0xFFFFFFFFu;
  }

  /// <summary>
  /// Computes CRC-32/IEEE over exactly <paramref name="count"/> bytes from the
  /// stream's current position without buffering the payload as one array.
  /// </summary>
  public static uint Compute(Stream stream, long count) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentOutOfRangeException.ThrowIfNegative(count);

    Span<byte> buffer = stackalloc byte[16 * 1024];
    var crc = 0xFFFFFFFFu;
    var remaining = count;
    while (remaining > 0) {
      var wanted = (int)Math.Min(buffer.Length, remaining);
      var read = stream.Read(buffer[..wanted]);
      if (read == 0)
        throw new EndOfStreamException($"Expected {remaining} more bytes while computing CRC-32.");
      crc = Update(crc, buffer[..read]);
      remaining -= read;
    }
    return crc ^ 0xFFFFFFFFu;
  }

  private static uint Update(uint crc, ReadOnlySpan<byte> data) {
    foreach (var b in data)
      crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc;
  }
}
