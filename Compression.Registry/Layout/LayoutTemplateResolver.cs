namespace Compression.Registry.Layout;

/// <summary>
/// One file's resolved placement: which zone it belongs to, the zone's
/// concrete byte bounds, and its rank within the zone after sorting.
/// </summary>
/// <param name="FileIndex">Index into the input file list passed to
/// <see cref="LayoutTemplateResolver.Resolve"/>.</param>
/// <param name="ZoneName">Resolved zone name. Pseudo-zones for unmatched
/// files use <c>"&lt;leftover&gt;"</c>.</param>
/// <param name="ZoneStart">Inclusive byte start of the zone.</param>
/// <param name="ZoneEnd">Exclusive byte end of the zone.</param>
/// <param name="SortIndex">0-based rank within the zone after sort keys
/// have been applied.</param>
public sealed record ResolvedFilePlacement(
  int FileIndex,
  string ZoneName,
  long ZoneStart,
  long ZoneEnd,
  int SortIndex);

/// <summary>
/// Resolves a <see cref="LayoutTemplate"/> against a real file set into a
/// concrete placement plan (one <see cref="ResolvedFilePlacement"/> per
/// input file). The first zone whose <see cref="LayoutZone.Filter"/>
/// matches a file wins; unmatched files are placed per
/// <see cref="LayoutTemplate.LeftoverStrategy"/>.
/// </summary>
public static class LayoutTemplateResolver {

  /// <summary>Pseudo-zone name used for files matching no template zone.</summary>
  public const string LeftoverZoneName = "<leftover>";

  /// <summary>
  /// Resolves <paramref name="template"/> against <paramref name="files"/>
  /// and an image of size <paramref name="imageSize"/>. The output preserves
  /// the input <paramref name="files"/> ordering (<c>FileIndex</c> matches
  /// the input position) — callers iterate the result, group by ZoneName /
  /// SortIndex, and emit moves accordingly.
  /// </summary>
  public static IReadOnlyList<ResolvedFilePlacement> Resolve(
      LayoutTemplate template,
      IReadOnlyList<IFilterFileContext> files,
      long imageSize) {
    ArgumentNullException.ThrowIfNull(template);
    ArgumentNullException.ThrowIfNull(files);
    if (imageSize < 0) throw new ArgumentOutOfRangeException(nameof(imageSize));

    // Pre-resolve zone bounds + filters so we touch them once per zone.
    var zoneCount = template.Zones.Count;
    var bounds = new (long Start, long End)[zoneCount];
    var filters = new IFileFilter?[zoneCount];
    for (var z = 0; z < zoneCount; z++) {
      bounds[z] = RangeSpec.Parse(template.Zones[z].Range).Resolve(imageSize);
      filters[z] = string.IsNullOrWhiteSpace(template.Zones[z].Filter)
        ? null
        : FilterExpression.Parse(template.Zones[z].Filter!);
    }

    // Group file indices per zone (or to the leftover bucket).
    var perZone = new List<int>[zoneCount];
    for (var z = 0; z < zoneCount; z++) perZone[z] = [];
    var leftover = new List<int>();

    for (var fi = 0; fi < files.Count; fi++) {
      var file = files[fi];
      var assigned = false;
      for (var z = 0; z < zoneCount; z++) {
        if (filters[z] is null || filters[z]!.Matches(file)) {
          perZone[z].Add(fi);
          assigned = true;
          break;
        }
      }
      if (!assigned) leftover.Add(fi);
    }

    // Sort each zone according to its keys.
    var result = new List<ResolvedFilePlacement>(files.Count);
    for (var z = 0; z < zoneCount; z++) {
      var indices = perZone[z];
      Sort(indices, template.Zones[z].SortBy, files);
      var (start, end) = bounds[z];
      for (var i = 0; i < indices.Count; i++)
        result.Add(new ResolvedFilePlacement(indices[i], template.Zones[z].Name, start, end, i));
    }

    if (leftover.Count > 0) {
      // No filter / no sort for leftovers — keep input order.
      var (lstart, lend) = ComputeLeftoverBounds(template.LeftoverStrategy, bounds, imageSize);
      for (var i = 0; i < leftover.Count; i++)
        result.Add(new ResolvedFilePlacement(leftover[i], LeftoverZoneName, lstart, lend, i));
    }

    return result;
  }

  /// <summary>
  /// Computes the byte bounds for the leftover bucket. <c>FillGaps</c>
  /// returns the entire image (callers may slot leftovers into actual gaps
  /// between zones using the planner's free-list logic). <c>AppendAtEnd</c>
  /// returns a range starting after the highest zone end.
  /// </summary>
  private static (long Start, long End) ComputeLeftoverBounds(
      LeftoverStrategy strat, (long Start, long End)[] zoneBounds, long imageSize) {
    if (strat == LeftoverStrategy.AppendAtEnd) {
      long highest = 0;
      foreach (var (_, e) in zoneBounds) if (e > highest) highest = e;
      return (highest, imageSize);
    }
    return (0, imageSize);
  }

  /// <summary>
  /// Sorts <paramref name="indices"/> in place using <paramref name="keys"/>
  /// applied to <paramref name="files"/>. When <paramref name="keys"/> is
  /// empty, the input order is preserved.
  /// </summary>
  private static void Sort(List<int> indices, IReadOnlyList<DefragSortKey> keys, IReadOnlyList<IFilterFileContext> files) {
    if (keys.Count == 0 || indices.Count <= 1) return;
    indices.Sort((a, b) => {
      var fa = files[a];
      var fb = files[b];
      for (var k = 0; k < keys.Count; k++) {
        var cmp = CompareByKey(fa, fb, keys[k]);
        if (cmp != 0) return cmp;
      }
      return a.CompareTo(b); // stable tiebreaker
    });
  }

  private static int CompareByKey(IFilterFileContext a, IFilterFileContext b, DefragSortKey key) {
    var cmp = key.Field switch {
      DefragSortField.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
      DefragSortField.Path => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
      DefragSortField.Extension => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
      DefragSortField.Size => a.Size.CompareTo(b.Size),
      DefragSortField.LastModified => CompareNullable(a.LastModified, b.LastModified),
      DefragSortField.LastAccessed => CompareNullable(a.LastAccessed, b.LastAccessed),
      DefragSortField.Created => CompareNullable(a.Created, b.Created),
      DefragSortField.Attributes => CompareAttributes(a.Attributes, b.Attributes),
      _ => 0,
    };
    return key.Direction == SortDirection.Descending ? -cmp : cmp;
  }

  private static int CompareNullable<T>(T? a, T? b) where T : struct, IComparable<T> {
    // Nulls sort last (i.e. greater than any value).
    if (a is null && b is null) return 0;
    if (a is null) return 1;
    if (b is null) return -1;
    return a.Value.CompareTo(b.Value);
  }

  /// <summary>
  /// Attribute comparison: bitmask &gt; 0 sorts first. So files with any
  /// attribute set get a lower comparison value than files with attribute 0.
  /// Among non-zero attributes, smaller bitmask sorts first.
  /// </summary>
  private static int CompareAttributes(uint a, uint b) {
    var aSet = a > 0;
    var bSet = b > 0;
    if (aSet && !bSet) return -1;
    if (!aSet && bSet) return 1;
    return a.CompareTo(b);
  }
}
