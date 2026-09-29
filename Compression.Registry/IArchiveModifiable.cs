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

  /// <summary>Adds or replaces files while supplying operation-scoped credentials.</summary>
  /// <remarks>
  /// The default deliberately delegates to the legacy overload so every existing
  /// unencrypted modifier keeps its behavior unchanged. Password-aware formats
  /// override this overload and consume <see cref="ArchiveMutationOptions.Password"/>.
  /// </remarks>
  void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs, ArchiveMutationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    this.Add(archive, inputs);
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
    RebuildVerb.EditViaRebuild(archive, ops, creator, tmpDir => RebuildVerb.DeleteNamedEntries(tmpDir, entryNames ?? []));
  }

  /// <summary>Removes entries while supplying operation-scoped credentials.</summary>
  /// <remarks>
  /// The default delegates to the legacy overload; formats that need a password to
  /// locate or rewrite encrypted metadata can override this overload independently.
  /// </remarks>
  void Remove(Stream archive, string[] entryNames, ArchiveMutationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    this.Remove(archive, entryNames);
  }

  /// <summary>
  /// Gives entries new paths within an existing instance. A folder is renamed with everything
  /// beneath it; the renames apply in order, and none may land on a path that is already taken.
  ///
  /// <para><b>Default implementation</b>: a verified extract → move → re-create edit through
  /// <see cref="RebuildVerb.EditViaRebuild"/>, as Add and Remove do. Formats whose directory can be
  /// rewritten in place override it.</para>
  /// </summary>
  void Rename(Stream archive, IReadOnlyList<ArchiveRename> renames) {
    if (this is not IArchiveFormatOperations ops || this is not IArchiveCreatable creator)
      throw new System.NotSupportedException(
        "The default Rename requires the descriptor to also implement IArchiveFormatOperations + IArchiveCreatable.");
    ArgumentNullException.ThrowIfNull(renames);
    RebuildVerb.EditViaRebuild(archive, ops, creator, tmpDir => RebuildVerb.MoveEntries(tmpDir, renames));
  }

  /// <summary>Renames entries while supplying operation-scoped credentials.</summary>
  /// <remarks>The default delegates to the legacy overload, as Add and Remove do.</remarks>
  void Rename(Stream archive, IReadOnlyList<ArchiveRename> renames, ArchiveMutationOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    this.Rename(archive, renames);
  }
}

/// <summary>One entry, or one folder with everything beneath it, moved to a new path in the same instance.</summary>
/// <param name="From">The current entry path, <c>/</c>-separated; a trailing <c>/</c> is optional for a folder.</param>
/// <param name="To">The new entry path.</param>
public readonly record struct ArchiveRename(string From, string To);
