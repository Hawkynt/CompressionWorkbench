namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor exposes add / remove (and thereby the purge verb).
/// <para>
/// Implementing this interface makes the verbs <em>work</em>; it does <b>not</b> by itself
/// entitle the format to advertise <see cref="FormatCapabilities.CanModify"/> (R/W). The
/// default <see cref="Add"/> / <see cref="Remove"/> below — and any override that delegates
/// to <c>ModifyRebuilder</c> / <see cref="RebuildVerb"/> — are a verified extract → re-create
/// <em>rebuild</em>, i.e. a full rewrite of the container. A format whose modification is only
/// rebuild-backed is WORM: it advertises <see cref="FormatCapabilities.CanCreate"/> and must
/// NOT advertise <see cref="FormatCapabilities.CanModify"/> (see <see cref="FormatCapabilities"/>).
/// Reserve <see cref="FormatCapabilities.CanModify"/> for a genuine in-place writer that edits
/// the existing bytes (R/W filesystems; central-directory / member edits; byte-identity append).
/// </para>
/// </summary>
public interface IArchiveModifiable {
  /// <summary>
  /// Appends or replaces files inside <paramref name="archive"/>. On replacement the
  /// previous bytes are wiped the same way <see cref="Remove"/> wipes them.
  ///
  /// <para><b>Default implementation</b>: any descriptor that also implements
  /// <see cref="IArchiveFormatOperations"/> + <see cref="IArchiveCreatable"/> gets
  /// add for free — a verified extract → splat-new-files → re-create rebuild via
  /// <see cref="RebuildVerb.EditViaRebuild"/> (the same WORM rebuild that backs the
  /// other verbs). Formats with a true in-place writer override for efficiency.</para>
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
  /// Removes the named entries from <paramref name="archive"/> and wipes all on-disk
  /// traces. <b>Default implementation</b>: a verified extract → drop-named-files →
  /// re-create rebuild via <see cref="RebuildVerb.EditViaRebuild"/>. Passing every
  /// entry name (or all files) yields an empty container — i.e. the <em>purge</em> verb.
  /// Formats with a true in-place writer override for efficiency and forensic wiping.
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
