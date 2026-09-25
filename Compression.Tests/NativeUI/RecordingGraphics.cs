using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Hawkynt.NativeForms.Drawing;

namespace Compression.Tests.NativeUI;

/// <summary>
/// An <see cref="IGraphics"/> that actually rasterises into an ARGB buffer and, at the same time,
/// keeps a tally of what it was asked to do. The buffer answers "did anything render"; the tally
/// answers "did it render the right kind of thing" — a control that paints nothing but its
/// background looks plausible as pixels but shows up immediately as a one-entry tally.
/// </summary>
internal sealed class RecordingGraphics(int width, int height) : IGraphics {
  private readonly int[] _pixels = NewCanvas(width, height);
  private readonly Stack<Rectangle> _clips = new();
  private readonly List<string> _log = [];
  private readonly Dictionary<string, int> _counts = [];

  public int Width => width;
  public int Height => height;
  public int[] Pixels => this._pixels;
  public IReadOnlyList<string> Log => this._log;
  public IReadOnlyDictionary<string, int> Counts => this._counts;
  public int MaxClipDepth { get; private set; }
  public int UnbalancedPops { get; private set; }

  /// <summary>Number of pixels differing from the initial fill — the "did it draw" signal.</summary>
  public int PaintedPixels {
    get {
      var background = Blank(width, height);
      var count = 0;
      for (var i = 0; i < this._pixels.Length; ++i)
        if (this._pixels[i] != background[i])
          ++count;
      return count;
    }
  }

  /// <summary>Distinct colours in the result: a single value means a flat fill and nothing else.</summary>
  public int DistinctColors => this._pixels.Distinct().Count();

  private static int[] NewCanvas(int w, int h) => Blank(w, h);

  // A magenta ground no control would choose, so anything left of it is genuinely unpainted.
  private static int[] Blank(int w, int h) {
    var pixels = new int[w * h];
    Array.Fill(pixels, unchecked((int)0xFFFF00FF));
    return pixels;
  }

  private void Record(string op, object? detail = null) {
    this._counts[op] = this._counts.GetValueOrDefault(op) + 1;
    if (this._log.Count < 4000) this._log.Add(detail is null ? op : $"{op} {detail}");
  }

  private Rectangle Clip => this._clips.Count > 0 ? this._clips.Peek() : new(0, 0, width, height);

  private void Plot(int x, int y, Color color) {
    var clip = this.Clip;
    if (x < clip.Left || x >= clip.Right || y < clip.Top || y >= clip.Bottom) return;
    if (x < 0 || x >= width || y < 0 || y >= height) return;

    var index = y * width + x;
    if (color.A == 255) {
      this._pixels[index] = color.ToArgb();
      return;
    }

    var under = this._pixels[index];
    var a = color.A / 255.0;
    this._pixels[index] = unchecked((int)(
      0xFF000000u |
      (uint)(int)(color.R * a + ((under >> 16) & 0xFF) * (1 - a)) << 16 |
      (uint)(int)(color.G * a + ((under >> 8) & 0xFF) * (1 - a)) << 8 |
      (uint)(int)(color.B * a + (under & 0xFF) * (1 - a))));
  }

  public void FillRectangle(Color color, Rectangle bounds) {
    this.Record(nameof(this.FillRectangle), bounds);
    for (var y = bounds.Top; y < bounds.Bottom; ++y)
      for (var x = bounds.Left; x < bounds.Right; ++x)
        this.Plot(x, y, color);
  }

  public void DrawRectangle(Color color, Rectangle bounds, int thickness) {
    this.Record(nameof(this.DrawRectangle), bounds);
    for (var t = 0; t < Math.Max(1, thickness); ++t) {
      for (var x = bounds.Left; x < bounds.Right; ++x) {
        this.Plot(x, bounds.Top + t, color);
        this.Plot(x, bounds.Bottom - 1 - t, color);
      }

      for (var y = bounds.Top; y < bounds.Bottom; ++y) {
        this.Plot(bounds.Left + t, y, color);
        this.Plot(bounds.Right - 1 - t, y, color);
      }
    }
  }

  public void DrawLine(Color color, int x1, int y1, int x2, int y2, int thickness) {
    this.Record(nameof(this.DrawLine), $"{x1},{y1}-{x2},{y2}");

    var steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    if (steps == 0) {
      this.Plot(x1, y1, color);
      return;
    }

    var half = Math.Max(1, thickness) / 2;
    for (var i = 0; i <= steps; ++i) {
      var x = x1 + (x2 - x1) * i / steps;
      var y = y1 + (y2 - y1) * i / steps;
      for (var oy = -half; oy <= half; ++oy)
        for (var ox = -half; ox <= half; ++ox)
          this.Plot(x + ox, y + oy, color);
    }
  }

  public void DrawEllipse(Color color, Rectangle bounds, int thickness) {
    this.Record(nameof(this.DrawEllipse), bounds);
    this.Ellipse(color, bounds, outlineOnly: true);
  }

  public void FillEllipse(Color color, Rectangle bounds) {
    this.Record(nameof(this.FillEllipse), bounds);
    this.Ellipse(color, bounds, outlineOnly: false);
  }

  private void Ellipse(Color color, Rectangle bounds, bool outlineOnly) {
    double rx = bounds.Width / 2.0, ry = bounds.Height / 2.0;
    if (rx <= 0 || ry <= 0) return;

    double cx = bounds.Left + rx, cy = bounds.Top + ry;
    for (var y = bounds.Top; y < bounds.Bottom; ++y)
      for (var x = bounds.Left; x < bounds.Right; ++x) {
        var nx = (x + 0.5 - cx) / rx;
        var ny = (y + 0.5 - cy) / ry;
        var d = nx * nx + ny * ny;
        if (d > 1) continue;
        if (outlineOnly && d < 0.7) continue;
        this.Plot(x, y, color);
      }
  }

  public void FillRoundedRectangle(Color color, Rectangle bounds, int radius) {
    this.Record(nameof(this.FillRoundedRectangle), bounds);
    this.RoundedRectangle(color, bounds, radius, outlineOnly: false);
  }

  public void DrawRoundedRectangle(Color color, Rectangle bounds, int radius, int thickness) {
    this.Record(nameof(this.DrawRoundedRectangle), bounds);
    this.RoundedRectangle(color, bounds, radius, outlineOnly: true);
  }

  private void RoundedRectangle(Color color, Rectangle bounds, int radius, bool outlineOnly) {
    var r = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
    for (var y = bounds.Top; y < bounds.Bottom; ++y)
      for (var x = bounds.Left; x < bounds.Right; ++x) {
        // Distance into the nearest corner box; outside its quarter-circle the pixel is cut away.
        var dx = Math.Max(bounds.Left + r - x, x - (bounds.Right - 1 - r));
        var dy = Math.Max(bounds.Top + r - y, y - (bounds.Bottom - 1 - r));
        if (dx > 0 && dy > 0 && dx * dx + dy * dy > r * r) continue;

        if (outlineOnly) {
          var interior =
            x > bounds.Left && x < bounds.Right - 1 && y > bounds.Top && y < bounds.Bottom - 1 &&
            !(dx > 0 && dy > 0 && (dx - 1) * (dx - 1) + (dy - 1) * (dy - 1) > r * r);
          if (interior) continue;
        }

        this.Plot(x, y, color);
      }
  }

  /// <summary>
  /// Text is rasterised as a solid bar of the text colour over the measured extent. It is not
  /// legible, and is not meant to be: what is being checked is that the control asked for text at a
  /// sane place, which a bar in the right box shows and a wrong layout shows just as clearly.
  /// </summary>
  public void DrawText(string text, Font font, Color color, Rectangle bounds, ContentAlignment alignment) {
    this.Record(nameof(this.DrawText), $"\"{text}\" @{bounds} {alignment}");
    if (string.IsNullOrEmpty(text)) return;

    var size = Measure(text, font);
    var w = Math.Min(size.Width, bounds.Width);
    var h = Math.Min(size.Height, bounds.Height);

    var x = alignment switch {
      ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter
        => bounds.Left + (bounds.Width - w) / 2,
      ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight
        => bounds.Right - w,
      _ => bounds.Left,
    };

    var y = alignment switch {
      ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight
        => bounds.Top + (bounds.Height - h) / 2,
      ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight
        => bounds.Bottom - h,
      _ => bounds.Top,
    };

    // Two scanlines out of every three, so a text run stays visually distinct from a solid fill.
    for (var ty = 0; ty < h; ++ty) {
      if (ty % 3 == 2) continue;
      for (var tx = 0; tx < w; ++tx) this.Plot(x + tx, y + ty, color);
    }
  }

  public Size MeasureText(string text, Font font) {
    this.Record(nameof(this.MeasureText));
    return Measure(text, font);
  }

  private static readonly HeadlessBackend Metrics = new();

  /// <summary>Measures the way the backend does, so layout here matches layout there.</summary>
  private static Size Measure(string text, Font font) => Metrics.MeasureText(text, font);

  public void DrawImage(IImage image, Rectangle bounds) {
    this.Record(nameof(this.DrawImage), $"{image.Width}x{image.Height} -> {bounds}");
    if (image is not PixelImage source || bounds.Width <= 0 || bounds.Height <= 0) return;

    for (var y = 0; y < bounds.Height; ++y) {
      var sy = y * source.Height / bounds.Height;
      for (var x = 0; x < bounds.Width; ++x) {
        var sx = x * source.Width / bounds.Width;
        this.Plot(bounds.Left + x, bounds.Top + y, Color.FromArgb(source.Argb[sy * source.Width + sx]));
      }
    }
  }

  public void PushClip(Rectangle bounds) {
    this.Record(nameof(this.PushClip), bounds);
    this._clips.Push(this._clips.Count > 0 ? Rectangle.Intersect(this._clips.Peek(), bounds) : bounds);
    this.MaxClipDepth = Math.Max(this.MaxClipDepth, this._clips.Count);
  }

  public void PopClip() {
    this.Record(nameof(this.PopClip));
    if (this._clips.Count == 0) {
      ++this.UnbalancedPops;
      return;
    }

    this._clips.Pop();
  }

  /// <summary>Clips still on the stack when painting ended: every one of them is a leak.</summary>
  public int LeakedClips => this._clips.Count;
}
