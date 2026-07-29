namespace Compression.Registry;

/// <summary>
/// Generic, round-trip-verified "extract → re-create" engine shared by the
/// default implementations of the maintenance verbs (shrink, defragment) for
/// any descriptor that can both enumerate/extract (<see cref="IArchiveFormatOperations"/>)
/// and create (<see cref="IArchiveCreatable"/>) its format.
///
/// <para>Every rebuild is <b>verified</b>: the freshly created image is listed
/// back and its live-file count compared against the source. If the rebuild
/// would drop files, the operation throws <see cref="InvalidOperationException"/>
/// instead of producing a lossy result — so enabling a verb on a format whose
/// create path doesn't faithfully round-trip fails loudly rather than silently
/// corrupting data. This is what makes broad, default-implementation rollout
/// across filesystems safe.</para>
/// </summary>
public static class RebuildVerb {

  /// <summary>
  /// Extracts every entry of <paramref name="input"/> and re-creates the image
  /// into <paramref name="output"/> via <paramref name="creator"/>. Returns the
  /// source live-file count. Throws if the rebuilt image lists fewer live files
  /// than the source (lossy round-trip) — the caller's <paramref name="output"/>
  /// should be discarded in that case.
  /// </summary>
  public static int RebuildToStream(Stream input, Stream output,
      IArchiveFormatOperations ops, IArchiveCreatable creator,
      IReadOnlyDictionary<string, string>? formatSpecific = null) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(ops);
    ArgumentNullException.ThrowIfNull(creator);

    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_rebuild_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmpDir);
    try {
      // Capture the source's live entry names (as a sorted MULTISET — duplicates
      // matter) as the descriptor itself reports them. This is the identity a
      // faithful rebuild must reproduce exactly.
      input.Position = 0;
      var sourceNames = LiveNameList(ops, input);
      var sourceFileCount = sourceNames.Count;

      input.Position = 0;
      ops.Extract(input, tmpDir, null, null);

      var inputs = new List<ArchiveInputInfo>();
      foreach (var dir in Directory.GetDirectories(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, dir).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo("", rel + "/", true));
      }
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo(file, rel, false));
      }

      var options = new FormatCreateOptions { FormatSpecific = formatSpecific };
      output.Position = 0;
      output.SetLength(0);
      creator.Create(output, inputs, options);

      // Identity guard: a faithful rebuild must list back the EXACT same set of
      // live entry names. Anything else — dropped, duplicated, renamed (e.g.
      // content-addressed/hashed names, synthetic metadata entries the writer
      // re-derives) — means the round-trip isn't identity-preserving, so refuse
      // to commit and leave the caller to fall back / keep the original.
      output.Position = 0;
      List<string> rebuiltNames;
      try {
        rebuiltNames = LiveNameList(ops, output);
      } catch (Exception ex) {
        throw new InvalidOperationException(
          $"Rebuilt image could not be listed back ({ex.GetType().Name}: {ex.Message}); refusing a lossy rebuild.", ex);
      }
      if (!rebuiltNames.SequenceEqual(sourceNames, StringComparer.Ordinal))
        throw new InvalidOperationException(
          $"Rebuild changed the entry set ({sourceFileCount} → {rebuiltNames.Count}); refusing a non-identity-preserving rebuild.");

      output.Position = 0;
      return sourceFileCount;
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }
  }

  /// <summary>The sorted multiset of live (non-directory) entry names the descriptor lists.</summary>
  private static List<string> LiveNameList(IArchiveFormatOperations ops, Stream stream) {
    stream.Position = 0;
    return ops.List(stream, null).Where(e => !e.IsDirectory).Select(e => e.Name)
      .OrderBy(n => n, StringComparer.Ordinal).ToList();
  }

  /// <summary>
  /// In-place rebuild: re-creates <paramref name="archive"/> from its own
  /// contents (consolidating live data — the defragmentation side effect of the
  /// rebuild-via-WORM pattern) and overwrites the stream only when the rebuild
  /// is verified to round-trip. On any failure the original bytes are left
  /// untouched.
  /// </summary>
  public static void RebuildInPlace(Stream archive,
      IArchiveFormatOperations ops, IArchiveCreatable creator,
      IReadOnlyDictionary<string, string>? formatSpecific = null) {
    ArgumentNullException.ThrowIfNull(archive);
    // A scratch file, not a MemoryStream: the rebuilt image is a whole volume,
    // and a MemoryStream cannot hold one past 2 GB ("Stream was too long").
    using var rebuilt = CreateScratchStream();
    // Throws (leaving `archive` untouched) if the rebuild would lose data.
    RebuildToStream(archive, rebuilt, ops, creator, formatSpecific);
    archive.Position = 0;
    archive.SetLength(0);
    rebuilt.Position = 0;
    rebuilt.CopyTo(archive);
    archive.Flush();
  }

  /// <summary>
  /// Rebuild-based in-place edit shared by the default <see cref="IArchiveModifiable"/>:
  /// extract the archive, apply <paramref name="mutate"/> to the extracted file
  /// tree (add/overwrite/delete real files on disk), re-create the image, and
  /// overwrite the stream. The original bytes are left untouched on any failure.
  /// </summary>
  public static void EditViaRebuild(Stream archive, IArchiveFormatOperations ops,
      IArchiveCreatable creator, Action<string> mutate) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(mutate);
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_edit_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmpDir);
    try {
      archive.Position = 0;
      ops.Extract(archive, tmpDir, null, null);

      mutate(tmpDir);

      var inputs = new List<ArchiveInputInfo>();
      foreach (var dir in Directory.GetDirectories(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, dir).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo("", rel + "/", true));
      }
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        inputs.Add(new ArchiveInputInfo(file, rel, false));
      }

      using var rebuilt = CreateScratchStream();
      creator.Create(rebuilt, inputs, new FormatCreateOptions());

      // Verify the result lists back (a valid image) before committing.
      rebuilt.Position = 0;
      _ = ops.List(rebuilt, null);

      archive.Position = 0;
      archive.SetLength(0);
      rebuilt.Position = 0;
      rebuilt.CopyTo(archive);
      archive.Flush();
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }
  }
  /// <summary>
  /// A writable scratch stream that is not bounded by what a byte[] can hold.
  /// Deleted on close.
  /// </summary>
  internal static FileStream CreateScratchStream()
    => new(Path.Combine(Path.GetTempPath(), "cwb_rebuild_" + Guid.NewGuid().ToString("N") + ".tmp"),
           FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
           FileOptions.DeleteOnClose);

}
