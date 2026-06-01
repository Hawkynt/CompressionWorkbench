namespace Compression.Registry.Layout;

/// <summary>
/// One zone within a <see cref="LayoutTemplate"/>: a byte-range region that
/// holds files matching <see cref="Filter"/>, in the order specified by
/// <see cref="SortBy"/>.
/// </summary>
public sealed record LayoutZone {
  /// <summary>Human-readable zone name (used in UI / logs).</summary>
  public required string Name { get; init; }

  /// <summary>
  /// Range expression resolved by <see cref="RangeSpec.Parse"/>:
  /// <c>"0%-5%"</c>, <c>"0-1MB"</c>, <c>"[16384, 32768)"</c>, etc.
  /// </summary>
  public required string Range { get; init; }

  /// <summary>
  /// Optional filter expression parsed by <see cref="FilterExpression.Parse"/>.
  /// <c>null</c> = no filter (every file matches; in practice the first zone
  /// catches everything).
  /// </summary>
  public string? Filter { get; init; }

  /// <summary>
  /// Sort keys applied within this zone (later keys break ties of earlier
  /// ones). Empty list = no explicit ordering; files keep their input order.
  /// </summary>
  public IReadOnlyList<DefragSortKey> SortBy { get; init; } = [];
}
