using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// The <c>reconfigure</c> verb: change an existing container's geometry/options
/// (e.g. FAT cluster size or root-directory entries, NTFS MFT record size,
/// image size) <em>after</em> creation, without losing any data.
///
/// <para>Reconfigure is an <see cref="ILayoutOptimizable"/> operation. The
/// descriptor receives the requested parameters through
/// <see cref="LayoutRebuildOptions.Parameters"/> and writes a staged target.
/// The staged image is compared against a full
/// <see cref="SemanticPreservationManifest"/> before the atomic swap. Merely
/// being creatable is deliberately insufficient: create support does not imply
/// that after-creation geometry changes preserve every filesystem semantic.</para>
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
    var descriptor = FormatRegistry.GetById(formatId);
    var ops = FormatRegistry.GetArchiveOps(formatId);

    if (descriptor is not ILayoutOptimizable layout)
      throw new NotSupportedException(
        $"Format {formatId} cannot change allocation geometry — it does not implement ILayoutOptimizable.");
    if (ops == null)
      throw new NotSupportedException(
        $"Format {formatId} cannot verify a geometry rebuild — archive/filesystem operations are unavailable.");

    SemanticPreservationManifest sourceManifest;
    using (var source = File.OpenRead(path))
      sourceManifest = SemanticPreservationManifest.Capture(source, ops, password);

    var tempOut = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
      Path.GetFileName(path) + $".reconfigure.tmp.{Guid.NewGuid():N}");
    try {
      using (var source = File.OpenRead(path))
      using (var target = new FileStream(tempOut, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
        layout.RebuildStreaming(source, target, new LayoutRebuildOptions {
          Parameters = newOptions,
        });
        target.Flush(flushToDisk: true);
      }

      SemanticPreservationManifest rebuiltManifest;
      using (var rebuilt = File.OpenRead(tempOut))
        rebuiltManifest = SemanticPreservationManifest.Capture(rebuilt, ops, password);
      sourceManifest.VerifyEquivalent(rebuiltManifest);

      var newSize = new FileInfo(tempOut).Length;
      AtomicFileWriter.ReplaceTarget(tempOut, path);
      return new ReconfigureResult(originalSize, newSize, newOptions, rebuiltManifest.EntryCount);
    } finally {
      AtomicFileWriter.TryDelete(tempOut);
    }
  }

}
