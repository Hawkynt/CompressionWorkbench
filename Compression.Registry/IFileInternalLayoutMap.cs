#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Exposes the byte-level internal structure of a single file (not an archive
/// or filesystem) so the block-chart can visualize and rearrange its chunks.
/// Examples: JPEG APP markers, MP4 atoms, RIFF chunks, ID3 tags, PNG chunks.
/// </summary>
public interface IFileInternalLayoutMap {
  /// <summary>
  /// Enumerates the top-level structural chunks inside <paramref name="file"/>.
  /// Each chunk becomes a <see cref="DefragBlockInfo"/> with its byte offset
  /// and length within the file. The stream's position may be modified during
  /// enumeration but the caller owns the lifetime — implementations must not
  /// dispose <paramref name="file"/>.
  /// </summary>
  IEnumerable<DefragBlockInfo> EnumerateChunks(Stream file);
}
