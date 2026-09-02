#pragma warning disable CS1591
namespace FileSystem.ApplePascal;

/// <summary>
/// Picks the smallest Apple Pascal volume size (in 512-byte blocks, rounded to
/// a multiple of 8 per Pascal convention) that fits a given fileset. Block size
/// is always 512 (spec-mandated for Apple Pascal volumes); only the volume
/// total varies.
/// </summary>
public static class ApplePascalOptimizer {

  /// <summary>
  /// Represents an apple pascal geometry.
  /// </summary>
  public sealed record ApplePascalGeometry(int BlockSize, int VolumeBlocks);

  /// <summary>Reserved for the 6-block boot + directory region.</summary>
  private const int ReservedBlocks = 6;

  /// <summary>
  /// Performs the find operation.
  /// </summary>
  public static ApplePascalGeometry Find(System.Collections.Generic.IReadOnlyList<long> fileSizes) {
    System.ArgumentNullException.ThrowIfNull(fileSizes);
    var totalBlocks = ReservedBlocks;
    foreach (var s in fileSizes) {
      if (s <= 0) continue;
      var b = (int)((s + ApplePascalReader.BlockSize - 1) / ApplePascalReader.BlockSize);
      totalBlocks += b;
    }
    // Round up to multiple of 8 (Pascal allocation-tile convention).
    var rounded = ((totalBlocks + 7) / 8) * 8;
    // Standard Apple Pascal floppy sizes: 280 (140 KB SS), 560 (280 KB DS).
    // Honour them when payload fits, else use the rounded value.
    if (rounded < 280) rounded = 280;
    return new ApplePascalGeometry(ApplePascalReader.BlockSize, rounded);
  }
}
