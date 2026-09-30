#pragma warning disable CS1591
using Compression.Registry;

namespace FileSystem.Ext;

/// <summary>
/// Shrinks an ext2/3/4 filesystem image in place: files are packed towards the
/// start by the in-place defragmenter, then trailing free blocks are trimmed by
/// <see cref="ExtInPlaceShrinker"/>, which updates the bitmaps, descriptors,
/// superblock, backups and checksums. Every surviving block, inode and directory
/// entry stays as it was.
/// </summary>
/// <remarks>
/// This used to extract every file and write a fresh ext2 volume of the minimum
/// size, which dropped the journal, features, label, UUID, directories' contents
/// beyond names, and every file's mode, owner, times, links and extended
/// attributes. A volume that cannot be shrunk in place is now left as it is.
/// </remarks>
public static class ExtShrinkHelper {

  /// <summary>
  /// Result of an ext shrink operation: original and new sizes, plus whether the
  /// image was actually reduced.
  /// </summary>
  public sealed record ShrinkResult(long OriginalSize, long NewSize, bool WasReduced);

  /// <summary>
  /// Shrinks the ext image in <paramref name="image" /> to the smallest size the
  /// in-place path can reach.
  /// </summary>
  /// <param name="image">Readable/writable/seekable stream containing the ext image.</param>
  /// <returns>Shrink result with before/after sizes.</returns>
  public static ShrinkResult Shrink(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var originalSize = image.Length;

    // Packing first leaves the free space at the tail, where the trim can take it.
    try {
      new ExtFormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    } catch (NotSupportedException) {
      // Not laid out in place; the trim below takes what trailing space there is.
    }

    try {
      var result = ExtInPlaceShrinker.ShrinkToFit(image);
      return new ShrinkResult(originalSize, image.Length, result.WasReduced);
    } catch (NotSupportedException) {
      return new ShrinkResult(originalSize, originalSize, false);
    }
  }
}
