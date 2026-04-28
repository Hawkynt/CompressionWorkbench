using System.Globalization;
using System.Text;

namespace Compression.Analysis;

/// <summary>
/// Render options for SVG / HTML output.
/// </summary>
/// <param name="MaxColumns">
/// Hard cap on rendered columns. Block maps wider than this are downsampled —
/// each output cell aggregates the innermost owner of a slice of source blocks.
/// Default 4096 (the spec's "don't render multi-million blocks" rule).
/// </param>
/// <param name="CellWidth">SVG cell width in pixels.</param>
/// <param name="CellHeight">SVG cell height in pixels.</param>
/// <param name="ShowAllLayers">If true, render one row per nesting depth.</param>
public sealed record BlockMapRenderOptions(
  int MaxColumns = 4096,
  int CellWidth = 4,
  int CellHeight = 16,
  bool ShowAllLayers = true);

/// <summary>
/// Multi-format renderer for <see cref="BlockMap"/>: ASCII (CLI), SVG
/// (per-block colored cells), HTML (SVG + legend + summary).
/// </summary>
public static class BlockMapRenderer {

  // ── Color palette ──────────────────────────────────────────────────

  /// <summary>
  /// Curated colors for well-known formats. Falls back to deterministic
  /// HSL-from-hash for everything else, so renderings are stable across
  /// runs even when a formatId isn't in this table.
  /// </summary>
  private static readonly Dictionary<string, string> CuratedColors = new(StringComparer.OrdinalIgnoreCase) {
    // Filesystems.
    ["Ext"] = "#4caf50",  ["Ext2"] = "#4caf50",  ["Ext3"] = "#4caf50",  ["Ext4"] = "#4caf50",
    ["Fat"] = "#ff9800",  ["Fat12"] = "#ff9800", ["Fat16"] = "#ff9800", ["Fat32"] = "#ff9800",
    ["ExFat"] = "#ffb74d",
    ["Ntfs"] = "#1976d2",
    ["Btrfs"] = "#009688",
    ["Xfs"] = "#d32f2f",
    ["Hfs"] = "#9c27b0",   ["HfsPlus"] = "#9c27b0",
    ["Apfs"] = "#7b1fa2",
    ["Zfs"] = "#5d4037",
    ["SquashFs"] = "#3f51b5",
    ["Iso9660"] = "#607d8b",
    ["Udf"] = "#455a64",
    // Partition tables.
    ["Mbr"] = "#9e9e9e", ["MBR"] = "#9e9e9e",
    ["Gpt"] = "#bdbdbd", ["GPT"] = "#bdbdbd",
    // Disk images.
    ["Qcow2"] = "#673ab7",
    ["Vmdk"] = "#5e35b2",
    ["Vhd"] = "#7e57c2",
    ["Vdi"] = "#9575cd",
  };

  /// <summary>
  /// Returns a deterministic CSS color for <paramref name="formatId"/>. Curated
  /// formats (FAT/EXT/NTFS/etc.) get hand-picked colors; unknown ones get an
  /// HSL color whose hue is the hash of the id, so the same format always
  /// renders the same color.
  /// </summary>
  public static string ColorFor(string formatId) {
    if (CuratedColors.TryGetValue(formatId, out var c)) return c;
    var (h, s, l) = HslFor(formatId);
    return HslToHex(h, s, l);
  }

  private static (int H, int S, int L) HslFor(string formatId) {
    // Use a stable hash so colors don't shift between runs / .NET versions.
    var hash = 0;
    foreach (var ch in formatId) hash = unchecked(hash * 31 + ch);
    return (Math.Abs(hash % 360), 70, 55);
  }

  private static string HslToHex(int h, int s, int l) {
    // Standard HSL → RGB.
    var sn = s / 100.0;
    var ln = l / 100.0;
    var c = (1 - Math.Abs(2 * ln - 1)) * sn;
    var hp = h / 60.0;
    var x = c * (1 - Math.Abs(hp % 2 - 1));
    double r1 = 0, g1 = 0, b1 = 0;
    if (hp < 1) { r1 = c; g1 = x; }
    else if (hp < 2) { r1 = x; g1 = c; }
    else if (hp < 3) { g1 = c; b1 = x; }
    else if (hp < 4) { g1 = x; b1 = c; }
    else if (hp < 5) { r1 = x; b1 = c; }
    else { r1 = c; b1 = x; }
    var m = ln - c / 2;
    var r = (int)Math.Round((r1 + m) * 255);
    var g = (int)Math.Round((g1 + m) * 255);
    var b = (int)Math.Round((b1 + m) * 255);
    return string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}");
  }

  // ── ASCII (innermost) ─────────────────────────────────────────────

  /// <summary>
  /// Compact ASCII strip — one char per visible block, character is the first
  /// letter of the innermost format id (or <c>·</c> for unmapped). Followed by
  /// a legend listing every distinct format encountered.
  /// </summary>
  public static string RenderAscii(BlockMap map, int columns = 80) {
    ArgumentNullException.ThrowIfNull(map);
    if (map.BlockCount == 0) return "(empty map)";
    if (columns <= 0) columns = map.BlockCount;

    var width = Math.Min(map.BlockCount, columns);
    var letters = new char[width];
    var seen = new SortedDictionary<char, string>();

    for (var i = 0; i < width; ++i) {
      var srcStart = (long)i * map.BlockCount / width;
      var srcEnd = (long)(i + 1) * map.BlockCount / width;
      string? innermost = null;
      for (var s = (int)srcStart; s < srcEnd; ++s) {
        var stk = map.GetOwnerStack(s);
        if (stk.Count > 0) { innermost = stk[^1]; break; }
      }
      if (innermost is null) {
        letters[i] = '·'; // middle-dot for unmapped
      } else {
        var ch = char.ToUpperInvariant(innermost[0]);
        letters[i] = ch;
        if (!seen.ContainsKey(ch)) seen[ch] = innermost;
      }
    }

    var sb = new StringBuilder();
    sb.Append(letters);
    sb.AppendLine();
    sb.Append("Legend: ");
    var first = true;
    foreach (var kv in seen) {
      if (!first) sb.Append("  ");
      first = false;
      sb.Append(kv.Key).Append(" = ").Append(kv.Value);
    }
    sb.Append("  · = unmapped");
    return sb.ToString();
  }

  /// <summary>
  /// Multi-row ASCII, one row per nesting depth (depth 0 = outermost on top).
  /// </summary>
  public static string RenderAsciiLayered(BlockMap map) {
    ArgumentNullException.ThrowIfNull(map);
    if (map.BlockCount == 0) return "(empty map)";
    var depth = map.MaxDepth;
    if (depth == 0) return "(no owners)";

    var sb = new StringBuilder();
    var seen = new SortedDictionary<char, string>();
    for (var d = 0; d < depth; ++d) {
      sb.Append("Depth ").Append(d).Append(": ");
      for (var i = 0; i < map.BlockCount; ++i) {
        var stk = map.GetOwnerStack(i);
        if (stk.Count > d) {
          var ch = char.ToUpperInvariant(stk[d][0]);
          sb.Append(ch);
          if (!seen.ContainsKey(ch)) seen[ch] = stk[d];
        } else {
          sb.Append('·');
        }
      }
      sb.AppendLine();
    }
    sb.Append("Legend: ");
    var first = true;
    foreach (var kv in seen) {
      if (!first) sb.Append("  ");
      first = false;
      sb.Append(kv.Key).Append(" = ").Append(kv.Value);
    }
    return sb.ToString();
  }

  // ── SVG ───────────────────────────────────────────────────────────

  /// <summary>
  /// SVG renderer — one rect per visible cell, one row per layer (when
  /// <see cref="BlockMapRenderOptions.ShowAllLayers"/>). Each cell carries
  /// a <c>&lt;title&gt;</c> tooltip with offset/length/format.
  /// <para>
  /// Source blocks are downsampled to <see cref="BlockMapRenderOptions.MaxColumns"/>
  /// when needed, picking the innermost owner of the source slice for each cell.
  /// </para>
  /// </summary>
  public static string RenderSvg(BlockMap map, BlockMapRenderOptions? opts = null) {
    ArgumentNullException.ThrowIfNull(map);
    opts ??= new BlockMapRenderOptions();

    var cols = Math.Min(map.BlockCount, Math.Max(1, opts.MaxColumns));
    var depth = opts.ShowAllLayers ? Math.Max(1, map.MaxDepth) : 1;
    var w = cols * opts.CellWidth;
    var h = depth * opts.CellHeight;

    var sb = new StringBuilder();
    sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(w)
      .Append("\" height=\"").Append(h)
      .Append("\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h)
      .Append("\" shape-rendering=\"crispEdges\">");
    sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#1e1e1e\"/>");

    for (var d = 0; d < depth; ++d) {
      for (var c = 0; c < cols; ++c) {
        var srcStart = (long)c * map.BlockCount / cols;
        var srcEnd = (long)(c + 1) * map.BlockCount / cols;
        if (srcEnd <= srcStart) srcEnd = srcStart + 1;

        // Pick the most-common owner at depth d across the slice.
        string? winner = PickOwnerAtDepth(map, (int)srcStart, (int)Math.Min(srcEnd, map.BlockCount), d);
        if (winner is null) continue;

        var x = c * opts.CellWidth;
        var y = d * opts.CellHeight;
        var color = ColorFor(winner);
        var byteStart = srcStart * map.BlockSize;
        var byteEnd = Math.Min(map.TotalBytes, srcEnd * map.BlockSize);
        sb.Append("<rect x=\"").Append(x)
          .Append("\" y=\"").Append(y)
          .Append("\" width=\"").Append(opts.CellWidth)
          .Append("\" height=\"").Append(opts.CellHeight)
          .Append("\" fill=\"").Append(color).Append("\">")
          .Append("<title>")
          .Append(EscapeXml(winner)).Append(" @ depth ").Append(d).Append(": 0x")
          .Append(byteStart.ToString("X", CultureInfo.InvariantCulture)).Append(" .. 0x")
          .Append(byteEnd.ToString("X", CultureInfo.InvariantCulture))
          .Append(" (").Append(byteEnd - byteStart).Append(" B)")
          .Append("</title></rect>");
      }
    }
    sb.Append("</svg>");
    return sb.ToString();
  }

  private static string? PickOwnerAtDepth(BlockMap map, int srcStart, int srcEnd, int depth) {
    Dictionary<string, int>? counts = null;
    string? sole = null;
    for (var i = srcStart; i < srcEnd; ++i) {
      var stk = map.GetOwnerStack(i);
      if (stk.Count <= depth) continue;
      var fmt = stk[depth];
      if (sole is null && counts is null) { sole = fmt; continue; }
      if (counts is null) {
        counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (sole is not null) counts[sole] = 1;
        sole = null;
      }
      counts.TryGetValue(fmt, out var n);
      counts[fmt] = n + 1;
    }
    if (counts is null) return sole;
    string? best = null;
    var bestN = -1;
    foreach (var kv in counts) {
      if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
    }
    return best;
  }

  // ── HTML ──────────────────────────────────────────────────────────

  /// <summary>
  /// Self-contained HTML page wrapping the SVG with a legend table and
  /// hit summary. <paramref name="hits"/> may be null (legend will be
  /// derived from the map's histogram alone).
  /// </summary>
  public static string RenderHtml(BlockMap map, IReadOnlyList<NestedHit>? hits) {
    ArgumentNullException.ThrowIfNull(map);

    var svg = RenderSvg(map, new BlockMapRenderOptions());
    var counts = map.CountByFormat();

    var sb = new StringBuilder();
    sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
    sb.AppendLine("<title>Block Map</title>");
    sb.AppendLine("<style>");
    sb.AppendLine("body { font-family: ui-monospace, Menlo, Consolas, monospace; background: #1e1e1e; color: #ddd; margin: 16px; }");
    sb.AppendLine("h1, h2 { color: #fff; font-weight: 500; }");
    sb.AppendLine("table { border-collapse: collapse; margin-top: 8px; }");
    sb.AppendLine("td, th { padding: 4px 10px; border: 1px solid #333; text-align: left; }");
    sb.AppendLine(".sw { display: inline-block; width: 14px; height: 14px; vertical-align: middle; margin-right: 6px; border: 1px solid #555; }");
    sb.AppendLine(".map { background: #111; padding: 8px; overflow-x: auto; }");
    sb.AppendLine("</style></head><body>");

    sb.AppendLine("<h1>Block Map</h1>");
    sb.Append("<p>Total: ").Append(map.TotalBytes).Append(" B / ")
      .Append(map.BlockCount).Append(" blocks of ").Append(map.BlockSize).Append(" B / max depth ")
      .Append(map.MaxDepth).AppendLine("</p>");
    sb.AppendLine("<div class=\"map\">").AppendLine(svg).AppendLine("</div>");

    sb.AppendLine("<h2>Legend</h2>");
    sb.AppendLine("<table><tr><th>Color</th><th>Format</th><th>Blocks</th></tr>");
    foreach (var kv in counts.OrderByDescending(k => k.Value)) {
      var color = ColorFor(kv.Key);
      sb.Append("<tr><td><span class=\"sw\" style=\"background:").Append(color).Append("\"></span>").Append(color)
        .Append("</td><td>").Append(EscapeHtml(kv.Key))
        .Append("</td><td>").Append(kv.Value).AppendLine("</td></tr>");
    }
    sb.AppendLine("</table>");

    if (hits is { Count: > 0 }) {
      sb.AppendLine("<h2>Hits</h2>");
      sb.AppendLine("<table><tr><th>Depth</th><th>Format</th><th>Offset</th><th>Length</th><th>Confidence</th><th>Envelope</th></tr>");
      AppendHitRows(sb, hits);
      sb.AppendLine("</table>");
    }
    sb.AppendLine("</body></html>");
    return sb.ToString();
  }

  private static void AppendHitRows(StringBuilder sb, IReadOnlyList<NestedHit> hits) {
    foreach (var h in hits) {
      sb.Append("<tr><td>").Append(h.Depth).Append("</td>")
        .Append("<td><span class=\"sw\" style=\"background:").Append(ColorFor(h.FormatId)).Append("\"></span>").Append(EscapeHtml(h.FormatId)).Append("</td>")
        .Append("<td>0x").Append(h.ByteOffset.ToString("X", CultureInfo.InvariantCulture)).Append("</td>")
        .Append("<td>").Append(h.Length).Append("</td>")
        .Append("<td>").Append(h.Confidence.ToString("F2", CultureInfo.InvariantCulture)).Append("</td>")
        .Append("<td>").Append(EscapeHtml(string.Join(" ▸ ", h.EnvelopeStack))).Append("</td></tr>")
        .AppendLine();
      if (h.Children.Count > 0) AppendHitRows(sb, h.Children);
    }
  }

  private static string EscapeXml(string s) => s
    .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

  private static string EscapeHtml(string s) => EscapeXml(s);
}
