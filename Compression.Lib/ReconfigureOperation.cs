using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// The <c>reconfigure</c> verb — the geometry maintenance operation
/// (<see cref="MaintenanceCapability.ChangeGeometry"/>): lay an existing volume out again at
/// another allocation geometry (cluster size, image size, …) without losing anything.
/// </summary>
/// <remarks>
/// <para>Only formats whose relayout keeps everything they carry offer it
/// (<see cref="ILayoutOptimizable.RelayoutPreservesEverything"/>), only the keys their schema
/// tags <see cref="FormatOptionDescriptor.IsAllocationGeometry"/> may be set, and the result
/// replaces the original only when <see cref="ArchiveSemanticManifest"/> finds every path,
/// byte, timestamp and container property unchanged
/// (<see cref="MaintenanceVerbs.ChangeGeometry"/>).</para>
/// <para>The extract → re-create rebuild this used to run kept names and bytes and dropped
/// the label, serial, attributes, owners and timestamps of a real volume, so a format whose
/// relayout cannot carry those is refused with <see cref="NotSupportedException"/> and the
/// file is left byte for byte as it was.</para>
/// </remarks>
public static class ReconfigureOperation {

  /// <summary>Outcome of a reconfigure pass.</summary>
  /// <param name="OriginalSize">Container size before reconfiguring, in bytes.</param>
  /// <param name="NewSize">Container size after reconfiguring, in bytes.</param>
  /// <param name="AppliedOptions">The geometry options that were applied.</param>
  /// <param name="FileCount">Number of live (non-directory) entries preserved.</param>
  public sealed record ReconfigureResult(
    long OriginalSize, long NewSize,
    IReadOnlyDictionary<string, string> AppliedOptions, int FileCount);

  /// <summary>
  /// Lays the container at <paramref name="path"/> out again with <paramref name="newOptions"/>
  /// as its geometry, in place.
  /// </summary>
  /// <param name="path">Path to the existing container.</param>
  /// <param name="newOptions">Geometry keys and values from the format's options schema.</param>
  /// <param name="password">Password for an encrypted container (optional).</param>
  /// <exception cref="FileNotFoundException">The container does not exist.</exception>
  /// <exception cref="NotSupportedException">The format cannot change geometry losslessly, a key
  /// is not a geometry key, or the relayout would not keep everything — the original is untouched.</exception>
  public static ReconfigureResult Reconfigure(string path,
      IReadOnlyDictionary<string, string> newOptions, string? password = null) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentNullException.ThrowIfNull(newOptions);
    if (!File.Exists(path)) throw new FileNotFoundException("Container not found.", path);

    FormatRegistration.EnsureInitialized();
    var formatId = FormatDetector.Detect(path).ToString();
    var target = (object?)FormatRegistry.GetArchiveOps(formatId) ?? FormatRegistry.GetById(formatId)
      ?? throw new NotSupportedException($"'{Path.GetFileName(path)}' is not in a recognised format; nothing to reconfigure.");

    MaintenanceResult? result = null;
    AtomicFileWriter.WriteAtomic(path, output => {
      using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      result = MaintenanceVerbs.ChangeGeometry(target, input, output, newOptions, password);
    });

    var files = ArchiveOperations.List(path, password).Count(e => !e.IsDirectory);
    return new ReconfigureResult(result!.OriginalSize, result.NewSize, newOptions, files);
  }
}
