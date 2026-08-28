namespace Compression.Registry;

/// <summary>
/// Opt-in capability for editing an existing archive/image through add/replace/remove.
/// <para>
/// The physical strategy is format-specific: implementations may patch blocks in place,
/// append replacement metadata, relayout members, or perform a verified extract → edit →
/// re-create rebuild. All are valid implementations of the same public mutation contract
/// when the resulting instance preserves the semantics the descriptor claims to support.
/// </para>
/// <para>
/// A descriptor advertising <see cref="FormatCapabilities.CanModify"/> must expose this
/// interface and its supported-profile edit path must actually round-trip. Merely being able
/// to create a fresh instance is not enough. A fully modifiable container is also purgeable:
/// removing all live entries is a required subset of the remove contract.
/// </para>
/// </summary>
public interface IArchiveModifiable : IArchivePurgeable {
  /// <summary>
  /// Adds files to an existing instance, replacing entries with the same logical path/name.
  ///
  /// <para><b>Default implementation</b>: descriptors that also implement
  /// <see cref="IArchiveFormatOperations"/> and <see cref="IArchiveCreatable"/> get a verified
  /// extract → edit → re-create implementation through <see cref="RebuildVerb.EditViaRebuild"/>.
  /// Formats with a cheaper native editor override it.</para>
  /// </summary>
  void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new System.NotSupportedException(
        "The default Add requires the descriptor to also implement IArchiveFormatOperations + IArchiveCreatable.");
    RebuildVerb.EditViaRebuild(archive, ops, creator, tmpDir => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
        var dest = Path.Combine(tmpDir, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.WriteAllBytes(dest, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes the named entries from an existing instance. Passing every entry name yields an
  /// empty container/image where the format permits one.
  ///
  /// <para><b>Default implementation</b>: a verified extract → drop-named-files → re-create
  /// edit through <see cref="RebuildVerb.EditViaRebuild"/>. Native implementations may instead
  /// unlink/free in place and optionally wipe released storage.</para>
  /// </summary>
  void Remove(Stream archive, string[] entryNames) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new System.NotSupportedException(
        "The default Remove requires the descriptor to also implement IArchiveFormatOperations + IArchiveCreatable.");
    var skip = new HashSet<string>(entryNames ?? [], System.StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, ops, creator, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }
}
