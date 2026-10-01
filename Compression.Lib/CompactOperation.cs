using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Composite maintenance verb: defragment extents, compress, shrink — each stage run only
/// where the format offers it (<see cref="MaintenanceCapabilities"/>), each one lossless or
/// skipped. It never changes geometry; that is <see cref="ReconfigureOperation"/>.
/// </summary>
public static class CompactOperation {
  /// <summary>Outcome of a compact pass.</summary>
  /// <param name="OriginalSize">Size before, in bytes.</param>
  /// <param name="NewSize">Size after, in bytes.</param>
  /// <param name="StepsRun">The stages that ran and changed the file, in order.</param>
  /// <param name="Minimal">Always false: the minimal-geometry rebuild is gone (see <see cref="CompactOptions.Minimal"/>).</param>
  public sealed record CompactResult(long OriginalSize, long NewSize, IReadOnlyList<string> StepsRun, bool Minimal);

  /// <summary>Options for <see cref="Compact"/>.</summary>
  public sealed class CompactOptions {
    /// <summary>
    /// Asked for the former minimal-geometry rebuild, which re-created the container and kept
    /// only names and bytes. That is refused now: changing geometry is its own operation
    /// (<see cref="ReconfigureOperation"/>), and it verifies what it keeps.
    /// </summary>
    public bool Minimal { get; init; }

    /// <summary>Password for an encrypted container.</summary>
    public string? Password { get; init; }

    /// <summary>Receives one line per stage.</summary>
    public Action<string>? Log { get; init; }
  }

  /// <summary>Runs defragment extents → compress → shrink on the file at <paramref name="path"/>, in place.</summary>
  /// <exception cref="NotSupportedException"><see cref="CompactOptions.Minimal"/> was set.</exception>
  public static CompactResult Compact(string path, CompactOptions? options = null) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    if (!File.Exists(path)) throw new FileNotFoundException("Container not found.", path);
    options ??= new CompactOptions();
    if (options.Minimal)
      throw new NotSupportedException(
        "compact no longer rebuilds at minimal geometry: that re-created the container and kept only names and bytes. "
        + "Change the geometry explicitly with 'reconfigure', which verifies what it keeps.");
    var log = options.Log ?? (_ => { });

    FormatRegistration.EnsureInitialized();
    var originalSize = new FileInfo(path).Length;
    var profile = MaintenanceOperations.Describe(path);
    var ops = FormatRegistry.GetArchiveOps(profile.FormatId);
    var steps = new List<string>();

    if (profile.Supports(MaintenanceCapability.DefragmentExtents) && ops is IArchiveDefragmentable defragmentable) {
      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        defragmentable.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
        steps.Add("defragment");
        log("defragment: consolidated live data at the start.");
      } catch (Exception ex) {
        log($"defragment: skipped ({ex.GetType().Name}: {ex.Message}).");
      }
    }

    if (profile.Supports(MaintenanceCapability.Compress)) {
      try {
        var r = MaintenanceOperations.Compress(path, path, options.Password);
        if (r.Changed) steps.Add("compress");
        log(r.Changed
          ? $"compress: {r.OriginalSize:N0} → {r.NewSize:N0} bytes."
          : "compress: no smaller verified encoding; kept as it was.");
      } catch (Exception ex) {
        log($"compress: skipped ({ex.GetType().Name}: {ex.Message}).");
      }
    }

    if (profile.Supports(MaintenanceCapability.Shrink) && ops is IArchiveShrinkable shrinkable) {
      var tempOut = path + ".compact-shrink.tmp";
      try {
        using (var input = File.OpenRead(path))
        using (var output = File.Create(tempOut))
          shrinkable.Shrink(input, output);
        var shrunkLen = new FileInfo(tempOut).Length;
        if (shrunkLen > 0 && shrunkLen < new FileInfo(path).Length) {
          File.Move(tempOut, path, overwrite: true);
          steps.Add("shrink");
          log($"shrink: reduced to {shrunkLen:N0} bytes.");
        } else {
          log("shrink: already compact (no reduction).");
        }
      } catch (Exception ex) {
        log($"shrink: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { /* best effort */ }
      }
    }

    return new CompactResult(originalSize, new FileInfo(path).Length, steps, Minimal: false);
  }
}
