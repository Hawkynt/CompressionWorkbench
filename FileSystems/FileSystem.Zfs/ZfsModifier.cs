#pragma warning disable CS1591
namespace FileSystem.Zfs;

/// <summary>
/// Modifier for ZFS pool images produced by <see cref="ZfsWriter"/>.
/// <para>
/// <see cref="AddOrReplace"/> first attempts a genuine copy-on-write in-place add
/// via <see cref="ZfsInPlaceAdder"/>: it writes NEW (CoW) blocks for the changed
/// path only — the file data block + its dnode, the rewritten ROOT-directory
/// micro-ZAP, the dataset dnode array, the dataset and MOS objset blocks, and a new
/// uberblock (txg+1) into the next slot of every label's uberblock array — leaving
/// every untouched data block byte-identical at its offset and the image size
/// unchanged. The result round-trips through <see cref="ZfsReader"/> (added and
/// existing files list and extract byte-identically), and Fletcher-4 is recomputed
/// for every new block.
/// </para>
/// <para>
/// Cases the in-place adder does not handle — nested directories, a multi-block
/// (indirect) dnode array, a fat-ZAP root directory, a full micro-ZAP or dnode-array
/// block, a file larger than a single 1&#160;MB data block, or a data area with no
/// free tail to append into — throw <see cref="NotSupportedException"/> and fall back
/// to the rebuild strategy: read every entry via <see cref="ZfsReader"/>, apply the
/// changes in memory, and emit a fresh image over the old bytes via
/// <see cref="ZfsWriter"/>. <see cref="Remove"/> is always rebuild-based (reclaiming
/// freed blocks in place is not implemented).
/// </para>
/// </summary>
public static class ZfsModifier {

  /// <summary>
  /// Adds or replaces files in <paramref name="archive"/>. Tries genuine in-place
  /// copy-on-write first (per file, in order); if any add hits an unhandled shape the
  /// whole batch falls back to a single rebuild that applies every change. Existing
  /// entries are preserved except those whose names are overridden by the new inputs.
  /// </summary>
  public static void AddOrReplace(Stream archive, IReadOnlyList<(string Name, byte[] Data)> toAddOrReplace) {
    if (TryAddInPlace(archive, toAddOrReplace))
      return;

    var size = archive.Length;
    archive.Position = 0;
    var reader = new ZfsReader(archive);
    var existing = new List<(string Name, byte[] Data)>();
    var overridden = new HashSet<string>(toAddOrReplace.Select(t => t.Name), StringComparer.Ordinal);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (overridden.Contains(entry.Name)) continue;
      existing.Add((entry.Name, reader.Extract(entry)));
    }
    foreach (var (name, data) in toAddOrReplace)
      existing.Add((name, data));

    var rebuilt = BuildImage(existing, size);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
    archive.Flush();
  }

  // Attempts the genuine copy-on-write in-place add for every requested file on a
  // working copy of the image. Returns true (and writes the result back) only when all
  // files were added in place; on the first unhandled shape it discards the working copy
  // and returns false so the caller rebuilds the whole batch from the untouched original.
  private static bool TryAddInPlace(Stream archive, IReadOnlyList<(string Name, byte[] Data)> toAddOrReplace) {
    if (toAddOrReplace.Count == 0)
      return false;

    archive.Position = 0;
    using var snapshot = new MemoryStream();
    archive.CopyTo(snapshot);
    var image = snapshot.ToArray();

    try {
      foreach (var (name, data) in toAddOrReplace)
        ZfsInPlaceAdder.AddFile(image, name, data);
    } catch (NotSupportedException) {
      return false;
    } catch (InvalidDataException) {
      return false;
    }

    archive.Position = 0;
    archive.Write(image);
    archive.SetLength(image.Length);
    archive.Flush();
    return true;
  }

  /// <summary>Rebuilds <paramref name="archive"/> without the named entries.</summary>
  public static void Remove(Stream archive, IReadOnlyCollection<string> names) {
    var size = archive.Length;
    archive.Position = 0;
    var reader = new ZfsReader(archive);
    var nameSet = new HashSet<string>(names, StringComparer.Ordinal);
    var keep = new List<(string Name, byte[] Data)>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (nameSet.Contains(entry.Name)) continue;
      keep.Add((entry.Name, reader.Extract(entry)));
    }

    var rebuilt = BuildImage(keep, size);
    archive.Position = 0;
    archive.Write(rebuilt);
    archive.SetLength(rebuilt.Length);
    archive.Flush();
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files, long imageSize) {
    var w = new ZfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms, imageSize);
    return ms.ToArray();
  }
}
