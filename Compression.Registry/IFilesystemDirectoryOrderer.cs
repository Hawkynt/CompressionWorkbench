namespace Compression.Registry;

/// <summary>
/// Opt-in capability for changing the physical order of filesystem directory
/// entries without moving file payload extents merely for that purpose.
/// </summary>
public interface IFilesystemDirectoryOrderer {
  /// <summary>
  /// Reorders directory entries in <paramref name="image"/> according to the
  /// format's canonical or locality-oriented order.
  /// </summary>
  void SortDirectoryEntries(Stream image);
}
