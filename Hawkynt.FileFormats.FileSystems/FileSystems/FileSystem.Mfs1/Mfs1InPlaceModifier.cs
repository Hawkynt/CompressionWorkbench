#pragma warning disable CS1591
namespace FileSystem.Mfs1;

/// <summary>
/// In-place modifier for Acorn MFS-1 (DFS-tier) disk images. MFS-1's catalog is the
/// Acorn DFS one — sector 0 (title and names) + sector 1 (title tail, counts, boot option
/// and metadata) — so edits go through <see cref="FileSystem.Bbc.BbcModifier"/>: only the
/// two catalogue sectors and the file's own sectors are written, and every other file
/// keeps its name, directory letter, lock, load and execution address and sectors.
/// </summary>
/// <remarks>
/// The image used to be re-packed through <see cref="Mfs1Writer"/> from the names the
/// reader lists, which the writer read as "D.NAME": "A.TXT" became file "TXT" in directory
/// "A", and the title, boot option, locks and load/execution addresses were lost.
/// Removed files' sectors are zeroed so no forensic trace remains.
/// </remarks>
public static class Mfs1InPlaceModifier {

  /// <summary>
  /// Adds — or replaces by name — files in an existing MFS-1 image. The
  /// image is re-packed from its existing files plus the new ones; the
  /// outer sector count is preserved.
  /// </summary>
  public static void AddFiles(Stream archive, IReadOnlyList<(string Name, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in inputs) {
      ArgumentNullException.ThrowIfNull(name);
      ArgumentNullException.ThrowIfNull(data);
      // "D.NAME" goes into directory D, as Create reads it; a file of that name is replaced.
      var (catalogName, directory) = Mfs1Writer.CatalogName(name);
      RemoveEntry(archive, catalogName, directory);
      FileSystem.Bbc.BbcModifier.AddEntry(archive, catalogName, data, directory, loadAddr: 0, execAddr: 0);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing MFS-1 image. Returns
  /// the number of entries removed.
  /// </summary>
  public static int RemoveFiles(Stream archive, IReadOnlyList<string> names) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(names);

    var removed = 0;
    foreach (var name in names) {
      ArgumentNullException.ThrowIfNull(name);
      var (catalogName, directory) = Mfs1Writer.CatalogName(name);
      if (RemoveEntry(archive, catalogName, directory)) ++removed;
    }
    return removed;
  }

  /// <summary>Removes the entry named exactly so in that directory; true when there was one.</summary>
  private static bool RemoveEntry(Stream archive, string catalogName, char directory)
    => FileSystem.Bbc.BbcModifier.RemoveEntry(archive, catalogName, directory, wipeData: true);
}
