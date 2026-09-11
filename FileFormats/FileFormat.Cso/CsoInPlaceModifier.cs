#pragma warning disable CS1591

namespace FileFormat.Cso;

/// <summary>
/// Block-level mutator for CSO v1/v2 and ZSO images.
/// </summary>
/// <remarks>
/// A CSO index is ordered by physical block position and each block's physical size is derived from
/// the following index entry. Growing a middle block in place therefore cannot be represented by
/// changing only that entry. Edits are staged as a canonical whole-container repack and committed
/// only after the replacement has been encoded successfully. This also removes stale padding and
/// supports non-zero index shifts without trying to preserve their allocation slack.
/// </remarks>
public static class CsoInPlaceModifier {
  /// <summary>
  /// Replaces one logical block. <paramref name="newUncompressedData"/> must be exactly block_size
  /// bytes; for a partial final block only its logical prefix is retained, matching CSO semantics.
  /// </summary>
  public static void WriteBlock(Stream image, int blockIndex, ReadOnlySpan<byte> newUncompressedData) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new InvalidOperationException("CSO/ZSO block modification requires a readable, writable, seekable stream.");

    var layout = CsoImage.ReadLayout(image);
    if ((uint)blockIndex >= (uint)layout.BlockCount)
      throw new ArgumentOutOfRangeException(nameof(blockIndex),
        $"CSO/ZSO block index {blockIndex} outside [0, {layout.BlockCount}).");
    if (newUncompressedData.Length != layout.BlockSize)
      throw new ArgumentException(
        $"New block payload must be exactly block_size ({layout.BlockSize}) bytes; got {newUncompressedData.Length}.",
        nameof(newUncompressedData));

    var replacement = newUncompressedData.ToArray();
    var replacements = new Dictionary<int, byte[]> { [blockIndex] = replacement };
    using var logical = CsoImage.OpenLogicalStream(image, layout, replacements);
    using var staged = CreateScratchStream();
    CsoWriter.Write(staged, logical, layout.UncompressedSize, checked((int)layout.BlockSize), layout.Variant);

    staged.Position = 0;
    image.Position = 0;
    image.SetLength(0);
    staged.CopyTo(image);
    image.Flush();
  }

  // ── Header parsing retained for callers that only need geometry ───────

  internal sealed record CsoHeader(
    uint HeaderSize, ulong UncompressedSize, uint BlockSize, byte Version, byte Align,
    int BlockCount, uint[] IndexRaw, CsoVariant Variant);

  internal static CsoHeader ReadHeader(Stream image) {
    var layout = CsoImage.ReadLayout(image);
    return new CsoHeader(
      layout.HeaderSize, layout.UncompressedSize, layout.BlockSize, layout.Version, layout.Align,
      layout.BlockCount, layout.IndexRaw, layout.Variant);
  }

  internal static FileStream CreateScratchStream()
    => new(Path.Combine(Path.GetTempPath(), "cwb_cso_" + Guid.NewGuid().ToString("N") + ".tmp"),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
}
