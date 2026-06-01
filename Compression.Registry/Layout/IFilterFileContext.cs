namespace Compression.Registry.Layout;

/// <summary>
/// View of one file's metadata used by a <see cref="IFileFilter"/> and by
/// the layout-template sorter. Implementations are read-only and may
/// expose <c>null</c> for fields the underlying source can't supply
/// (e.g. classic FAT lacks atime; classic ProDOS lacks access timestamps
/// entirely). Filters comparing a missing field always evaluate to <c>false</c>.
///
/// <para>The <c>All*</c> properties give a filter access to the population
/// statistics so functions like <c>quartile(0.75)</c> can resolve to the
/// correct percentile for the field being compared. They MUST contain the
/// same number of entries as the file set being filtered and MUST be the
/// same instance across calls within a single resolve operation — the
/// filter caches percentile computations by reference.</para>
/// </summary>
public interface IFilterFileContext {
  /// <summary>File name (final path segment).</summary>
  string Name { get; }
  /// <summary>Full path, '/'-separated. Empty string when not nested.</summary>
  string Path { get; }
  /// <summary>Extension including leading dot, lower-case. Empty when none.</summary>
  string Extension { get; }
  /// <summary>Logical file size in bytes (sum of extent lengths).</summary>
  long Size { get; }
  /// <summary>Last-modified timestamp (UTC), or null when unavailable.</summary>
  DateTime? LastModified { get; }
  /// <summary>Last-accessed timestamp (UTC), or null when unavailable.</summary>
  DateTime? LastAccessed { get; }
  /// <summary>Creation timestamp (UTC), or null when unavailable.</summary>
  DateTime? Created { get; }
  /// <summary>Attribute bitmask (filesystem-specific encoding); 0 when none.</summary>
  uint Attributes { get; }

  /// <summary>All last-modified times across the file set. Used by quartile() on the LastModified field.</summary>
  IReadOnlyList<DateTime>? AllLastModifiedTimes { get; }
  /// <summary>All last-accessed times across the file set. Used by quartile() on the LastAccessed field.</summary>
  IReadOnlyList<DateTime>? AllLastAccessedTimes { get; }
  /// <summary>All creation times across the file set. Used by quartile() on the Created field.</summary>
  IReadOnlyList<DateTime>? AllCreatedTimes { get; }
  /// <summary>All sizes across the file set. Used by quartile() on the Size field.</summary>
  IReadOnlyList<long>? AllSizes { get; }
}

/// <summary>
/// A compiled filter expression. Created via <see cref="FilterExpression.Parse(string)"/>.
/// Returns <c>true</c> when a file matches the expression.
/// </summary>
public interface IFileFilter {
  /// <summary>Evaluates the filter against <paramref name="file"/>.</summary>
  bool Matches(IFilterFileContext file);
}

/// <summary>
/// Plain DTO implementation of <see cref="IFilterFileContext"/> used by
/// callers that build the filter context up front rather than wrapping
/// a live source.
/// </summary>
public sealed record FilterFileContext : IFilterFileContext {
  /// <inheritdoc/>
  public string Name { get; init; } = string.Empty;
  /// <inheritdoc/>
  public string Path { get; init; } = string.Empty;
  /// <inheritdoc/>
  public string Extension { get; init; } = string.Empty;
  /// <inheritdoc/>
  public long Size { get; init; }
  /// <inheritdoc/>
  public DateTime? LastModified { get; init; }
  /// <inheritdoc/>
  public DateTime? LastAccessed { get; init; }
  /// <inheritdoc/>
  public DateTime? Created { get; init; }
  /// <inheritdoc/>
  public uint Attributes { get; init; }
  /// <inheritdoc/>
  public IReadOnlyList<DateTime>? AllLastModifiedTimes { get; init; }
  /// <inheritdoc/>
  public IReadOnlyList<DateTime>? AllLastAccessedTimes { get; init; }
  /// <inheritdoc/>
  public IReadOnlyList<DateTime>? AllCreatedTimes { get; init; }
  /// <inheritdoc/>
  public IReadOnlyList<long>? AllSizes { get; init; }
}
