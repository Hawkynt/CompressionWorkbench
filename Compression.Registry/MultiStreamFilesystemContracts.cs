#pragma warning disable CS1591
using System.Collections;

namespace Compression.Registry;

/// <summary>
/// Describes the purpose of one member in a multi-stream filesystem source set.
/// The role is intentionally generic: distributed filesystems may map metadata and
/// payload data to different backing filesystems, while RAID-like or journaled
/// formats can use the auxiliary roles without introducing format-specific fields.
/// </summary>
public enum FilesystemSourceRole {
  Unknown,
  Metadata,
  Data,
  Journal,
  Auxiliary,
}

/// <summary>
/// One named stream in a multi-stream filesystem source set.
/// </summary>
/// <param name="Name">Stable caller-facing member name used for diagnostics.</param>
/// <param name="Stream">Readable backing stream.</param>
/// <param name="Role">Semantic role of the member, when known.</param>
/// <param name="FormatId">
/// Optional registry id of the filesystem/container represented by <paramref name="Stream"/>.
/// Distributed filesystem providers use this to open the member through the normal
/// CompressionWorkbench parser stack rather than asking the host OS to mount it.
/// </param>
/// <param name="RootPath">
/// Root directory inside the member that belongs to the outer filesystem. <c>/</c>
/// means the member filesystem root.
/// </param>
public sealed record FilesystemStreamSource(
  string Name,
  Stream Stream,
  FilesystemSourceRole Role = FilesystemSourceRole.Unknown,
  string? FormatId = null,
  string RootPath = "/"
);

/// <summary>
/// Validated ordered collection of streams that together form one filesystem source.
/// Member names are unique case-insensitively so diagnostics and topology maps can use
/// them as stable keys. The set owns no streams; lifetime remains controlled by the
/// caller and <see cref="FilesystemOpenOptions.LeaveOpen"/> of the opened session.
/// </summary>
public sealed class FilesystemStreamSet : IReadOnlyList<FilesystemStreamSource> {
  private readonly FilesystemStreamSource[] _sources;

  /// <summary>Creates a validated source set.</summary>
  /// <param name="sources">Streams participating in the logical filesystem.</param>
  /// <exception cref="ArgumentException">
  /// Thrown when the set is empty, a name is blank/duplicated, or a stream is null.
  /// </exception>
  public FilesystemStreamSet(IEnumerable<FilesystemStreamSource> sources) {
    ArgumentNullException.ThrowIfNull(sources);
    _sources = sources.ToArray();
    if (_sources.Length == 0)
      throw new ArgumentException("A filesystem stream set must contain at least one source.", nameof(sources));

    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in _sources) {
      ArgumentNullException.ThrowIfNull(source);
      if (string.IsNullOrWhiteSpace(source.Name))
        throw new ArgumentException("Every filesystem stream source must have a non-empty name.", nameof(sources));
      ArgumentNullException.ThrowIfNull(source.Stream);
      if (!names.Add(source.Name))
        throw new ArgumentException($"Duplicate filesystem stream source name '{source.Name}'.", nameof(sources));
    }
  }

  /// <summary>Gets the number of participating streams.</summary>
  public int Count => _sources.Length;

  /// <summary>Gets one participating stream by ordinal.</summary>
  public FilesystemStreamSource this[int index] => _sources[index];

  /// <inheritdoc />
  public IEnumerator<FilesystemStreamSource> GetEnumerator()
    => ((IEnumerable<FilesystemStreamSource>)_sources).GetEnumerator();

  IEnumerator IEnumerable.GetEnumerator() => _sources.GetEnumerator();
}

/// <summary>
/// Descriptor-side entry point for filesystems whose logical namespace spans multiple
/// independent streams or backing filesystem images. Existing
/// <see cref="IFilesystemDriverProvider"/> implementations remain unchanged; a descriptor
/// may implement either or both contracts.
/// </summary>
public interface IMultiStreamFilesystemDriverProvider {
  /// <summary>Probes the supplied member set without mutating any source.</summary>
  FilesystemDriverProfile ProbeFilesystem(FilesystemStreamSet sources);

  /// <summary>Opens a logical filesystem session over the supplied member set.</summary>
  IFilesystemSession OpenFilesystem(FilesystemStreamSet sources, FilesystemOpenOptions options);
}

/// <summary>
/// Optional readiness report for a filesystem whose implementation consumes a
/// <see cref="FilesystemStreamSet"/> rather than one stream.
/// </summary>
public interface IMultiStreamFilesystemDriverReadinessProvider {
  /// <summary>Describes which mounted-driver layers are available for this exact source set.</summary>
  FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
    FilesystemStreamSet sources,
    FilesystemDriverTarget target);
}
