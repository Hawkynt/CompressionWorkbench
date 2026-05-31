namespace Compression.Registry;

/// <summary>
/// A filesystem descriptor that can analyse and optimise its own layout parameters
/// without loading the full image into memory, enabling seamless operation on images
/// of any size (including multi-TB exFAT or ext4 volumes).
///
/// <para><b>In-place patches</b> (volume label, serial number, geometry fields):
/// implemented by seeking to the known superblock/BPB offset and overwriting
/// a handful of bytes. Zero copy; no allocation; works at any scale.</para>
///
/// <para><b>Structural changes</b> (cluster size, inode size, FAT type, block size):
/// require a streaming rebuild via <see cref="RebuildStreaming"/>. The source is
/// read sequentially; the target is written sequentially. Peak memory use is
/// bounded by <c>O(max(FAT-table, directory-tree))</c>, not by image size.</para>
/// </summary>
public interface ILayoutOptimizable {
  /// <summary>
  /// Reads only the superblock / BPB of <paramref name="image"/> to determine the
  /// current layout parameters and compute the optimal alternatives.
  /// The stream must be readable and seekable but is never fully loaded.
  /// </summary>
  LayoutAnalysis AnalyzeLayout(Stream image);

  /// <summary>
  /// Applies metadata-only changes (volume label, serial number, geometry CHS
  /// fields, etc.) by seeking directly to the relevant superblock offsets.
  /// Throws <see cref="NotSupportedException"/> for changes that would require
  /// moving data clusters (e.g. cluster-size change) — call
  /// <see cref="RebuildStreaming"/> for those.
  /// </summary>
  /// <param name="image">A readable, writable, seekable stream.</param>
  /// <param name="patch">The set of metadata fields to overwrite in-place.</param>
  void PatchInPlace(Stream image, LayoutPatch patch);

  /// <summary>
  /// Converts <paramref name="source"/> to <paramref name="target"/> with the
  /// layout parameters in <paramref name="options"/>. Reads and writes
  /// sequentially — never loads the full source into memory. Suitable for
  /// images of any size; typical peak allocation is O(cluster-size + FAT-sector).
  /// </summary>
  void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options);
}

// ── Value types ─────────────────────────────────────────────────────────────

/// <summary>Result of <see cref="ILayoutOptimizable.AnalyzeLayout"/>.</summary>
public sealed class LayoutAnalysis {
  /// <summary>Total image size in bytes as read from the superblock.</summary>
  public long ImageSize { get; init; }

  /// <summary>Current allocation-unit size in bytes (cluster, block, …).</summary>
  public int CurrentUnitSize { get; init; }

  /// <summary>Total internal slack at the current unit size in bytes.</summary>
  public long CurrentSlackBytes { get; init; }

  /// <summary>Optimal unit size chosen by <c>Compression.Core.Layout.FilesystemLayoutOptimizer</c>.</summary>
  public int OptimalUnitSize { get; init; }

  /// <summary>Total internal slack at the optimal unit size in bytes.</summary>
  public long OptimalSlackBytes { get; init; }

  /// <summary>Bytes that could be saved by switching to <see cref="OptimalUnitSize"/>.</summary>
  public long PotentialSavingsBytes => CurrentSlackBytes - OptimalSlackBytes;

  /// <summary>Metadata changes that can be applied by <see cref="ILayoutOptimizable.PatchInPlace"/>.</summary>
  public IReadOnlyList<string> InPlaceChanges { get; init; } = [];

  /// <summary>Structural changes that require <see cref="ILayoutOptimizable.RebuildStreaming"/>.</summary>
  public IReadOnlyList<string> RequiresRebuild { get; init; } = [];

  /// <summary>Free-form notes from the analyser (warnings, recommendations, etc.).</summary>
  public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Metadata fields that <see cref="ILayoutOptimizable.PatchInPlace"/> can
/// overwrite without touching data clusters.
/// </summary>
public sealed class LayoutPatch {
  /// <summary>New volume label. Null = leave unchanged.</summary>
  public string? VolumeLabel { get; init; }

  /// <summary>New volume serial number. Null = leave unchanged.</summary>
  public uint? SerialNumber { get; init; }

  /// <summary>Additional filesystem-specific fields keyed by name.</summary>
  public IReadOnlyDictionary<string, string>? Extra { get; init; }
}

/// <summary>
/// Target parameters for <see cref="ILayoutOptimizable.RebuildStreaming"/>.
/// </summary>
public sealed class LayoutRebuildOptions {
  /// <summary>Target allocation unit size in bytes. 0 = auto-select optimal.</summary>
  public int UnitSize { get; init; }

  /// <summary>Target image total size in bytes. 0 = auto-size to fit files.</summary>
  public long ImageSize { get; init; }

  /// <summary>
  /// Format-specific tunable parameters (same keys as
  /// <see cref="FormatOptionDescriptor.Key"/>). Merged with auto-selected
  /// values; explicit entries win.
  /// </summary>
  public IReadOnlyDictionary<string, string>? Parameters { get; init; }

  /// <summary>
  /// Optional progress callback: (bytesRead, totalBytes). Called after each
  /// cluster or metadata region is processed.
  /// </summary>
  public Action<long, long>? OnProgress { get; init; }
}
