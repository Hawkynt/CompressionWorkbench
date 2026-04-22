#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.FontCollection;

/// <summary>
/// Emits a decoded TrueType glyph as an SVG document. Off-curve control points
/// are interpreted as TrueType quadratic Beziers — when two consecutive off-curve
/// points occur, an implied on-curve point at their midpoint is inserted
/// (TT spec §"Glyf table").
/// </summary>
internal static class GlyphSvgEmitter {
  public static string Emit(TrueTypeGlyphDecoder.DecodedGlyph g, int unitsPerEm, string? label) {
    // SVG y axis points down; TrueType y axis points up. Flip by translating.
    var width = Math.Max(1, g.XMax - g.XMin);
    var height = Math.Max(1, g.YMax - g.YMin);
    var sb = new StringBuilder();
    sb.Append(
      $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{g.XMin} {-g.YMax} {width} {height}' ");
    sb.Append($"data-units-per-em='{unitsPerEm}'");
    if (label != null) sb.Append($" data-label='{System.Security.SecurityElement.Escape(label)}'");
    sb.Append(">\n");

    if (g.IsComposite) {
      var ids = string.Join(",", g.ComponentGlyphIndices);
      sb.Append($"  <title>composite glyph: references {ids}</title>\n");
      sb.Append("  <text x='0' y='0' fill='#888'>Composite glyph — outlines not expanded.</text>\n");
      sb.Append("</svg>\n");
      return sb.ToString();
    }

    sb.Append("  <path fill='#000' d='");
    foreach (var contour in g.Contours)
      EmitContour(sb, contour);
    sb.Append("'/>\n");
    sb.Append("</svg>\n");
    return sb.ToString();
  }

  private static void EmitContour(StringBuilder sb, IReadOnlyList<(int X, int Y, bool OnCurve)> contour) {
    if (contour.Count == 0) return;

    // Ensure the path starts on an on-curve point; if it doesn't, rotate or synthesise one.
    var n = contour.Count;
    var startIdx = 0;
    while (startIdx < n && !contour[startIdx].OnCurve) ++startIdx;
    int x0, y0;
    if (startIdx == n) {
      // All off-curve → synthesise start at midpoint between first and last points.
      var a = contour[0]; var b = contour[^1];
      x0 = (a.X + b.X) / 2;
      y0 = (a.Y + b.Y) / 2;
      startIdx = 0;
    } else {
      var p = contour[startIdx];
      x0 = p.X; y0 = p.Y;
    }

    sb.Append($"M{Fmt(x0)} {Fmt(-y0)} ");
    for (var i = 1; i <= n; ++i) {
      var idx = (startIdx + i) % n;
      var prevIdx = (startIdx + i - 1) % n;
      var p = contour[idx];
      var prev = contour[prevIdx];
      if (p.OnCurve) {
        if (prev.OnCurve)
          sb.Append($"L{Fmt(p.X)} {Fmt(-p.Y)} ");
        else
          sb.Append($"Q{Fmt(prev.X)} {Fmt(-prev.Y)} {Fmt(p.X)} {Fmt(-p.Y)} ");
      } else if (!prev.OnCurve) {
        // Two off-curves in a row → implied on-curve midpoint.
        var mx = (prev.X + p.X) / 2;
        var my = (prev.Y + p.Y) / 2;
        sb.Append($"Q{Fmt(prev.X)} {Fmt(-prev.Y)} {Fmt(mx)} {Fmt(-my)} ");
      }
    }
    sb.Append("Z ");
  }

  private static string Fmt(int v) => v.ToString(CultureInfo.InvariantCulture);
}
