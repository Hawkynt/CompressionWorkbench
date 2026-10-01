#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Moves chunks within a single file and patches internal offset pointers
/// so the file remains valid. Examples: moving MP4 moov atom before mdat,
/// relocating JPEG EXIF to the front, compacting ID3v2 padding.
/// </summary>
/// <remarks>
/// Moving chunks re-encodes nothing and keeps every chunk, so it is the in-place form of
/// <see cref="IArchiveCanonicalizable"/>: the <c>canonicalize</c> maintenance operation runs
/// <see cref="Optimize(Stream)"/> on a copy and verifies the copy before it is kept.
/// </remarks>
public interface IFileInternalChunkMover : IArchiveCanonicalizable {
  /// <summary>
  /// Performs the canonical optimization for the format (e.g., MP4 fast-start,
  /// JPEG EXIF-first). The stream must be readable, writable, and seekable.
  /// If the file is already in the optimal layout, this is a no-op.
  /// </summary>
  void Optimize(Stream file);

  /// <summary>
  /// Performs optimization with an optional metadata placement profile that
  /// controls where metadata chunks land relative to the data payload.
  /// The default implementation ignores the profile and delegates to
  /// <see cref="Optimize(Stream)"/>.
  /// </summary>
  void Optimize(Stream file, MetadataPlacementProfile? profile) => Optimize(file);

  /// <summary>
  /// Copies <paramref name="input"/> to <paramref name="output"/> and canonicalizes the copy
  /// in place with <see cref="Optimize(Stream)"/>.
  /// </summary>
  void IArchiveCanonicalizable.Canonicalize(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanRead || !output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Canonicalizing in place needs a readable, writable, seekable target.", nameof(output));
    if (input.CanSeek) input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
    output.Position = 0;
    this.Optimize(output);
    output.Flush();
    output.Position = 0;
  }
}
