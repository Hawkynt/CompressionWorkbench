namespace Compression.NativeUI.Theming;

/// <summary>How a <see cref="IconShape"/> turns its polygons into coverage.</summary>
internal enum IconShapeKind {
  /// <summary>Even-odd fill across every polygon of the shape, matching WPF's default fill rule.</summary>
  Fill,

  /// <summary>Stroke along each polygon as an open polyline.</summary>
  Stroke,

  /// <summary>Stroke along each polygon, joining the last point back to the first.</summary>
  StrokeClosed,
}

/// <summary>
/// One painted layer of an icon, in the 16x16 coordinate space the WPF resource dictionary used.
/// Layers composite in order, so the model is the same painter's algorithm a <c>DrawingGroup</c> ran.
/// </summary>
/// <param name="Kind">Whether the polygons are filled or stroked.</param>
/// <param name="Polygons">Sub-paths of the shape, each an ordered point list.</param>
/// <param name="Color">Straight (non-premultiplied) ARGB.</param>
/// <param name="Thickness">Stroke width in icon units; ignored for <see cref="IconShapeKind.Fill"/>.</param>
internal readonly record struct IconShape(
  IconShapeKind Kind,
  (double X, double Y)[][] Polygons,
  uint Color,
  double Thickness = 0
);

/// <summary>Geometry helpers for describing icons without spelling out every vertex.</summary>
internal static class IconGeometry {
  /// <summary>Builds a polygon from a flat <c>x, y, x, y, ...</c> run.</summary>
  public static (double X, double Y)[] Poly(params double[] xy) {
    if ((xy.Length & 1) != 0) throw new ArgumentException("Coordinate list must be pairs.", nameof(xy));
    var points = new (double, double)[xy.Length / 2];
    for (var i = 0; i < points.Length; ++i)
      points[i] = (xy[2 * i], xy[2 * i + 1]);
    return points;
  }

  /// <summary>Approximates an ellipse as a polygon; 48 segments is smooth well past 64px.</summary>
  public static (double X, double Y)[] Ellipse(double cx, double cy, double rx, double ry, int segments = 48) {
    var points = new (double, double)[segments];
    for (var i = 0; i < segments; ++i) {
      var a = 2 * Math.PI * i / segments;
      points[i] = (cx + rx * Math.Cos(a), cy + ry * Math.Sin(a));
    }
    return points;
  }

  /// <summary>Approximates a circle as a polygon.</summary>
  public static (double X, double Y)[] Circle(double cx, double cy, double r, int segments = 48)
    => Ellipse(cx, cy, r, r, segments);

  /// <summary>Builds an axis-aligned rectangle — by far the most common shape in the icon set.</summary>
  public static (double X, double Y)[] Rect(double x0, double y0, double x1, double y1)
    => [(x0, y0), (x1, y0), (x1, y1), (x0, y1)];

  /// <summary>
  /// Flattens a quadratic Bézier. The icons came from XAML path data, where a couple of shapes —
  /// the preview lens — are <c>Q</c> segments that no polygon primitive reproduces.
  /// </summary>
  public static (double X, double Y)[] Quad((double X, double Y) from, (double X, double Y) control, (double X, double Y) to, int segments = 16) {
    var points = new (double, double)[segments + 1];
    for (var i = 0; i <= segments; ++i) {
      var t = (double)i / segments;
      var u = 1 - t;
      points[i] = (
        u * u * from.X + 2 * u * t * control.X + t * t * to.X,
        u * u * from.Y + 2 * u * t * control.Y + t * t * to.Y
      );
    }
    return points;
  }

  /// <summary>Concatenates point runs into one path, dropping a duplicated seam point.</summary>
  public static (double X, double Y)[] Join(params (double X, double Y)[][] runs) {
    var result = new List<(double X, double Y)>();
    foreach (var run in runs)
      foreach (var p in run) {
        if (result.Count > 0) {
          var last = result[^1];
          if (Math.Abs(last.X - p.X) < 1e-9 && Math.Abs(last.Y - p.Y) < 1e-9) continue;
        }
        result.Add(p);
      }
    return result.ToArray();
  }

  /// <summary>
  /// Approximates a circular arc, sweeping counter-clockwise from <paramref name="startDegrees"/>
  /// to <paramref name="endDegrees"/>. Used for the open shapes — the padlock shackle, mostly —
  /// that a closed ellipse cannot express.
  /// </summary>
  public static (double X, double Y)[] Arc(double cx, double cy, double r, double startDegrees, double endDegrees, int segments = 24) {
    var points = new (double, double)[segments + 1];
    var start = startDegrees * Math.PI / 180.0;
    var end = endDegrees * Math.PI / 180.0;
    for (var i = 0; i <= segments; ++i) {
      var a = start + (end - start) * i / segments;
      points[i] = (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }
    return points;
  }
}

/// <summary>
/// Rasterizes <see cref="IconShape"/> layers to a straight-ARGB buffer.
/// <para>
/// The frontend needs pixels: NativeForms' <see cref="Hawkynt.NativeForms.ImageList"/> takes ARGB
/// spans, while the icons were authored as WPF vector drawings. Rendering them here keeps one
/// definition per icon and lets every size come out sharp, instead of shipping pre-baked bitmaps
/// that blur the moment the shell runs at a scale factor other than 1.
/// </para>
/// </summary>
internal static class VectorIconRenderer {
  /// <summary>The coordinate space the icon definitions are authored in.</summary>
  public const double DesignSize = 16.0;

  private const int SuperSample = 4;

  /// <summary>Renders <paramref name="shapes"/> into a fresh <paramref name="size"/>-square ARGB buffer.</summary>
  public static int[] Render(IReadOnlyList<IconShape> shapes, int size) {
    ArgumentNullException.ThrowIfNull(shapes);
    ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

    var pixels = new int[size * size];
    var scale = size / DesignSize;
    var coverage = new byte[size * size];
    var hits = new int[size * size];

    foreach (var shape in shapes) {
      Array.Clear(hits);
      var polygons = shape.Kind == IconShapeKind.Fill
        ? shape.Polygons
        : BuildStrokeOutline(shape, scale);

      if (shape.Kind == IconShapeKind.Fill)
        AccumulateEvenOdd(hits, size, polygons, scale);
      else
        AccumulateUnion(hits, size, polygons, scale);

      const int Samples = SuperSample * SuperSample;
      for (var i = 0; i < coverage.Length; ++i)
        coverage[i] = (byte)(hits[i] * 255 / Samples);

      Composite(pixels, coverage, shape.Color);
    }

    return pixels;
  }

  /// <summary>
  /// Expands a stroked path into filled pieces: a quad per segment plus a disc at every vertex, so
  /// joins and caps stay round. The pieces are unioned rather than even-odd filled, otherwise
  /// overlaps at the joins would punch holes in the stroke.
  /// </summary>
  private static (double X, double Y)[][] BuildStrokeOutline(in IconShape shape, double scale) {
    var half = Math.Max(shape.Thickness, 0.1) / 2.0;
    var pieces = new List<(double X, double Y)[]>();

    foreach (var path in shape.Polygons) {
      if (path.Length == 0) continue;

      var closed = shape.Kind == IconShapeKind.StrokeClosed;
      var last = closed ? path.Length : path.Length - 1;
      for (var i = 0; i < last; ++i) {
        var a = path[i];
        var b = path[(i + 1) % path.Length];
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) continue;

        var nx = -dy / len * half;
        var ny = dx / len * half;
        pieces.Add([
          (a.X + nx, a.Y + ny),
          (b.X + nx, b.Y + ny),
          (b.X - nx, b.Y - ny),
          (a.X - nx, a.Y - ny),
        ]);
      }

      // Round joins/caps. Skipped when the stroke is thinner than roughly a device pixel, where the
      // discs would only add shimmer.
      if (half * scale >= 0.35)
        foreach (var p in path)
          pieces.Add(IconGeometry.Circle(p.X, p.Y, half, 12));
    }

    return pieces.ToArray();
  }

  private static void AccumulateEvenOdd(int[] hits, int size, (double X, double Y)[][] polygons, double scale) {
    var crossings = new List<double>(16);

    for (var py = 0; py < size; ++py)
      for (var sy = 0; sy < SuperSample; ++sy) {
        var y = (py + (sy + 0.5) / SuperSample) / scale;
        crossings.Clear();
        foreach (var poly in polygons)
          CollectCrossings(poly, y, crossings);
        if (crossings.Count < 2) continue;

        crossings.Sort();
        for (var i = 0; i + 1 < crossings.Count; i += 2)
          MarkSpan(hits, size, py, crossings[i] * scale, crossings[i + 1] * scale);
      }
  }

  private static void AccumulateUnion(int[] hits, int size, (double X, double Y)[][] polygons, double scale) {
    var crossings = new List<double>(8);
    var row = new bool[size * SuperSample];

    for (var py = 0; py < size; ++py)
      for (var sy = 0; sy < SuperSample; ++sy) {
        var y = (py + (sy + 0.5) / SuperSample) / scale;
        Array.Clear(row);
        var any = false;

        foreach (var poly in polygons) {
          crossings.Clear();
          CollectCrossings(poly, y, crossings);
          if (crossings.Count < 2) continue;
          crossings.Sort();
          for (var i = 0; i + 1 < crossings.Count; i += 2)
            any |= MarkSubRow(row, size, crossings[i] * scale, crossings[i + 1] * scale);
        }

        if (!any) continue;
        for (var sx = 0; sx < row.Length; ++sx)
          if (row[sx])
            ++hits[py * size + sx / SuperSample];
      }
  }

  private static void CollectCrossings((double X, double Y)[] poly, double y, List<double> crossings) {
    for (var i = 0; i < poly.Length; ++i) {
      var a = poly[i];
      var b = poly[(i + 1) % poly.Length];
      if (Math.Abs(a.Y - b.Y) < 1e-12) continue;
      var lo = Math.Min(a.Y, b.Y);
      var hi = Math.Max(a.Y, b.Y);
      if (y < lo || y >= hi) continue;
      crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
    }
  }

  private static void MarkSpan(int[] hits, int size, int py, double x0, double x1) {
    var from = (int)Math.Ceiling(x0 * SuperSample - 0.5);
    var to = (int)Math.Ceiling(x1 * SuperSample - 0.5);
    if (from < 0) from = 0;
    if (to > size * SuperSample) to = size * SuperSample;
    for (var sx = from; sx < to; ++sx)
      ++hits[py * size + sx / SuperSample];
  }

  private static bool MarkSubRow(bool[] row, int size, double x0, double x1) {
    var from = (int)Math.Ceiling(x0 * SuperSample - 0.5);
    var to = (int)Math.Ceiling(x1 * SuperSample - 0.5);
    if (from < 0) from = 0;
    if (to > size * SuperSample) to = size * SuperSample;
    var any = false;
    for (var sx = from; sx < to; ++sx) {
      row[sx] = true;
      any = true;
    }
    return any;
  }

  /// <summary>Source-over composite of a flat colour through a coverage mask.</summary>
  private static void Composite(int[] pixels, byte[] coverage, uint color) {
    var sa = (color >> 24) & 0xFF;
    var sr = (color >> 16) & 0xFF;
    var sg = (color >> 8) & 0xFF;
    var sb = color & 0xFF;

    for (var i = 0; i < pixels.Length; ++i) {
      var cov = coverage[i];
      if (cov == 0) continue;

      var alpha = sa * cov / 255;
      if (alpha == 0) continue;

      var dst = unchecked((uint)pixels[i]);
      var da = (dst >> 24) & 0xFF;
      var dr = (dst >> 16) & 0xFF;
      var dg = (dst >> 8) & 0xFF;
      var db = dst & 0xFF;

      var outA = alpha + da * (255 - alpha) / 255;
      if (outA == 0) {
        pixels[i] = 0;
        continue;
      }

      var r = (sr * alpha + dr * da * (255 - alpha) / 255) / outA;
      var g = (sg * alpha + dg * da * (255 - alpha) / 255) / outA;
      var b = (sb * alpha + db * da * (255 - alpha) / 255) / outA;

      pixels[i] = unchecked((int)(((uint)outA << 24) | ((uint)Clamp(r) << 16) | ((uint)Clamp(g) << 8) | Clamp(b)));
    }
  }

  private static uint Clamp(long v) => (uint)(v < 0 ? 0 : v > 255 ? 255 : v);
}
