#pragma warning disable CS1591
namespace FileSystem.ReiserFs;

// ─────────────────────────────────────────────────────────────────────────────
// In-place ReiserFS v3.6 image mutation.
//
// Strategy: read-modify-rebuild via the proven multi-leaf writer.
//
// Earlier revisions kept a hand-written single-leaf splice path:
//   * walk down the S+tree, find the leaf that would hold the new key,
//   * snapshot every item, edit it in-memory, re-pack the leaf body.
// That works for trivial in-leaf edits but breaks the moment any of:
//   * the parent directory isn't the root (nested paths),
//   * the file body needs INDIRECT items (i.e. > leaf payload),
//   * the leaf would overflow (split required),
//   * the leaf would underflow on remove (merge required),
//   * the dirent's deh_offset would collide with an existing R5-hashed entry
//     and force a re-sort of the DIRENTRY item,
// is in play. The hand-written path lit up `NotSupportedException` for each of
// those — but reiserfsck rejected the few cases it DID accept because the
// modifier never re-hashed the new dirent with the writer's R5 hash, so the
// only working case was R5-by-luck.
//
// The honest fix: read the entire image to a (path, bytes) list and call the
// existing multi-leaf writer with the updated list. The writer already handles
// nested directories, R5-hashed dirents, leaf-splitting via PackLeaves, internal
// page emission, deh_offset bumping for hash collisions and reiserfsck-clean
// bitmap / sd_blocks accounting. The cost is O(image size) per Add / Remove
// rather than O(edit-locality), but for a WORM-style image editor this is the
// pragmatic ceiling.
//
// This collapses every previously-NotSupportedException branch into the rebuild
// path:
//   * nested paths        → writer's BuildTree creates intermediate directories
//   * leaf split / merge  → writer's PackLeaves greedily packs leaves
//   * multi-leaf descent  → writer always emits internal pages above leaves
//   * INDIRECT items      → writer's BuildLeafItems chooses DIRECT vs INDIRECT
//   * dirent ordering     → writer's BuildDirEntryItems R5-hashes every name
//   * sd_blocks accuracy  → writer's PatchStatDataBlockCounts is final-pass
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Reads-modifies-rebuilds a ReiserFS v3.6 image to add or remove files. Add and
/// Remove materialise every existing live entry from the input image, apply the
/// requested edit in memory, and call <see cref="ReiserFsWriter"/> to produce a
/// fresh spec-compliant image. The new image is written back into the same stream
/// in place.
/// </summary>
internal static class ReiserFsModifier {

  /// <summary>
  /// Adds (or replaces, by ordinal name match) a single file into the ReiserFS
  /// image. Existing entries are carried forward unchanged. The new image is
  /// built by <see cref="ReiserFsWriter"/> and atomically replaces the stream
  /// contents.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var flat = name.Replace('\\', '/').Trim('/');
    if (flat.Length == 0)
      throw new ArgumentException("name is empty", nameof(name));

    // ── Genuine in-place attempt first ────────────────────────────────────────
    // Splice the file into the live S+tree without re-emitting the image or
    // relocating existing data blocks. ReiserFsInPlaceAdder mutates the tree in
    // place — leaf split / merge, internal-node maintenance, tree-height growth,
    // nested directory creation and replace-by-name are all handled — and only
    // throws NotSupportedException for rare edge cases (over-long names, a single
    // item larger than a leaf). We run it on an in-memory copy of the current
    // image so a mid-edit failure never corrupts the stream — only on success do
    // we commit it.
    image.Position = 0;
    byte[] current;
    using (var snapshot = new MemoryStream()) {
      image.CopyTo(snapshot);
      current = snapshot.ToArray();
    }
    try {
      var inPlace = ReiserFsInPlaceAdder.AddFile(current, flat, data);
      image.Position = 0;
      image.Write(inPlace);
      image.SetLength(inPlace.Length);
      return;
    } catch (NotSupportedException) {
      // Fall through to the verified rebuild for structural cases the in-place
      // path does not yet handle.
    }

    image.Position = 0;
    var existing = ReadAllEntries(image);

    // Replace-by-name semantics: if a same-named entry already exists, drop the
    // old one so the new bytes win.
    existing.RemoveAll(e => string.Equals(e.Name, flat, StringComparison.Ordinal));
    existing.Add((flat, data));

    var rebuilt = BuildImage(existing);
    image.Position = 0;
    image.Write(rebuilt);
    image.SetLength(rebuilt.Length);
  }

  /// <summary>
  /// Removes the named entry from the ReiserFS image. Returns true if it was
  /// present and removed; false if no such name existed.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    _ = wipeData; // rebuild always starts from zeroed bytes — wipe is implicit.

    var flat = name.Replace('\\', '/').Trim('/');
    if (flat.Length == 0) return false;

    // ── Genuine in-place attempt first ────────────────────────────────────────
    // Drop the target's items + dirent from the live S+tree and free its
    // INDIRECT data blocks without relocating surviving data blocks. Handles
    // nested paths, directory targets (recursive) and multi-leaf images; falls
    // through to the rebuild only for rare edge cases.
    image.Position = 0;
    byte[] current;
    using (var snapshot = new MemoryStream()) {
      image.CopyTo(snapshot);
      current = snapshot.ToArray();
    }
    try {
      var inPlace = ReiserFsInPlaceAdder.RemoveFile(current, flat, wipeData);
      image.Position = 0;
      image.Write(inPlace);
      image.SetLength(inPlace.Length);
      return true;
    } catch (FileNotFoundException) {
      return false; // name not present — nothing to remove
    } catch (NotSupportedException) {
      // Fall through to rebuild for structural cases.
    }

    image.Position = 0;
    var existing = ReadAllEntries(image);
    var before = existing.Count;
    existing.RemoveAll(e => string.Equals(e.Name, flat, StringComparison.Ordinal));
    if (existing.Count == before) return false;

    var rebuilt = BuildImage(existing);
    image.Position = 0;
    image.Write(rebuilt);
    image.SetLength(rebuilt.Length);
    return true;
  }

  /// <summary>
  /// Reads every live (non-directory) entry from the supplied image into a
  /// (path, bytes) list. Directory objects are reconstructed implicitly from
  /// path components by <see cref="ReiserFsWriter.AddFile"/>, so we only
  /// materialise files here.
  /// </summary>
  private static List<(string Name, byte[] Data)> ReadAllEntries(Stream image) {
    var pos = image.Position;
    try {
      image.Position = 0;
      var reader = new ReiserFsReader(image);
      var list = new List<(string Name, byte[] Data)>(reader.Entries.Count);
      foreach (var entry in reader.Entries) {
        if (entry.IsDirectory) continue;
        list.Add((entry.Name.Replace('\\', '/'), reader.Extract(entry)));
      }
      return list;
    } finally {
      try { image.Position = pos; } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Builds a fresh ReiserFS image containing exactly the supplied file list.
  /// </summary>
  private static byte[] BuildImage(List<(string Name, byte[] Data)> files) {
    var writer = new ReiserFsWriter();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    using var ms = new MemoryStream();
    writer.WriteTo(ms);
    return ms.ToArray();
  }
}
