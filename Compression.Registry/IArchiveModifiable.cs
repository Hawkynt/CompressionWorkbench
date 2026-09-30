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
}

/// <summary>
/// Renames entries without touching anything else about the container. Only formats that can do
/// that losslessly implement it: every entry keeps its data, timestamps, permissions and metadata,
/// and the container keeps its comment, geometry and label. A format that could only rename by
/// re-creating itself does not implement it and is not offered a rename at all.
/// </summary>
public interface IArchiveRenamable {
  /// <summary>
  /// Writes to <paramref name="destination"/> a copy of <paramref name="source"/> in which the
  /// entries are renamed — a folder with everything beneath it — and nothing else differs.
  /// </summary>
  /// <exception cref="FileNotFoundException">A rename names an entry that is not there.</exception>
  /// <exception cref="IOException">A new name is already taken.</exception>
  void Rename(Stream source, Stream destination, IReadOnlyList<ArchiveRename> renames);
}

/// <summary>One entry, or one folder with everything beneath it, moved to a new path in the same instance.</summary>
/// <param name="From">The current entry path, <c>/</c>-separated; a trailing <c>/</c> is optional for a folder.</param>
/// <param name="To">The new entry path.</param>
public readonly record struct ArchiveRename(string From, string To);

/// <summary>Resolves renames against the entry names a container actually holds.</summary>
public static class ArchiveRenames {
  /// <summary>
  /// Every stored name <paramref name="renames"/> affect, mapped to its new name: a file by its exact
  /// path, a folder with every name beneath it. Names are compared exactly — two entries differing
  /// only in case are two entries. A leading <c>/</c> or a trailing <c>/</c> on a stored name is kept.
  /// </summary>
  /// <exception cref="ArgumentException">A path is empty or leaves the tree.</exception>
  /// <exception cref="FileNotFoundException">A rename matches nothing.</exception>
  /// <exception cref="IOException">
  /// A new name is taken — also when it differs from an existing one only in case, since such a
  /// pair cannot coexist once extracted on Windows or macOS — or a folder would move into itself.
  /// </exception>
  public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyList<ArchiveRename> renames, IReadOnlyCollection<string> names) {
    ArgumentNullException.ThrowIfNull(renames);
    ArgumentNullException.ThrowIfNull(names);

    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (fromName, toName) in renames) {
      var from = Normalize(fromName);
      var to = Normalize(toName);
      if (to.StartsWith(from + "/", StringComparison.Ordinal))
        throw new IOException($"'{from}' cannot be moved into itself.");

      var matched = false;
      foreach (var name in names) {
        var (lead, bare, trail) = Split(name);
        string? renamed = bare == from ? to
          : bare.StartsWith(from + "/", StringComparison.Ordinal) ? to + bare[from.Length..]
          : null;
        if (renamed is null) continue;

        matched = true;
        if (renamed != bare) map[name] = lead + renamed + trail;
      }

      if (!matched) throw new FileNotFoundException($"No entry named '{from}'.", from);
    }

    var stay = new HashSet<string>(names.Where(n => !map.ContainsKey(n)).Select(n => Split(n).Bare), StringComparer.OrdinalIgnoreCase);
    var arriving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var renamed in map.Values.Select(n => Split(n).Bare))
      if (stay.Contains(renamed) || !arriving.Add(renamed))
        throw new IOException($"'{renamed}' already exists.");

    return map;
  }

  private static (string Lead, string Bare, string Trail) Split(string name) {
    var lead = name.StartsWith('/') ? "/" : "";
    var trail = name.Length > lead.Length && name.EndsWith('/') ? "/" : "";
    return (lead, name[lead.Length..(name.Length - trail.Length)], trail);
  }

  private static string Normalize(string? path) {
    var normalized = (path ?? "").Replace('\\', '/').Trim('/');
    if (normalized.Length == 0) throw new ArgumentException("An entry path cannot be empty.", nameof(path));
    if (normalized.Split('/').Any(part => part is "" or "." or ".."))
      throw new ArgumentException($"'{path}' is not a valid entry path.", nameof(path));
    return normalized;
  }
}
