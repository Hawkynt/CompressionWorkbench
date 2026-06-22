using Compression.Registry;
using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// The <c>reconfigure</c> verb: change an existing container's geometry/options
/// (e.g. FAT cluster size or root-directory entries, NTFS MFT record size,
/// image size) <em>after</em> creation, without losing any data.
///
/// <para>Reconfigure is a verified extract → re-create round-trip. The contents
/// are extracted to a temp tree and the container is re-created with the
/// caller-supplied options threaded straight into the writer's
/// <see cref="FormatCreateOptions.FormatSpecific"/> bag. The rebuilt image is
/// listed back and its live-entry multiset compared against the source; the swap
/// onto the original path only happens when the round-trip is identity-preserving.
/// On any failure — unsupported format, a writer that drops/renames entries, an
/// I/O error — the original file is left byte-for-byte untouched.</para>
///
/// <para>Unlike <see cref="CompactOperation"/>'s minimal-geometry rebuild,
/// reconfigure does <b>not</b> require the result to be smaller: changing the
/// geometry is the goal, and a larger cluster size can legitimately grow the
/// image. The only invariant is that the live contents survive unchanged.</para>
/// </summary>
public static class ReconfigureOperation {

  /// <summary>Outcome of a reconfigure pass.</summary>
  /// <param name="OriginalSize">Container size before reconfiguring, in bytes.</param>
  /// <param name="NewSize">Container size after reconfiguring, in bytes.</param>
  /// <param name="AppliedOptions">The format-specific options that were applied.</param>
  /// <param name="FileCount">Number of live (non-directory) entries preserved.</param>
  public sealed record ReconfigureResult(
    long OriginalSize, long NewSize,
    IReadOnlyDictionary<string, string> AppliedOptions, int FileCount);

  /// <summary>
  /// Re-creates the container at <paramref name="path"/> in place with the
  /// supplied <paramref name="newOptions"/> as the format-specific tunables.
  /// Live contents are preserved byte-for-byte; only geometry / layout changes.
  /// </summary>
  /// <param name="path">Path to the existing container.</param>
  /// <param name="newOptions">Format-specific knobs to apply (keys/values match
  /// the format's <see cref="IFormatOptionsSchema"/>). Forwarded verbatim to the
  /// writer; unknown keys are ignored by the writer.</param>
  /// <param name="password">Password for an encrypted source/target (optional).</param>
  /// <exception cref="FileNotFoundException">The container does not exist.</exception>
  /// <exception cref="NotSupportedException">The detected format cannot be re-created.</exception>
  /// <exception cref="InvalidOperationException">The verified rebuild would not
  /// round-trip (entry set changed) — the original is left untouched.</exception>
  public static ReconfigureResult Reconfigure(string path,
      IReadOnlyDictionary<string, string> newOptions, string? password = null) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentNullException.ThrowIfNull(newOptions);
    if (!File.Exists(path)) throw new FileNotFoundException("Container not found.", path);

    FormatRegistration.EnsureInitialized();
    var originalSize = new FileInfo(path).Length;
    var format = FormatDetector.Detect(path);
    var formatId = format.ToString();
    var ops = FormatRegistry.GetArchiveOps(formatId);

    if (ops is not IArchiveCreatable)
      throw new NotSupportedException(
        $"Format {formatId} cannot be reconfigured — it does not support re-creation.");

    // Snapshot the source's live entry multiset BEFORE touching anything, so we
    // can verify the rebuild preserved exactly the same set of files.
    var sourceNames = LiveNames(path, password);

    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_reconfig_" + Guid.NewGuid().ToString("N")[..8]);
    // Keep the original extension so the rebuilt image still content/extension-
    // detects as the same format when we re-list it for the verification step
    // (weak-magic formats like FAT lean on the extension).
    var tempOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
      Path.GetFileNameWithoutExtension(path) + ".reconfig-" + Guid.NewGuid().ToString("N")[..6]
        + Path.GetExtension(path));
    try {
      Directory.CreateDirectory(tempDir);
      ArchiveOperations.Extract(path, tempDir, password, files: null);
      var inputs = ArchiveOperations.EnumerateTempInputs(tempDir);

      ArchiveOperations.Create(tempOut, inputs,
        new CompressionOptions { Password = password },
        format, newOptions);

      // Identity guard (mirrors RebuildVerb): the rebuilt image must list back
      // the EXACT same set of live entry names. Anything else means the
      // round-trip isn't faithful, so refuse the swap and keep the original.
      List<string> rebuiltNames;
      try {
        rebuiltNames = LiveNames(tempOut, password);
      } catch (Exception ex) {
        throw new InvalidOperationException(
          $"Reconfigured image could not be listed back ({ex.GetType().Name}: {ex.Message}); "
          + "refusing a lossy rebuild — original left untouched.", ex);
      }
      if (!rebuiltNames.SequenceEqual(sourceNames, StringComparer.Ordinal))
        throw new InvalidOperationException(
          $"Reconfigure changed the entry set ({sourceNames.Count} → {rebuiltNames.Count}); "
          + "refusing a non-identity-preserving rebuild — original left untouched.");

      var newSize = new FileInfo(tempOut).Length;
      File.Move(tempOut, path, overwrite: true);
      return new ReconfigureResult(originalSize, newSize, newOptions, rebuiltNames.Count);
    } finally {
      if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
      if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { /* best effort */ }
    }
  }

  /// <summary>The sorted multiset of live (non-directory) entry names a container lists.</summary>
  private static List<string> LiveNames(string path, string? password)
    => ArchiveOperations.List(path, password)
      .Where(e => !e.IsDirectory)
      .Select(e => e.Name)
      .OrderBy(n => n, StringComparer.Ordinal)
      .ToList();
}
