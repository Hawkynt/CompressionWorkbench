#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Moves chunks within a single file and patches internal offset pointers
/// so the file remains valid. Examples: moving MP4 moov atom before mdat,
/// relocating JPEG EXIF to the front, compacting ID3v2 padding.
/// </summary>
public interface IFileInternalChunkMover : IArchiveCanonicalizable {
  /// <summary>
  /// Canonicalizes the file in place (for example MP4 fast-start or metadata
  /// chunk ordering). The stream must be readable, writable, and seekable.
  /// If the file is already canonical, this is a no-op.
  /// </summary>
  void CanonicalizeInPlace(Stream file) => Optimize(file);

  /// <summary>
  /// Canonicalizes the file in place using an optional metadata placement
  /// profile. The default ignores the profile.
  /// </summary>
  void CanonicalizeInPlace(Stream file, MetadataPlacementProfile? profile)
    => CanonicalizeInPlace(file);

  /// <summary>
  /// Legacy spelling retained for source compatibility. Existing movers may
  /// continue implementing this member while canonical callers dispatch through
  /// <see cref="CanonicalizeInPlace(Stream)"/>.
  /// </summary>
  void Optimize(Stream file) =>
    throw new NotSupportedException(
      $"{GetType().Name} must implement {nameof(CanonicalizeInPlace)} or the legacy {nameof(Optimize)} method.");

  /// <summary>Legacy spelling retained for source compatibility.</summary>
  void Optimize(Stream file, MetadataPlacementProfile? profile)
    => CanonicalizeInPlace(file, profile);

  /// <inheritdoc />
  void IArchiveCanonicalizable.Canonicalize(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanRead || !output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Canonicalization target must be readable, writable, and seekable.", nameof(output));

    if (input.CanSeek) input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
    output.Position = 0;
    CanonicalizeInPlace(output);
    output.Position = 0;
  }
}
