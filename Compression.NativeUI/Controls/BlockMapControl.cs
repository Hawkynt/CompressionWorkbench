using System.Diagnostics;
using System.Drawing;
using Compression.Core.Layout;
using Compression.NativeUI.Theming;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// Reports the byte range covered by a clicked tile plus every block that intersects it. Hosts use
/// this to render a drill-down view.
/// </summary>
internal sealed class TileClickedEventArgs : EventArgs {
  /// <summary>First byte offset (inclusive) covered by the clicked tile.</summary>
  public long StartOffset { get; init; }

  /// <summary>One past the last byte offset covered by the clicked tile.</summary>
  public long EndOffset { get; init; }

  /// <summary>Span in bytes covered by the tile.</summary>
  public long ByteSpan => Math.Max(0, this.EndOffset - this.StartOffset);

  /// <summary>Every block whose own range intersects the tile's range.</summary>
  public IReadOnlyList<DefragBlockInfo> Contents { get; init; } = [];
}

/// <summary>
/// Draws a defragmentation block map as a tile grid, coloured by block kind and classification,
/// or projected onto a circular platter or a stack of platters.
/// <para>
/// Maps run to hundreds of thousands of blocks, so the image is binned onto a fixed tile grid once
/// and the binning is cached against (map, image size, grid dimensions) — head movement and
/// same-size redraws reuse it. Pixels are composed into one buffer and blitted in a single call
/// rather than issued as tens of thousands of fill commands.
/// </para>
/// </summary>
internal sealed class BlockMapControl : OwnerDrawnControl {
  // Kind colours, used when no classification is set.
  private static readonly Color Free = Color.FromArgb(0xE0, 0xE0, 0xE0);
  private static readonly Color Bad = Color.FromArgb(0xC0, 0x30, 0x30);
  private static readonly Color Meta = Color.FromArgb(0x70, 0x70, 0x70);
  private static readonly Color InProgress = Color.FromArgb(0xFF, 0xC8, 0x00);

  // Classification colours, hot through frozen.
  private static readonly Color Hot = Color.FromArgb(0xE6, 0x4A, 0x19);
  private static readonly Color Normal = Color.FromArgb(0x42, 0xA5, 0xF5);
  private static readonly Color Cold = Color.FromArgb(0x66, 0xBB, 0x6A);
  private static readonly Color Frozen = Color.FromArgb(0x90, 0xA4, 0xAE);
  private static readonly Color Directory = Color.FromArgb(0xDA, 0xA5, 0x20);

  /// <summary>Maps at or above this size also get the click drill-down index.</summary>
  private const int BinningCacheThreshold = 100_000;

  /// <summary>A redraw slower than this is a regression; it is reported to the debugger only.</summary>
  private const long SlowRedrawWarnMs = 200;

  private const double TilePx = 6.0;

  // Projected-view constants, matching the previous renderer.
  private const double PlatterPerspectiveYScale = 0.42;
  private const int MaxStackedPlatters = 8;
  private const double PlatterInnerFraction = 0.22;
  private const double PlatterSpacingFraction = 0.5;
  private const double PlatterOpacity = 0.72;
  private const int CircularTargetWedges = 8192;

  private readonly ToolTip _toolTip = new() { InitialDelay = 300, AutoPopDelay = 8000 };

  private IReadOnlyList<DefragBlockInfo>? _blockMap;
  private long _imageSize;
  private long _readHead = -1;
  private long _writeHead = -1;
  private BlockMapView _viewMode = BlockMapView.LinearBlocks;

  // Cached binning, consumed by hit-testing and reused across redraws.
  private int _cols;
  private int _rows;
  private double _tileW;
  private double _tileH;
  private double _bytesPerTile;
  private DefragBlockKind[]? _tileKinds;
  private DefragBlockClass?[]? _tileClasses;
  private string?[]? _tileFiles;
  private long[]? _tileLengths;
  private long[]? _tileFreeBytes;
  private long[]? _tileUsedBytes;
  private long[]? _tileMetaBytes;
  private long[]? _tileBadBytes;

  // Compressed-sparse-row index from tile to contributing blocks, built only for large maps.
  private int[]? _tileBlockOffsets;
  private int[]? _tileBlockIndices;

  private object? _cachedMapRef;
  private long _cachedImageSize;
  private int _cachedMapCount;
  private string? _lastTooltip;

  public BlockMapControl() => this.BackColor = Color.WhiteSmoke;

  /// <summary>Raised on a left click over a tile in the linear view.</summary>
  public event EventHandler<TileClickedEventArgs>? TileClicked;

  public IReadOnlyList<DefragBlockInfo>? BlockMap {
    get => this._blockMap;
    set {
      this._blockMap = value;
      this.InvalidateBinningCache();
      this.Invalidate();
    }
  }

  public long ImageSize {
    get => this._imageSize;
    set {
      this._imageSize = value;
      this.InvalidateBinningCache();
      this.Invalidate();
    }
  }

  /// <summary>Read-head offset, or negative to hide the marker.</summary>
  public long ReadHead {
    get => this._readHead;
    set {
      this._readHead = value;
      this.Invalidate();
    }
  }

  /// <summary>Write-head offset, or negative to hide the marker.</summary>
  public long WriteHead {
    get => this._writeHead;
    set {
      this._writeHead = value;
      this.Invalidate();
    }
  }

  /// <summary>
  /// Which projection the map is drawn in: the linear tile grid, a circular platter, or a stack of
  /// platters, so the user can see roughly where data would physically sit on the medium.
  /// </summary>
  public BlockMapView ViewMode {
    get => this._viewMode;
    set {
      this._viewMode = value;
      this.Invalidate();
    }
  }

  /// <summary>
  /// Physical CHS geometry for the projected views. Set from the image's BPB when known; otherwise
  /// a heuristic geometry is derived from the image size.
  /// </summary>
  public MediaGeometry? Geometry { get; set; }

  /// <summary>Head movement must not re-bin, so only map identity and size invalidate.</summary>
  private void InvalidateBinningCache() {
    this._cachedMapRef = null;
    this._cachedImageSize = 0;
    this._cachedMapCount = 0;
    this._tileBlockOffsets = null;
    this._tileBlockIndices = null;
  }

  protected override void OnPaint(PaintEventArgs e) {
    var sw = Stopwatch.StartNew();

    var g = e.Graphics;
    var w = this.Width;
    var h = this.Height;
    if (w < 4 || h < 4) return;

    g.FillRectangle(Color.WhiteSmoke, new(0, 0, w, h));

    var map = this._blockMap;
    var imageSize = this._imageSize;
    if (map is null || map.Count == 0 || imageSize <= 0) {
      g.DrawText("No image loaded", DefaultTheme.Instance.DefaultFont, Color.Gray,
        new(0, 0, w, h), ContentAlignment.MiddleCenter);
      this.ClearTiles();
      return;
    }

    // Aim for roughly 6x6 pixel tiles, bounded so neither dimension explodes.
    var cols = (int)Math.Clamp(w / TilePx, 16, 240);
    var rows = (int)Math.Clamp(h / TilePx, 8, 120);
    var totalTiles = cols * rows;
    var bytesPerTile = (double)imageSize / totalTiles;

    var cacheHit = ReferenceEquals(map, this._cachedMapRef)
                && this._cachedImageSize == imageSize
                && this._cachedMapCount == map.Count
                && this._cols == cols
                && this._rows == rows
                && this._tileKinds is { } cached
                && cached.Length == totalTiles;

    if (!cacheHit) this.Bin(map, imageSize, cols, rows, totalTiles, bytesPerTile);

    this._cols = cols;
    this._rows = rows;
    this._bytesPerTile = bytesPerTile;

    var pixels = new int[w * h];
    if (this._viewMode is BlockMapView.CircularPlatter or BlockMapView.CylinderStack) {
      var geometry = this.Geometry ?? MediaGeometry.Heuristic(imageSize);
      if (this._viewMode == BlockMapView.CircularPlatter)
        this.DrawCircularPlatter(pixels, w, h, geometry, totalTiles, bytesPerTile);
      else
        this.DrawCylinderStack(pixels, w, h, geometry, totalTiles, bytesPerTile);

      // The wedge-to-byte mapping is many-to-one, so the projected views are not hit-testable.
      this._tileW = 0;
      this._tileH = 0;
    } else {
      this._tileW = (double)w / cols;
      this._tileH = (double)h / rows;
      this.DrawLinearGrid(pixels, w, h, cols, rows);
    }

    g.DrawImage(Images.FromArgb(w, h, pixels), new(0, 0, w, h));

    if (this._viewMode is BlockMapView.LinearBlocks or BlockMapView.LinearLba) {
      DrawHead(g, this._readHead, imageSize, w, h, Color.LimeGreen);
      DrawHead(g, this._writeHead, imageSize, w, h, Color.Orange);
    }

    sw.Stop();
    if (sw.ElapsedMilliseconds > SlowRedrawWarnMs)
      Debug.WriteLine($"[BlockMapControl] OnPaint slow: {sw.ElapsedMilliseconds} ms (blocks={map.Count:N0}, tiles={totalTiles}, cacheHit={cacheHit})");
  }

  private void ClearTiles() {
    this._tileKinds = null;
    this._tileClasses = null;
    this._tileFiles = null;
    this._tileLengths = null;
    this._tileBlockOffsets = null;
    this._tileBlockIndices = null;
    this._cachedMapRef = null;
  }

  /// <summary>
  /// Projects the block map onto the tile grid. Each tile records the kind that wins by priority —
  /// which drives the tooltip and the click selection — plus the byte count of every kind present,
  /// which drives the blended colour.
  /// </summary>
  private void Bin(IReadOnlyList<DefragBlockInfo> map, long imageSize, int cols, int rows, int totalTiles, double bytesPerTile) {
    var tileKinds = new DefragBlockKind[totalTiles];
    var tileClasses = new DefragBlockClass?[totalTiles];
    var tileFiles = new string?[totalTiles];
    var tileLengths = new long[totalTiles];
    var tileFreeBytes = new long[totalTiles];
    var tileUsedBytes = new long[totalTiles];
    var tileMetaBytes = new long[totalTiles];
    var tileBadBytes = new long[totalTiles];
    Array.Fill(tileKinds, DefragBlockKind.Free);

    var buildClickCache = map.Count >= BinningCacheThreshold;
    var perTileCount = buildClickCache ? new int[totalTiles] : null;

    foreach (var b in map) {
      var startTile = Math.Max(0, (int)(b.Offset / bytesPerTile));
      var endTile = Math.Min(totalTiles - 1, (int)((b.Offset + b.Length - 1) / bytesPerTile));
      var priority = KindPriority(b.Kind);

      for (var t = startTile; t <= endTile; ++t) {
        if (priority > KindPriority(tileKinds[t])) {
          tileKinds[t] = b.Kind;
          tileClasses[t] = b.Classification;
          tileFiles[t] = b.FileName;
          tileLengths[t] = b.Length;
        }

        // Attribute only the bytes that actually land in this tile, so a block straddling a
        // boundary does not count twice.
        var tileStart = (long)(t * bytesPerTile);
        var tileEnd = (long)((t + 1) * bytesPerTile);
        var overlap = Math.Max(0, Math.Min(b.Offset + b.Length, tileEnd) - Math.Max(b.Offset, tileStart));

        switch (b.Kind) {
          case DefragBlockKind.Free: tileFreeBytes[t] += overlap; break;
          case DefragBlockKind.Used: tileUsedBytes[t] += overlap; break;
          case DefragBlockKind.MetadataReserved: tileMetaBytes[t] += overlap; break;
          case DefragBlockKind.Bad: tileBadBytes[t] += overlap; break;
          // InProgress is an overlay, not a blended contribution.
        }

        if (perTileCount is not null) ++perTileCount[t];
      }
    }

    // Visibility guarantee: a used or bad extent whose mapped range rounded away entirely still
    // claims the tile holding its midpoint, so a small file never disappears behind metadata.
    foreach (var b in map) {
      if (b.Kind is not (DefragBlockKind.Used or DefragBlockKind.Bad) || b.Length <= 0) continue;

      var midTile = (int)((b.Offset + b.Length / 2) / bytesPerTile);
      if (midTile < 0 || midTile >= totalTiles) continue;
      if (KindPriority(tileKinds[midTile]) >= KindPriority(b.Kind)) continue;

      tileKinds[midTile] = b.Kind;
      tileClasses[midTile] = b.Classification;
      tileFiles[midTile] = b.FileName;
      tileLengths[midTile] = b.Length;
    }

    if (buildClickCache && perTileCount is not null)
      BuildClickIndex(map, totalTiles, bytesPerTile, perTileCount, out this._tileBlockOffsets, out this._tileBlockIndices);
    else {
      this._tileBlockOffsets = null;
      this._tileBlockIndices = null;
    }

    this._tileKinds = tileKinds;
    this._tileClasses = tileClasses;
    this._tileFiles = tileFiles;
    this._tileLengths = tileLengths;
    this._tileFreeBytes = tileFreeBytes;
    this._tileUsedBytes = tileUsedBytes;
    this._tileMetaBytes = tileMetaBytes;
    this._tileBadBytes = tileBadBytes;

    // Commit the cache identity only once binning succeeded; partial state would mislead the
    // next frame into skipping the work.
    this._cachedMapRef = map;
    this._cachedImageSize = imageSize;
    this._cachedMapCount = map.Count;
  }

  /// <summary>
  /// Builds a compressed-sparse-row index from tile to contributing block indices, so a click
  /// resolves in time proportional to the blocks in that tile rather than the whole map.
  /// </summary>
  private static void BuildClickIndex(
    IReadOnlyList<DefragBlockInfo> map, int totalTiles, double bytesPerTile, int[] perTileCount,
    out int[] offsets, out int[] indices) {
    offsets = new int[totalTiles + 1];
    var running = 0;
    for (var t = 0; t < totalTiles; ++t) {
      offsets[t] = running;
      running += perTileCount[t];
    }
    offsets[totalTiles] = running;

    indices = new int[running];
    var cursor = new int[totalTiles];
    var blockIndex = 0;

    foreach (var b in map) {
      var startTile = Math.Max(0, (int)(b.Offset / bytesPerTile));
      var endTile = Math.Min(totalTiles - 1, (int)((b.Offset + b.Length - 1) / bytesPerTile));
      for (var t = startTile; t <= endTile; ++t)
        indices[offsets[t] + cursor[t]++] = blockIndex;
      ++blockIndex;
    }
  }

  private void DrawLinearGrid(int[] pixels, int w, int h, int cols, int rows) {
    var colors = new int[cols * rows];
    for (var i = 0; i < colors.Length; ++i) colors[i] = ToArgb(this.BlendedColor(i));

    for (var y = 0; y < h; ++y) {
      var row = Math.Min(rows - 1, y * rows / h);
      var rowBase = row * cols;
      var dst = y * w;
      for (var x = 0; x < w; ++x)
        pixels[dst + x] = colors[rowBase + Math.Min(cols - 1, x * cols / w)];
    }
  }

  /// <summary>
  /// Paints the platter by inverting the projection per pixel: a pixel's radius picks the track and
  /// its angle picks the sector, which together name the wedge and therefore the tile. That is one
  /// pass over the viewport rather than thousands of filled wedges.
  /// </summary>
  private void DrawCircularPlatter(int[] pixels, int w, int h, MediaGeometry geom, int totalTiles, double bytesPerTile) {
    var cx = w / 2.0;
    var cy = h / 2.0;
    var maxR = Math.Min(w, h) * 0.46;
    this.PaintPlatter(pixels, w, h, cx, cy, maxR, maxR, geom, totalTiles, bytesPerTile, headBucket: -1, opacity: 1.0);

    // Spindle bore.
    FillEllipse(pixels, w, h, cx, cy, maxR * PlatterInnerFraction * 0.4, maxR * PlatterInnerFraction * 0.4, Color.Black, 1.0);
  }

  /// <summary>
  /// Stacks the platters back to front, each carrying only the LBAs whose head maps to it, and
  /// composites them at partial opacity so the whole stack stays visible instead of the front
  /// platter occluding the rest.
  /// </summary>
  private void DrawCylinderStack(int[] pixels, int w, int h, MediaGeometry geom, int totalTiles, double bytesPerTile) {
    var heads = Math.Min(MaxStackedPlatters, Math.Max(1, geom.Heads));
    var cx = w / 2.0;

    // Solve for radii and spacing so the whole stack fits the viewport.
    const double Gutter = 8.0;
    var availableH = Math.Max(40.0, h - 2 * Gutter);
    var availableW = Math.Max(40.0, w - 2 * Gutter);
    var ry = Math.Max(1.0, Math.Min(
      availableH / (2.0 + (heads - 1) * PlatterSpacingFraction),
      availableW / 2.0 * PlatterPerspectiveYScale));
    var rx = ry / PlatterPerspectiveYScale;
    var spacing = ry * PlatterSpacingFraction;
    var firstCy = Gutter + ry;

    for (var head = heads - 1; head >= 0; --head) {
      var cy = firstCy + head * spacing;
      this.PaintPlatter(pixels, w, h, cx, cy, rx, ry, geom, totalTiles, bytesPerTile, head, PlatterOpacity);
      FillEllipse(pixels, w, h, cx, cy, rx * PlatterInnerFraction * 0.35, ry * PlatterInnerFraction * 0.35, Color.Black, PlatterOpacity);
    }

    // The spindle shaft runs through every platter at full opacity.
    var shaftTop = (int)(firstCy - ry * PlatterInnerFraction * 0.4);
    var shaftBottom = (int)(firstCy + (heads - 1) * spacing + ry * PlatterInnerFraction * 0.4);
    var shaftHalf = Math.Max(1, (int)(rx * PlatterInnerFraction * 0.09));
    for (var y = Math.Max(0, shaftTop); y < Math.Min(h, shaftBottom); ++y)
      for (var x = Math.Max(0, (int)cx - shaftHalf); x < Math.Min(w, (int)cx + shaftHalf); ++x)
        pixels[y * w + x] = ToArgb(Color.DimGray);
  }

  /// <summary>
  /// Paints one platter. <paramref name="headBucket"/> of -1 draws every LBA (the flat view);
  /// otherwise only the LBAs whose head falls in that bucket are drawn.
  /// </summary>
  private void PaintPlatter(
    int[] pixels, int w, int h, double cx, double cy, double rx, double ry,
    MediaGeometry geom, int totalTiles, double bytesPerTile, int headBucket, double opacity) {
    var (tracks, sectors) = PickWedgeGrid(geom);
    var ringThickness = (1.0 - PlatterInnerFraction) / tracks;
    var lbasPerWedge = (double)geom.TotalSectors / ((long)tracks * sectors);
    var sourceHeads = Math.Max(1, geom.Heads);
    var drawnHeads = Math.Min(MaxStackedPlatters, sourceHeads);
    var spt = Math.Max(1, geom.SectorsPerTrack);

    var x0 = Math.Max(0, (int)(cx - rx) - 1);
    var x1 = Math.Min(w, (int)(cx + rx) + 2);
    var y0 = Math.Max(0, (int)(cy - ry) - 1);
    var y1 = Math.Min(h, (int)(cy + ry) + 2);

    var baseColor = ToArgb(Free);

    for (var y = y0; y < y1; ++y) {
      var ndy = (y + 0.5 - cy) / ry;
      for (var x = x0; x < x1; ++x) {
        var ndx = (x + 0.5 - cx) / rx;
        var radius = Math.Sqrt(ndx * ndx + ndy * ndy);
        if (radius > 1.0 || radius < PlatterInnerFraction) continue;

        // The platter surface itself, so empty tracks still read as a disc.
        Blend(pixels, y * w + x, baseColor, opacity);

        // Angle measured clockwise from twelve o'clock, matching the wedge layout.
        var angle = Math.Atan2(ndx, -ndy);
        if (angle < 0) angle += 2 * Math.PI;

        var track = (int)((1.0 - radius) / ringThickness);
        if (track < 0 || track >= tracks) continue;
        var sector = (int)(angle / (2 * Math.PI / sectors));
        if (sector < 0 || sector >= sectors) continue;

        var wedgeIndex = track * (long)sectors + sector;
        var lbaStart = (long)(wedgeIndex * lbasPerWedge);
        if (lbaStart >= geom.TotalSectors) continue;

        if (headBucket >= 0) {
          var realHead = (int)(lbaStart / spt % sourceHeads);
          var bucket = sourceHeads <= drawnHeads ? realHead : (int)((long)realHead * drawnHeads / sourceHeads);
          if (bucket != headBucket) continue;
        }

        var tileIndex = (int)Math.Clamp(lbaStart * geom.BytesPerSector / bytesPerTile, 0, totalTiles - 1);
        if (this.IsEmptyTile(tileIndex)) continue;

        Blend(pixels, y * w + x, ToArgb(this.BlendedColor(tileIndex)), opacity);
      }
    }
  }

  private bool IsEmptyTile(int index)
    => this._tileKinds is { } kinds
       && kinds[index] == DefragBlockKind.Free
       && this._tileUsedBytes![index] == 0
       && this._tileMetaBytes![index] == 0
       && this._tileBadBytes![index] == 0;

  /// <summary>
  /// Picks the ring and wedge counts from the medium's real geometry. When cylinders times
  /// sectors-per-track fits the budget, every real cylinder gets a ring and every real sector a
  /// wedge; past that both dimensions scale by the same factor so the real aspect survives.
  /// </summary>
  internal static (int Tracks, int SectorsPerTrack) PickWedgeGrid(MediaGeometry geom) {
    var cylinders = Math.Max(1, (int)Math.Min(geom.Cylinders, int.MaxValue));
    var spt = Math.Max(1, geom.SectorsPerTrack);
    var product = (long)cylinders * spt;
    if (product <= CircularTargetWedges) return (cylinders, spt);

    var scale = Math.Sqrt((double)product / CircularTargetWedges);
    return (Math.Max(1, (int)Math.Ceiling(cylinders / scale)), Math.Max(1, (int)Math.Ceiling(spt / scale)));
  }

  private static void Blend(int[] pixels, int index, int argb, double opacity) {
    if (opacity >= 1.0) {
      pixels[index] = argb;
      return;
    }

    var dst = unchecked((uint)pixels[index]);
    var src = unchecked((uint)argb);
    var a = opacity;

    var r = (byte)(((src >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * (1 - a));
    var g = (byte)(((src >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * (1 - a));
    var b = (byte)((src & 0xFF) * a + (dst & 0xFF) * (1 - a));
    pixels[index] = unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b));
  }

  private static void FillEllipse(int[] pixels, int w, int h, double cx, double cy, double rx, double ry, Color color, double opacity) {
    if (rx < 0.5 || ry < 0.5) return;
    var argb = ToArgb(color);

    for (var y = Math.Max(0, (int)(cy - ry)); y < Math.Min(h, (int)(cy + ry) + 1); ++y) {
      var ndy = (y + 0.5 - cy) / ry;
      for (var x = Math.Max(0, (int)(cx - rx)); x < Math.Min(w, (int)(cx + rx) + 1); ++x) {
        var ndx = (x + 0.5 - cx) / rx;
        if (ndx * ndx + ndy * ndy <= 1.0) Blend(pixels, y * w + x, argb, opacity);
      }
    }
  }

  private static int ToArgb(Color c) => unchecked((int)(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));

  private static void DrawHead(IGraphics g, long offset, long imageSize, int w, int h, Color color) {
    if (offset < 0 || imageSize <= 0) return;
    var x = (int)((double)offset / imageSize * w);
    g.DrawLine(color, x, 0, x, h, 2);
  }

  private static Color ColorFor(DefragBlockKind kind, DefragBlockClass? cls) => kind switch {
    DefragBlockKind.Free => Free,
    DefragBlockKind.Bad => Bad,
    DefragBlockKind.MetadataReserved => Meta,
    DefragBlockKind.InProgress => InProgress,
    DefragBlockKind.Used => UsedColor(cls),
    _ => Free,
  };

  private static Color UsedColor(DefragBlockClass? cls) => cls switch {
    DefragBlockClass.Hot => Hot,
    DefragBlockClass.Cold => Cold,
    DefragBlockClass.Frozen => Frozen,
    DefragBlockClass.Directory => Directory,
    _ => Normal,
  };

  /// <summary>
  /// Decides which kind wins when several share a tile, which is common for small files on a large
  /// image. This drives the tooltip and the click selection; the drawn colour blends instead.
  /// </summary>
  private static int KindPriority(DefragBlockKind kind) => kind switch {
    DefragBlockKind.InProgress => 5,
    DefragBlockKind.Used => 4,
    DefragBlockKind.Bad => 3,
    DefragBlockKind.MetadataReserved => 2,
    DefragBlockKind.Free => 1,
    _ => 0,
  };

  /// <summary>
  /// Blends the per-kind colours by byte count, with a floor that guarantees any used or bad bytes
  /// contribute at least 15% — without it a sixteen-byte file in a hundred-kilobyte tile renders as
  /// pure free space and vanishes.
  /// </summary>
  private Color BlendedColor(int tile) {
    if (this._tileKinds is not { } kinds) return Free;

    var dominant = kinds[tile];
    var cls = this._tileClasses![tile];
    var freeBytes = this._tileFreeBytes![tile];
    var usedBytes = this._tileUsedBytes![tile];
    var metaBytes = this._tileMetaBytes![tile];
    var badBytes = this._tileBadBytes![tile];

    var total = freeBytes + usedBytes + metaBytes + badBytes;
    if (total <= 0) return ColorFor(dominant, cls);

    const double MinFeatureWeight = 0.15;
    double fw = freeBytes, uw = usedBytes, mw = metaBytes, bw = badBytes;
    if (uw > 0) uw = Math.Max(uw, total * MinFeatureWeight);
    if (bw > 0) bw = Math.Max(bw, total * MinFeatureWeight);

    var sum = fw + uw + mw + bw;
    if (sum <= 0) return ColorFor(dominant, cls);

    var used = UsedColor(cls);
    return Color.FromArgb(
      Channel(fw * Free.R + uw * used.R + mw * Meta.R + bw * Bad.R, sum),
      Channel(fw * Free.G + uw * used.G + mw * Meta.G + bw * Bad.G, sum),
      Channel(fw * Free.B + uw * used.B + mw * Meta.B + bw * Bad.B, sum));

    static int Channel(double weighted, double sum) => Math.Clamp((int)(weighted / sum), 0, 255);
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);

    // The projected views map screen position non-linearly, so per-tile hover is linear-only.
    if (this._viewMode is BlockMapView.CircularPlatter or BlockMapView.CylinderStack) {
      this.SetTooltip(null);
      return;
    }

    if (this.TileAt(e.X, e.Y) is not { } tile || this._tileKinds is not { } kinds) {
      this.SetTooltip(null);
      return;
    }

    this.SetTooltip(BuildTooltipText(kinds[tile], this._tileClasses![tile], this._tileFiles![tile], this._tileLengths![tile]));
  }

  protected override void OnMouseLeave(EventArgs e) {
    base.OnMouseLeave(e);
    this.SetTooltip(null);
  }

  protected override void OnMouseDown(MouseEventArgs e) {
    base.OnMouseDown(e);

    if (this._blockMap is not { Count: > 0 } map) return;
    if (this.TileAt(e.X, e.Y) is not { } tile) return;
    if (this._bytesPerTile <= 0) return;

    var startOffset = (long)Math.Floor(tile * this._bytesPerTile);
    var endOffset = (long)Math.Floor((tile + 1) * this._bytesPerTile);
    if (this._imageSize > 0 && endOffset > this._imageSize) endOffset = this._imageSize;
    if (startOffset >= endOffset) return;

    List<DefragBlockInfo> contents;
    if (this._tileBlockOffsets is { } offsets && this._tileBlockIndices is { } indices && tile < offsets.Length - 1) {
      var lo = offsets[tile];
      var hi = offsets[tile + 1];
      contents = new(hi - lo);
      for (var i = lo; i < hi; ++i) {
        var index = indices[i];
        if (index >= 0 && index < map.Count) contents.Add(map[index]);
      }
    } else {
      // Small maps walk the list; a click is a discrete gesture so the linear cost is fine.
      contents = [];
      foreach (var b in map)
        if (b.Offset < endOffset && b.Offset + b.Length > startOffset)
          contents.Add(b);
    }

    this.Focus();
    this.TileClicked?.Invoke(this, new() {
      StartOffset = startOffset,
      EndOffset = endOffset,
      Contents = contents,
    });
  }

  private int? TileAt(int x, int y) {
    if (this._tileW <= 0 || this._tileH <= 0 || this._tileKinds is not { } kinds) return null;

    var col = (int)(x / this._tileW);
    var row = (int)(y / this._tileH);
    if (col < 0 || col >= this._cols || row < 0 || row >= this._rows) return null;

    var index = row * this._cols + col;
    return index >= 0 && index < kinds.Length ? index : null;
  }

  private void SetTooltip(string? text) {
    if (this._lastTooltip == text) return;
    this._lastTooltip = text;

    if (string.IsNullOrEmpty(text)) {
      this._toolTip.Hide();
      return;
    }

    this._toolTip.SetToolTip(this, text);
  }

  private static string BuildTooltipText(DefragBlockKind kind, DefragBlockClass? cls, string? name, long length) {
    var size = FormatBytes(length);
    return kind switch {
      DefragBlockKind.Free => $"Free space — {size}",
      DefragBlockKind.Bad => $"Bad / quarantined — {size}",
      DefragBlockKind.MetadataReserved => $"Metadata / reserved — {size}",
      DefragBlockKind.InProgress => name is not null ? $"{name} — {size} — In progress" : $"In progress — {size}",
      DefragBlockKind.Used => name is not null
        ? $"{name} — {length:N0} bytes — {cls ?? DefragBlockClass.Normal}"
        : $"Used — {length:N0} bytes — {cls ?? DefragBlockClass.Normal}",
      _ => $"{kind} — {size}",
    };
  }

  private static string FormatBytes(long bytes) => bytes switch {
    < 1024 => $"{bytes:N0} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };
}
