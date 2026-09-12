using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Mix;

/// <summary>
/// Computes the 32-bit Westwood "classic" file ID used by Tiberian Dawn / Red Alert 1 MIX archives.
/// </summary>
/// <remarks>
/// Reference: OpenRA <c>OpenRA.Mods.Cnc/FileSystem/MixFile.cs</c> (PackageHashType.Classic) and
/// XCC Utilities (Olaf van der Spek). The algorithm is:
/// <list type="number">
///   <item>Uppercase the filename (ASCII / invariant).</item>
///   <item>Null-pad to a multiple of 4 bytes.</item>
///   <item>Read little-endian 32-bit words; for each word: <c>crc = rotl(crc, 1) + word</c>.</item>
/// </list>
/// This matches the per-file directory keys produced by Westwood's RA/TD tools and consumed by
/// the games at runtime for binary-search lookup.
/// </remarks>
public static class WestwoodCrc {

  /// <summary>
  /// Computes the Westwood TD/RA1 32-bit file ID for the given filename.
  /// </summary>
  /// <param name="filename">The original filename. Will be uppercased internally.</param>
  /// <returns>The 32-bit Westwood ID hash.</returns>
  public static uint Hash(string filename) {
    ArgumentNullException.ThrowIfNull(filename);

    var upper = filename.ToUpperInvariant();
    var raw = Encoding.ASCII.GetBytes(upper);

    var paddedLen = (raw.Length + 3) & ~3;
    Span<byte> padded = paddedLen <= 256 ? stackalloc byte[paddedLen] : new byte[paddedLen];
    padded.Clear();
    raw.AsSpan().CopyTo(padded);

    var crc = 0u;
    for (var i = 0; i < paddedLen; i += 4) {
      var word = BinaryPrimitives.ReadUInt32LittleEndian(padded.Slice(i, 4));
      crc = ((crc << 1) | (crc >> 31)) + word;
    }

    return crc;
  }
}
