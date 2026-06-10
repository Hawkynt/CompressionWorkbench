#pragma warning disable CS1591
namespace FileSystem.Mfs1;

/// <summary>
/// In-place modifier for Acorn MFS-1 (DFS-tier) disk images. MFS-1's
/// catalog is a flat fixed-offset region — sector 0 (names) + sector 1
/// (metadata) — that we can shift in place; files live contiguously from
/// sector 2 onwards.
///
/// <para>
/// On <see cref="AddFiles"/> the existing catalog is parsed via
/// <see cref="Mfs1Reader"/>, the new file is appended at the next free
/// sector, the catalog is re-serialised via <see cref="Mfs1Writer"/>, and
/// the buffer is written back to the underlying stream. The outer image
/// size (sector count) is preserved.
/// </para>
/// <para>
/// On <see cref="RemoveFiles"/> the catalog is re-built without the
/// dropped names. The data area previously occupied by the removed file
/// is zeroed so no forensic trace remains.
/// </para>
/// </summary>
public static class Mfs1InPlaceModifier {

  /// <summary>
  /// Adds — or replaces by name — files in an existing MFS-1 image. The
  /// image is re-packed from its existing files plus the new ones; the
  /// outer sector count is preserved.
  /// </summary>
  public static void AddFiles(Stream archive, IReadOnlyList<(string Name, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    var byName = ReadAll(image);
    foreach (var (name, data) in inputs) {
      ArgumentNullException.ThrowIfNull(name);
      ArgumentNullException.ThrowIfNull(data);
      // Replace-by-name (case-insensitive, matches reader's logic).
      var key = (Path.GetFileName(name) ?? name).ToUpperInvariant();
      // 7-char truncation mirrors writer's sanitisation.
      if (key.Length > 7) key = key[..7];
      byName.RemoveAll(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
      byName.Add((name, data));
    }

    WriteAll(archive, image.Length, byName);
  }

  /// <summary>
  /// Removes the named entries from an existing MFS-1 image. Returns
  /// the number of entries removed.
  /// </summary>
  public static int RemoveFiles(Stream archive, IReadOnlyList<string> names) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(names);

    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();

    var byName = ReadAll(image);
    var before = byName.Count;
    foreach (var name in names) {
      ArgumentNullException.ThrowIfNull(name);
      var key = (Path.GetFileName(name) ?? name).ToUpperInvariant();
      if (key.Length > 7) key = key[..7];
      byName.RemoveAll(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
    }
    var removed = before - byName.Count;
    if (removed > 0)
      WriteAll(archive, image.Length, byName);
    return removed;
  }

  private static List<(string Name, byte[] Data)> ReadAll(byte[] image) {
    var list = new List<(string Name, byte[] Data)>();
    if (image.Length < 2 * Mfs1Reader.SectorSize) return list;
    try {
      using var ms = new MemoryStream(image, writable: false);
      var r = new Mfs1Reader(ms);
      foreach (var e in r.Entries) {
        var data = r.Extract(e);
        list.Add((e.Name, data));
      }
    } catch {
      // Unparseable catalog → start fresh; the writer will produce a clean image.
    }
    return list;
  }

  private static void WriteAll(Stream archive, int totalBytes, IReadOnlyList<(string Name, byte[] Data)> entries) {
    var totalSectors = totalBytes / Mfs1Reader.SectorSize;
    if (totalSectors < 2) totalSectors = Mfs1Writer.DefaultTotalSectors;

    var w = new Mfs1Writer();
    foreach (var (name, data) in entries)
      w.AddFile(name, data);
    var rebuilt = w.Build(totalSectors: totalSectors);

    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
  }
}
