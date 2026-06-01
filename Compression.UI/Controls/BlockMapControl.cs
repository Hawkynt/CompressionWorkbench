using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compression.Registry;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using ToolTip = System.Windows.Controls.ToolTip;
using ToolTipService = System.Windows.Controls.ToolTipService;

namespace Compression.UI.Controls;

/// <summary>
/// Renders a <see cref="DefragProgressEvent.BlockMap"/> as a tile grid,
/// color-coded by <see cref="DefragBlockKind"/> + <see cref="DefragBlockClass"/>.
/// Optimised for 1000s..100Ks of blocks via fixed-grid binning: the image is
/// projected onto an N×M tile grid where each tile represents
/// <c>imageSize / (N*M)</c> bytes; the dominant block kind in that range
/// drives the tile colour. Read/write head offsets render as overlay markers.
/// </summary>
/// <remarks>
/// All drawing happens via direct <see cref="DrawingVisual"/> primitives, no
/// retained-mode WPF elements per tile, so the redraw cost stays flat as the
/// block count grows.
/// </remarks>
public sealed class BlockMapControl : FrameworkElement {

  // ── Brushes ────────────────────────────────────────────────────────
  // Kind colors — used when no Classification is set.
  private static readonly Brush FreeBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)); // light gray
  private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)); // red
  private static readonly Brush MetaBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)); // dark gray
  private static readonly Brush InProgressBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x00)); // amber

  // Classification colors — Hot→Frozen gradient.
  private static readonly Brush HotBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0x4A, 0x19)); // warm orange-red
  private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5)); // medium blue
  private static readonly Brush ColdBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)); // green
  private static readonly Brush FrozenBrush = new SolidColorBrush(Color.FromRgb(0x90, 0xA4, 0xAE)); // blue-grey
  private static readonly Brush DirectoryBrush = new SolidColorBrush(Color.FromRgb(0xDA, 0xA5, 0x20)); // goldenrod

  private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 0.5);
  private static readonly Pen ReadHeadPen = new(Brushes.LimeGreen, 2);
  private static readonly Pen WriteHeadPen = new(Brushes.Orange, 2);

  static BlockMapControl() {
    FreeBrush.Freeze(); BadBrush.Freeze(); MetaBrush.Freeze(); InProgressBrush.Freeze();
    HotBrush.Freeze(); NormalBrush.Freeze(); ColdBrush.Freeze(); FrozenBrush.Freeze();
    DirectoryBrush.Freeze();
    GridPen.Freeze(); ReadHeadPen.Freeze(); WriteHeadPen.Freeze();
  }

  public BlockMapControl() {
    // Tooltips need a reasonable hover delay so they don't pop up while the
    // user is just moving the cursor across the chart. ToolTipService
    // properties have to be set on the element instance.
    ToolTipService.SetInitialShowDelay(this, 300);
    ToolTipService.SetBetweenShowDelay(this, 200);
    ToolTipService.SetShowDuration(this, 8000);
    // Pre-attach an empty tooltip object so changes during MouseMove are
    // honoured immediately by the tooltip service.
    ToolTip = new ToolTip { Content = "", IsOpen = false };
  }

  // ── Cached layout state for hit-testing ────────────────────────────
  // Populated during OnRender, consumed by OnMouseMove to look up the
  // tile under the cursor without re-walking the block map. The binning
  // arrays (_tileKinds/_tileClasses/_tileFiles/_tileLengths) are also
  // cached against (_cachedMapRef, _cols, _rows, _cachedImageSize) so
  // subsequent redraws — for read/write-head movement, resize within the
  // same tile dimensions, etc. — skip the per-block walk entirely.
  // Without this, OnRender on a 1M-entry BlockMap walks every entry on
  // every redraw, which freezes the UI thread.
  private int _cols;
  private int _rows;
  private double _tileW;
  private double _tileH;
  private double _bytesPerTile;
  private DefragBlockKind[]? _tileKinds;
  private DefragBlockClass?[]? _tileClasses;
  private string?[]? _tileFiles;
  private long[]? _tileLengths; // per-tile representative byte length (file size when Used, region length otherwise)
  // Per-tile byte-count breakdown — used for color blending so tiles with
  // mixed content render a weighted blend instead of letting a single kind
  // dominate. Without this, a tile with 95% Free + 5% Used renders the same
  // as a tile with 100% Used.
  private long[]? _tileFreeBytes;
  private long[]? _tileUsedBytes;
  private long[]? _tileMetaBytes;
  private long[]? _tileBadBytes;

  // Click-drill-down cache. A CSR (compressed sparse row) layout: for tile
  // index t, the contributing block indices live in
  // _tileBlockIndices[_tileBlockOffsets[t].._tileBlockOffsets[t+1]).
  // Only built for large maps (>= BinningCacheThreshold) to keep small-map
  // memory footprint zero. Lookup at click time is O(k) where k = blocks
  // in the clicked tile, not O(N) over the full map.
  private int[]? _tileBlockOffsets;
  private int[]? _tileBlockIndices;

  // Identity of the cache. We use ReferenceEquals on the BlockMap so a new
  // list (even with identical contents) re-bins; this matches the WPF
  // dependency-property semantics — callers replace BlockMap, they don't
  // mutate it in place.
  private object? _cachedMapRef;
  private long _cachedImageSize;
  private int _cachedMapCount;

  // Maps with at least this many blocks switch on the binning cache + the
  // click-drill-down CSR cache. Below this threshold we behave exactly as
  // before — small-image rendering stays untouched.
  private const int BinningCacheThreshold = 100_000;

  // OnRender wall-clock budget. Anything slower indicates we've regressed;
  // a Debug.WriteLine surfaces it without disturbing release builds.
  private const long SlowRedrawWarnMs = 200;

  // ── Public state ───────────────────────────────────────────────────

  public IReadOnlyList<DefragBlockInfo>? BlockMap {
    get => (IReadOnlyList<DefragBlockInfo>?)GetValue(BlockMapProperty);
    set => SetValue(BlockMapProperty, value);
  }

  public long ImageSize {
    get => (long)GetValue(ImageSizeProperty);
    set => SetValue(ImageSizeProperty, value);
  }

  public long ReadHead {
    get => (long)GetValue(ReadHeadProperty);
    set => SetValue(ReadHeadProperty, value);
  }

  public long WriteHead {
    get => (long)GetValue(WriteHeadProperty);
    set => SetValue(WriteHeadProperty, value);
  }

  /// <summary>Which projection the block map is drawn in: the classic linear
  /// tile grid, a 2-D circular platter, or a 3-D cylinder stack. Lets the user
  /// see roughly where data would physically reside on the medium.</summary>
  public Compression.Core.Layout.BlockMapView ViewMode {
    get => (Compression.Core.Layout.BlockMapView)GetValue(ViewModeProperty);
    set => SetValue(ViewModeProperty, value);
  }

  public static readonly DependencyProperty ViewModeProperty =
    DependencyProperty.Register(nameof(ViewMode), typeof(Compression.Core.Layout.BlockMapView),
      typeof(BlockMapControl), new FrameworkPropertyMetadata(
        Compression.Core.Layout.BlockMapView.LinearBlocks, FrameworkPropertyMetadataOptions.AffectsRender));

  /// <summary>Physical CHS geometry used by the circular/cylinder projections.
  /// Set from the image's BPB when known; defaults to standard 63×255 geometry
  /// derived from <see cref="ImageSize"/>.</summary>
  public Compression.Core.Layout.MediaGeometry? Geometry { get; set; }

  public static readonly DependencyProperty BlockMapProperty =
    DependencyProperty.Register(nameof(BlockMap), typeof(IReadOnlyList<DefragBlockInfo>),
      typeof(BlockMapControl), new FrameworkPropertyMetadata(null,
        FrameworkPropertyMetadataOptions.AffectsRender, OnBlockMapOrSizeChanged));

  public static readonly DependencyProperty ImageSizeProperty =
    DependencyProperty.Register(nameof(ImageSize), typeof(long),
      typeof(BlockMapControl), new FrameworkPropertyMetadata(0L,
        FrameworkPropertyMetadataOptions.AffectsRender, OnBlockMapOrSizeChanged));

  /// <summary>
  /// Invalidate the binning cache when the BlockMap reference or ImageSize
  /// changes. Read/Write head movement does not invalidate (those properties
  /// have no callback) — they only trigger redraws, and the cached binning
  /// is reused so the per-tick redraw stays cheap.
  /// </summary>
  private static void OnBlockMapOrSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
    if (d is BlockMapControl ctrl) ctrl.InvalidateBinningCache();
  }

  private void InvalidateBinningCache() {
    this._cachedMapRef = null;
    this._cachedImageSize = 0;
    this._cachedMapCount = 0;
    this._tileBlockOffsets = null;
    this._tileBlockIndices = null;
  }

  public static readonly DependencyProperty ReadHeadProperty =
    DependencyProperty.Register(nameof(ReadHead), typeof(long),
      typeof(BlockMapControl), new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

  public static readonly DependencyProperty WriteHeadProperty =
    DependencyProperty.Register(nameof(WriteHead), typeof(long),
      typeof(BlockMapControl), new FrameworkPropertyMetadata(-1L, FrameworkPropertyMetadataOptions.AffectsRender));

  // ── Rendering ──────────────────────────────────────────────────────

  protected override void OnRender(DrawingContext dc) {
    var sw = Stopwatch.StartNew();

    var w = ActualWidth;
    var h = ActualHeight;
    if (w < 4 || h < 4) return;

    dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, w, h));

    var map = BlockMap;
    var imageSize = ImageSize;
    if (map == null || map.Count == 0 || imageSize <= 0) {
      // Empty placeholder.
      var msg = new FormattedText("No image loaded", System.Globalization.CultureInfo.InvariantCulture,
        System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Gray, 1.0);
      dc.DrawText(msg, new Point((w - msg.Width) / 2, (h - msg.Height) / 2));
      this._tileKinds = null;
      this._tileClasses = null;
      this._tileFiles = null;
      this._tileLengths = null;
      this._tileBlockOffsets = null;
      this._tileBlockIndices = null;
      this._cachedMapRef = null;
      return;
    }

    // Choose tile grid: aim for ~6×6px tiles, capped at 200×100 max.
    const double TilePx = 6.0;
    var cols = (int)Math.Clamp(w / TilePx, 16, 240);
    var rows = (int)Math.Clamp(h / TilePx, 8, 120);
    var totalTiles = cols * rows;
    var bytesPerTile = (double)imageSize / totalTiles;
    var tileW = w / cols;
    var tileH = h / rows;

    // Cache lookup. We re-bin only when (BlockMap reference, image size,
    // tile grid dimensions) actually change. Read/Write head moves and
    // resize-within-same-tiledims paths reuse the cached arrays.
    var cacheHit = ReferenceEquals(map, this._cachedMapRef)
                && this._cachedImageSize == imageSize
                && this._cachedMapCount == map.Count
                && this._cols == cols
                && this._rows == rows
                && this._tileKinds != null
                && this._tileKinds.Length == totalTiles;

    DefragBlockKind[] tileKinds;
    DefragBlockClass?[] tileClasses;
    string?[] tileFiles;
    long[] tileLengths;
    long[] tileFreeBytes;
    long[] tileUsedBytes;
    long[] tileMetaBytes;
    long[] tileBadBytes;
    if (cacheHit) {
      tileKinds = this._tileKinds!;
      tileClasses = this._tileClasses!;
      tileFiles = this._tileFiles!;
      tileLengths = this._tileLengths!;
      tileFreeBytes = this._tileFreeBytes!;
      tileUsedBytes = this._tileUsedBytes!;
      tileMetaBytes = this._tileMetaBytes!;
      tileBadBytes = this._tileBadBytes!;
    } else {
      // For each tile, decide its dominant block kind/class. Walk the map
      // once and accumulate per-tile counters. This is the expensive step
      // for million-entry maps — we only run it on cache miss.
      tileKinds = new DefragBlockKind[totalTiles];
      tileClasses = new DefragBlockClass?[totalTiles];
      tileFiles = new string?[totalTiles];
      tileLengths = new long[totalTiles];
      tileFreeBytes = new long[totalTiles];
      tileUsedBytes = new long[totalTiles];
      tileMetaBytes = new long[totalTiles];
      tileBadBytes = new long[totalTiles];
      for (var i = 0; i < totalTiles; i++) tileKinds[i] = DefragBlockKind.Free;

      // For very large maps, also build a CSR (compressed sparse row)
      // structure so click drill-down is O(blocks-in-tile) instead of O(N).
      // Two passes: first count entries per tile to size the index array,
      // then a second pass to fill it.
      var buildClickCache = map.Count >= BinningCacheThreshold;
      int[]? perTileCount = null;
      if (buildClickCache) {
        perTileCount = new int[totalTiles];
      }

      foreach (var b in map) {
        var startTile = (int)(b.Offset / bytesPerTile);
        var endTile = (int)((b.Offset + b.Length - 1) / bytesPerTile);
        if (startTile < 0) startTile = 0;
        if (endTile >= totalTiles) endTile = totalTiles - 1;
        // Priority: Used > MetadataReserved > Bad > Free; InProgress always wins.
        // The "dominant" kind drives the hover tooltip and the click-through
        // selection. The actual rendered color BLENDS all kinds present in a
        // tile by their byte proportion, so mixed tiles aren't misleading.
        var bPriority = KindPriority(b.Kind);
        for (var t = startTile; t <= endTile; t++) {
          if (bPriority > KindPriority(tileKinds[t])) {
            tileKinds[t] = b.Kind;
            tileClasses[t] = b.Classification;
            tileFiles[t] = b.FileName;
            tileLengths[t] = b.Length;
          }

          // Accumulate byte-count for color blending. Compute the per-tile
          // overlap range so a block straddling a tile boundary attributes
          // only the bytes that actually fall in this tile.
          var tileStartByte = (long)(t * bytesPerTile);
          var tileEndByte = (long)((t + 1) * bytesPerTile);
          var overlapStart = Math.Max(b.Offset, tileStartByte);
          var overlapEnd = Math.Min(b.Offset + b.Length, tileEndByte);
          var overlapBytes = Math.Max(0, overlapEnd - overlapStart);
          switch (b.Kind) {
            case DefragBlockKind.Free: tileFreeBytes[t] += overlapBytes; break;
            case DefragBlockKind.Used: tileUsedBytes[t] += overlapBytes; break;
            case DefragBlockKind.MetadataReserved: tileMetaBytes[t] += overlapBytes; break;
            case DefragBlockKind.Bad: tileBadBytes[t] += overlapBytes; break;
            // InProgress is drawn as an overlay, not blended.
          }
          if (perTileCount != null) perTileCount[t]++;
        }
      }

      // Visibility guarantee: any Used or Bad extent that fell within
      // bytes-per-tile of a boundary may have ended up sharing exactly one
      // tile with adjacent metadata. With the priority logic above the file
      // wins, but let's also defend against the case where rounding pushed
      // a tiny Used extent into a tile that was never visited by the foreach
      // (zero-length edge case or float-truncation hit on the boundary).
      // For each Used/Bad extent whose mapped range produced no Used/Bad
      // tile color, force-set the tile containing its midpoint.
      foreach (var b in map) {
        if (b.Kind != DefragBlockKind.Used && b.Kind != DefragBlockKind.Bad) continue;
        if (b.Length <= 0) continue;
        var midOffset = b.Offset + b.Length / 2;
        var midTile = (int)(midOffset / bytesPerTile);
        if (midTile < 0 || midTile >= totalTiles) continue;
        if (KindPriority(tileKinds[midTile]) < KindPriority(b.Kind)) {
          tileKinds[midTile] = b.Kind;
          tileClasses[midTile] = b.Classification;
          tileFiles[midTile] = b.FileName;
          tileLengths[midTile] = b.Length;
        }
      }

      if (buildClickCache && perTileCount != null) {
        // Prefix-sum into offsets, then fill indices in a second pass. The
        // resulting CSR layout is exact — no sentinel slots, no per-tile
        // list allocation.
        var offsets = new int[totalTiles + 1];
        var running = 0;
        for (var t = 0; t < totalTiles; t++) {
          offsets[t] = running;
          running += perTileCount[t];
        }
        offsets[totalTiles] = running;

        var indices = new int[running];
        var cursor = new int[totalTiles]; // running write position per tile
        var idx = 0;
        foreach (var b in map) {
          var startTile = (int)(b.Offset / bytesPerTile);
          var endTile = (int)((b.Offset + b.Length - 1) / bytesPerTile);
          if (startTile < 0) startTile = 0;
          if (endTile >= totalTiles) endTile = totalTiles - 1;
          for (var t = startTile; t <= endTile; t++) {
            indices[offsets[t] + cursor[t]] = idx;
            cursor[t]++;
          }
          idx++;
        }
        this._tileBlockOffsets = offsets;
        this._tileBlockIndices = indices;
      } else {
        this._tileBlockOffsets = null;
        this._tileBlockIndices = null;
      }

      // Commit the cache identity only after the binning has completed
      // successfully; partial state would mislead the next frame.
      this._cachedMapRef = map;
      this._cachedImageSize = imageSize;
      this._cachedMapCount = map.Count;
    }

    // Draw tiles with byte-weighted color blending. A tile holding 90% Free
    // and 10% Used renders as a blend of light-gray and the Used color
    // proportional to the byte counts, so even small Used regions tint
    // visibly without ALSO hiding the dominant context.
    var view = ViewMode;
    if (view is Compression.Core.Layout.BlockMapView.CircularPlatter
             or Compression.Core.Layout.BlockMapView.CylinderStack) {
      // Projected views: render each tile as a filled annular wedge sitting at
      // its physical platter coordinate. The wedge spans the angular range of
      // its sector(s) and the radial thickness of its track(s), so the result
      // is a solid doughnut (2-D) or stack of platter discs (3-D) — no dot
      // cloud, no gaps between tiles. Hit-testing is disabled because the
      // wedge → byte-range mapping is non-trivial; the linear view stays the
      // canonical interactive surface.
      // Default to Heuristic CHS so the donut reflects realistic geometry
      // (80×2×18 for a 1.44 MB floppy, 96×64×32 for a Zip, scaled HDD for
      // larger images) rather than Standard's fixed 255-head/63-spt that
      // makes a floppy degenerate to a single ring.
      var geom = Geometry ?? Compression.Core.Layout.MediaGeometry.Heuristic(imageSize);
      if (view == Compression.Core.Layout.BlockMapView.CircularPlatter) {
        DrawCircularPlatter(dc, w, h, geom, totalTiles, bytesPerTile,
          tileKinds, tileClasses, tileFreeBytes, tileUsedBytes, tileMetaBytes, tileBadBytes);
      } else {
        DrawCylinderStack(dc, w, h, geom, totalTiles, bytesPerTile,
          tileKinds, tileClasses, tileFreeBytes, tileUsedBytes, tileMetaBytes, tileBadBytes);
      }
      // Disable grid hit-testing for the projected views — the linear→wedge
      // mapping is many-to-one so a pixel can't reverse to a single tile.
      this._cols = 0;
      this._rows = 0;
      this._tileW = 0;
      this._tileH = 0;
      this._bytesPerTile = bytesPerTile;
      this._tileKinds = tileKinds;
      this._tileClasses = tileClasses;
      this._tileFiles = tileFiles;
      this._tileLengths = tileLengths;
      this._tileFreeBytes = tileFreeBytes;
      this._tileUsedBytes = tileUsedBytes;
      this._tileMetaBytes = tileMetaBytes;
      this._tileBadBytes = tileBadBytes;
      sw.Stop();
      if (sw.ElapsedMilliseconds > SlowRedrawWarnMs) {
        Debug.WriteLine($"[BlockMapControl] OnRender slow (projected): {sw.ElapsedMilliseconds} ms (blocks={map.Count:N0}, tiles={totalTiles}, cacheHit={cacheHit})");
      }
      return;
    }

    {
      // Linear tile grid (LinearBlocks / LinearLba): row-major by byte offset.
      for (var i = 0; i < totalTiles; i++) {
        var col = i % cols;
        var row = i / cols;
        var x = col * tileW;
        var y = row * tileH;
        var rect = new Rect(x, y, tileW + 0.5, tileH + 0.5);
        var brush = BlendedBrush(tileKinds[i], tileClasses[i],
          tileFreeBytes[i], tileUsedBytes[i], tileMetaBytes[i], tileBadBytes[i]);
        dc.DrawRectangle(brush, null, rect);
      }

      // Read/write head markers only make sense over the linear grid.
      DrawHead(dc, ReadHead, imageSize, w, h, ReadHeadPen);
      DrawHead(dc, WriteHead, imageSize, w, h, WriteHeadPen);
    }

    // Cache the layout for hit-testing in OnMouseMove.
    this._cols = cols;
    this._rows = rows;
    this._tileW = tileW;
    this._tileH = tileH;
    this._bytesPerTile = bytesPerTile;
    this._tileKinds = tileKinds;
    this._tileClasses = tileClasses;
    this._tileFiles = tileFiles;
    this._tileLengths = tileLengths;
    this._tileFreeBytes = tileFreeBytes;
    this._tileUsedBytes = tileUsedBytes;
    this._tileMetaBytes = tileMetaBytes;
    this._tileBadBytes = tileBadBytes;

    sw.Stop();
    if (sw.ElapsedMilliseconds > SlowRedrawWarnMs) {
      Debug.WriteLine($"[BlockMapControl] OnRender slow: {sw.ElapsedMilliseconds} ms (blocks={map.Count:N0}, tiles={totalTiles}, cacheHit={cacheHit})");
    }
  }

  // ── Projected (platter / cylinder-stack) rendering ────────────────
  // Each tile renders as a filled annular wedge sitting at its physical
  // (cylinder, head, sector) coordinate, the way a real defrag tool
  // (UltimateDefrag etc.) draws data on a platter. Two simplifications
  // vs. real hardware:
  // 1. We aggregate by TILE (not by sector) so the wedge count caps at the
  //    OnRender tile budget — high-LBA images don't explode into millions
  //    of draw calls. The tile's dominant kind + per-kind byte breakdown
  //    drives the same BlendedBrush as the linear view.
  // 2. The 3-D stack flattens platter perspective to a Y-scaled annulus
  //    (ellipse) so the cylinder math stays 2-D; no real 3-D transform is
  //    required. The 3/4-view angle (looking down + side) is approximated
  //    by ry = rx * PlatterPerspectiveYScale.

  /// <summary>Aspect-ratio Y-scale for the 3-D stacked-platter view —
  /// platters render as elliptical discs with vertical squash matching a
  /// ~3/4 view angle (looking down + to the side).</summary>
  private const double PlatterPerspectiveYScale = 0.42;

  /// <summary>How many heads to show in the 3-D stack — capped so a
  /// 255-head standard geometry doesn't draw 255 platters. Real consumer
  /// drives top out around 8 platters anyway.</summary>
  private const int MaxStackedPlatters = 8;

  /// <summary>Spindle-hole fraction for the projected views — the inner
  /// radius of the platter relative to its outer radius.</summary>
  private const double PlatterInnerFraction = 0.22;

  /// <summary>Vertical separation between consecutive platters in the 3D
  /// stack, expressed as a fraction of <c>ry</c>. 0.5 = each platter sits
  /// half a platter-height below the previous one (significant overlap →
  /// clear stacked-disk look). Combined with <see cref="PlatterOpacity"/>
  /// this is what gives the view its depth.</summary>
  private const double PlatterSpacingFraction = 0.5;

  /// <summary>Per-platter opacity in the 3D stack. Values below 1.0 let
  /// back platters bleed through front ones so the whole stack is visible
  /// at once instead of the top platter occluding everything behind it.
  /// 0.72 is the sweet spot — bright enough to read individual platters,
  /// transparent enough to see through to neighbours.</summary>
  private const double PlatterOpacity = 0.72;

  /// <summary>Target wedge count for the circular view — chosen so each
  /// wedge spans ≥ 1 pixel on a typical 800-px window. Higher values give
  /// finer LBA resolution; lower values give faster redraw.</summary>
  private const int CircularTargetWedges = 8192;

  private static void DrawCircularPlatter(
      DrawingContext dc, double w, double h,
      Compression.Core.Layout.MediaGeometry geom,
      int totalTiles, double bytesPerTile,
      DefragBlockKind[] tileKinds, DefragBlockClass?[] tileClasses,
      long[] tileFreeBytes, long[] tileUsedBytes, long[] tileMetaBytes, long[] tileBadBytes) {
    var cx = w / 2;
    var cy = h / 2;
    var maxR = Math.Min(w, h) * 0.46;

    // Base annulus — the platter surface — so even regions covered by
    // light-coloured wedges still look like a disk and not transparent.
    dc.DrawGeometry(FreeBrush, null, BuildAnnulus(cx, cy, maxR * PlatterInnerFraction, maxR));

    // Per-(track, sector-bucket) iteration: each wedge is a small angular
    // slice of one concentric ring, NOT a full ring summarising a tile-range
    // that spans tracks. Outer ring = low LBAs; inner ring = high LBAs.
    // Track count comes from REAL geometry (geom.Cylinders) so the visual
    // matches the medium's actual cylinder layout.
    var (tracksToShow, sectorsToShow) = PickWedgeGrid(geom);
    var lbasPerWedge = (double)geom.TotalSectors / (tracksToShow * sectorsToShow);
    var ringThickness = (1.0 - PlatterInnerFraction) / tracksToShow;
    var totalSectors = geom.TotalSectors;
    for (var track = 0; track < tracksToShow; track++) {
      var rOuterFrac = 1.0 - track * ringThickness;
      var rInnerFrac = 1.0 - (track + 1) * ringThickness;
      for (var sec = 0; sec < sectorsToShow; sec++) {
        var wedgeIndex = track * (long)sectorsToShow + sec;
        var lbaStart = (long)(wedgeIndex * lbasPerWedge);
        if (lbaStart >= totalSectors) break;
        var lbaEnd = Math.Min(totalSectors, (long)((wedgeIndex + 1) * lbasPerWedge)) - 1;

        var byteStart = lbaStart * geom.BytesPerSector;
        var byteEnd = Math.Min(byteStart + (lbaEnd - lbaStart + 1) * geom.BytesPerSector - 1,
                               (long)(totalTiles * bytesPerTile) - 1);
        // Pick the tile that owns the midpoint — adequate when each wedge
        // covers a single tile, accurate enough when it covers many.
        var midByte = (byteStart + byteEnd) / 2;
        var tileIdx = (int)Math.Clamp((long)(midByte / bytesPerTile), 0, totalTiles - 1);
        if (tileKinds[tileIdx] == DefragBlockKind.Free
            && tileUsedBytes[tileIdx] == 0 && tileMetaBytes[tileIdx] == 0 && tileBadBytes[tileIdx] == 0)
          continue;

        var startAngle = 2.0 * Math.PI * sec / sectorsToShow;
        var sweep = 2.0 * Math.PI / sectorsToShow;
        var wedge = new Compression.Core.Layout.PlatterWedge(startAngle, sweep, rInnerFrac, rOuterFrac);
        var brush = BlendedBrush(tileKinds[tileIdx], tileClasses[tileIdx],
          tileFreeBytes[tileIdx], tileUsedBytes[tileIdx], tileMetaBytes[tileIdx], tileBadBytes[tileIdx]);
        var geo = BuildWedgeGeometryElliptical(cx, cy, maxR, maxR, wedge);
        dc.DrawGeometry(brush, null, geo);
      }
    }

    // Spindle hole — flat black dot in the centre so the platter reads as
    // a real disc with a centre bore.
    dc.DrawEllipse(Brushes.Black, null, new Point(cx, cy),
      maxR * PlatterInnerFraction * 0.4, maxR * PlatterInnerFraction * 0.4);
  }

  /// <summary>
  /// Picks (tracksToShow, sectorsToShow) using the medium's REAL CHS
  /// geometry. When <c>Cylinders × SectorsPerTrack</c> fits in
  /// <see cref="CircularTargetWedges"/>, every real cylinder gets its
  /// own ring and every real sector its own wedge. When the product
  /// exceeds the budget, both dimensions are scaled down by the same
  /// factor so the aspect ratio of the real geometry is preserved.
  /// </summary>
  internal static (int Tracks, int SectorsPerTrack) PickWedgeGrid(Compression.Core.Layout.MediaGeometry geom) {
    var realCyls = Math.Max(1, (int)Math.Min(geom.Cylinders, int.MaxValue));
    var realSpt = Math.Max(1, geom.SectorsPerTrack);
    var product = (long)realCyls * realSpt;
    if (product <= CircularTargetWedges) return (realCyls, realSpt);
    // Scale both dims by the same factor so the ratio of cylinders to
    // sectors-per-track is preserved (= the real geometry's aspect).
    var scale = Math.Sqrt((double)product / CircularTargetWedges);
    var tracks = Math.Max(1, (int)Math.Ceiling(realCyls / scale));
    var spt = Math.Max(1, (int)Math.Ceiling(realSpt / scale));
    return (tracks, spt);
  }

  private static void DrawCylinderStack(
      DrawingContext dc, double w, double h,
      Compression.Core.Layout.MediaGeometry geom,
      int totalTiles, double bytesPerTile,
      DefragBlockKind[] tileKinds, DefragBlockClass?[] tileClasses,
      long[] tileFreeBytes, long[] tileUsedBytes, long[] tileMetaBytes, long[] tileBadBytes) {
    var heads = Math.Min(MaxStackedPlatters, Math.Max(1, geom.Heads));
    var cx = w / 2;

    // Solve for (rx, ry, platterSpacing) so the WHOLE stack fits inside the
    // viewport with small margins. Each platter is an ellipse of half-axes
    // (rx, ry); consecutive platters sit `platterSpacing` apart vertically.
    // Total vertical span = 2·ry (top platter top → top platter bottom)
    //                     + (heads-1) · spacing (drop from top to bottom centre)
    // With spacing = ry · PlatterSpacingFraction, the constraint becomes
    //   ry · (2 + (heads-1) · PlatterSpacingFraction) ≤ availableH
    // and we ALSO require 2·rx ≤ availableW with ry = rx · PerspectiveYScale.
    const double Margin = 8.0;
    var availableH = Math.Max(40.0, h - 2 * Margin);
    var availableW = Math.Max(40.0, w - 2 * Margin);
    var rySpan = 2.0 + (heads - 1) * PlatterSpacingFraction;
    var ryFromH = availableH / rySpan;
    var ryFromW = (availableW / 2.0) * PlatterPerspectiveYScale;
    var ry = Math.Max(1.0, Math.Min(ryFromH, ryFromW));
    var rx = ry / PlatterPerspectiveYScale;
    var platterSpacing = ry * PlatterSpacingFraction;
    var firstPlatterCy = Margin + ry; // top platter centre Y

    // Per-platter (track × sector-bucket) iteration: each platter holds
    // the slice of LBAs whose head index maps to that platter's bucket.
    // Same wedge-grid sizing as the 2-D view, but scoped per-head.
    var (tracksToShow, sectorsToShow) = PickWedgeGrid(geom);
    var ringThickness = (1.0 - PlatterInnerFraction) / tracksToShow;
    var sourceHeads = Math.Max(1, geom.Heads);
    var spt = Math.Max(1, geom.SectorsPerTrack);

    // Paint back-to-front: last head (lowest on screen) first, head 0 last.
    // Each platter is drawn inside its own PushOpacity scope so the layers
    // composite with see-through depth (otherwise the front platter fully
    // occludes everything behind it).
    for (var hh = heads - 1; hh >= 0; hh--) {
      var cy = firstPlatterCy + hh * platterSpacing;
      dc.PushOpacity(PlatterOpacity);

      // Base platter surface — light grey ellipse so empty tracks read
      // as visible disc surface (and not as background).
      dc.DrawGeometry(FreeBrush, null,
        BuildAnnulusElliptical(cx, cy, rx * PlatterInnerFraction, ry * PlatterInnerFraction, rx, ry));

      for (var track = 0; track < tracksToShow; track++) {
        var rOuterFrac = 1.0 - track * ringThickness;
        var rInnerFrac = 1.0 - (track + 1) * ringThickness;
        for (var sec = 0; sec < sectorsToShow; sec++) {
          var wedgeIndex = track * (long)sectorsToShow + sec;
          var lbaStart = (long)(wedgeIndex * (double)geom.TotalSectors / (tracksToShow * sectorsToShow));
          if (lbaStart >= geom.TotalSectors) break;

          // Bucket this LBA's head into one of our drawn platters.
          var realHead = (int)((lbaStart / spt) % sourceHeads);
          var bucket = sourceHeads <= heads ? realHead : (int)((long)realHead * heads / sourceHeads);
          if (bucket != hh) continue;

          var byteStart = lbaStart * geom.BytesPerSector;
          var tileIdx = (int)Math.Clamp((long)(byteStart / bytesPerTile), 0, totalTiles - 1);
          if (tileKinds[tileIdx] == DefragBlockKind.Free
              && tileUsedBytes[tileIdx] == 0 && tileMetaBytes[tileIdx] == 0 && tileBadBytes[tileIdx] == 0)
            continue;

          var startAngle = 2.0 * Math.PI * sec / sectorsToShow;
          var sweep = 2.0 * Math.PI / sectorsToShow;
          var wedge = new Compression.Core.Layout.PlatterWedge(startAngle, sweep, rInnerFrac, rOuterFrac);
          var brush = BlendedBrush(tileKinds[tileIdx], tileClasses[tileIdx],
            tileFreeBytes[tileIdx], tileUsedBytes[tileIdx], tileMetaBytes[tileIdx], tileBadBytes[tileIdx]);
          var geo = BuildWedgeGeometryElliptical(cx, cy, rx, ry, wedge);
          dc.DrawGeometry(brush, null, geo);
        }
      }

      // Spindle hole on this platter.
      dc.DrawEllipse(Brushes.Black, null, new Point(cx, cy),
        rx * PlatterInnerFraction * 0.35, ry * PlatterInnerFraction * 0.35);
      dc.Pop(); // PushOpacity(PlatterOpacity)
    }

    // Spindle shaft — connects all platters vertically through the centre.
    // Drawn at full opacity so it reads as one continuous shaft above/below
    // the see-through platters.
    var shaftTop = firstPlatterCy - ry * PlatterInnerFraction * 0.4;
    var shaftBottom = firstPlatterCy + (heads - 1) * platterSpacing + ry * PlatterInnerFraction * 0.4;
    var shaftPen = new Pen(Brushes.DimGray, Math.Max(1.5, rx * PlatterInnerFraction * 0.18));
    dc.DrawLine(shaftPen, new Point(cx, shaftTop), new Point(cx, shaftBottom));
  }

  /// <summary>Builds a filled circular annulus (donut) — used as the base
  /// platter surface so empty regions still look like a disk.</summary>
  private static Geometry BuildAnnulus(double cx, double cy, double rInner, double rOuter) {
    var sg = new StreamGeometry { FillRule = FillRule.EvenOdd };
    using (var ctx = sg.Open()) {
      ctx.BeginFigure(new Point(cx + rOuter, cy), true, true);
      ctx.ArcTo(new Point(cx - rOuter, cy), new Size(rOuter, rOuter), 0, false, SweepDirection.Clockwise, true, false);
      ctx.ArcTo(new Point(cx + rOuter, cy), new Size(rOuter, rOuter), 0, false, SweepDirection.Clockwise, true, false);
      if (rInner > 0.5) {
        ctx.BeginFigure(new Point(cx + rInner, cy), true, true);
        ctx.ArcTo(new Point(cx - rInner, cy), new Size(rInner, rInner), 0, false, SweepDirection.Counterclockwise, true, false);
        ctx.ArcTo(new Point(cx + rInner, cy), new Size(rInner, rInner), 0, false, SweepDirection.Counterclockwise, true, false);
      }
    }
    sg.Freeze();
    return sg;
  }

  /// <summary>Elliptical annulus (foreshortened disk for the 3-D stack view).</summary>
  private static Geometry BuildAnnulusElliptical(double cx, double cy, double rxInner, double ryInner, double rxOuter, double ryOuter) {
    var sg = new StreamGeometry { FillRule = FillRule.EvenOdd };
    using (var ctx = sg.Open()) {
      ctx.BeginFigure(new Point(cx + rxOuter, cy), true, true);
      ctx.ArcTo(new Point(cx - rxOuter, cy), new Size(rxOuter, ryOuter), 0, false, SweepDirection.Clockwise, true, false);
      ctx.ArcTo(new Point(cx + rxOuter, cy), new Size(rxOuter, ryOuter), 0, false, SweepDirection.Clockwise, true, false);
      if (rxInner > 0.5 && ryInner > 0.5) {
        ctx.BeginFigure(new Point(cx + rxInner, cy), true, true);
        ctx.ArcTo(new Point(cx - rxInner, cy), new Size(rxInner, ryInner), 0, false, SweepDirection.Counterclockwise, true, false);
        ctx.ArcTo(new Point(cx + rxInner, cy), new Size(rxInner, ryInner), 0, false, SweepDirection.Counterclockwise, true, false);
      }
    }
    sg.Freeze();
    return sg;
  }

  /// <summary>
  /// Builds the filled-arc-segment geometry (annular wedge) for one
  /// <see cref="Compression.Core.Layout.PlatterWedge"/> on a (potentially
  /// foreshortened) elliptical platter. Angles are CW from 12-o'clock so
  /// the wedge reads the way data spirals on a real disk.
  /// </summary>
  private static Geometry BuildWedgeGeometryElliptical(double cx, double cy, double rxMax, double ryMax, Compression.Core.Layout.PlatterWedge wedge) {
    var rxOuter = rxMax * wedge.OuterRadius;
    var ryOuter = ryMax * wedge.OuterRadius;
    var rxInner = rxMax * wedge.InnerRadius;
    var ryInner = ryMax * wedge.InnerRadius;

    // Full-ring fast path — just a filled annulus at that radial band.
    if (wedge.IsFullRing)
      return BuildAnnulusElliptical(cx, cy, rxInner, ryInner, rxOuter, ryOuter);

    // CW from 12-o'clock: screen X = sin(θ), screen Y = -cos(θ) (Y axis
    // grows downward in screen coords, so a "north" of -cos puts θ=0 at
    // the top). The wedge fills from startAngle through startAngle+sweep.
    var a0 = wedge.StartAngle;
    var a1 = wedge.StartAngle + wedge.SweepAngle;
    var isLargeArc = wedge.SweepAngle > Math.PI;

    var p0Outer = AngleToPoint(cx, cy, rxOuter, ryOuter, a0);
    var p1Outer = AngleToPoint(cx, cy, rxOuter, ryOuter, a1);
    var p0Inner = AngleToPoint(cx, cy, rxInner, ryInner, a0);
    var p1Inner = AngleToPoint(cx, cy, rxInner, ryInner, a1);

    var sg = new StreamGeometry();
    using (var ctx = sg.Open()) {
      // Start at the outer-edge start, sweep CW to outer-edge end, drop
      // down to inner-edge end, sweep back CCW to inner-edge start, close.
      ctx.BeginFigure(p0Outer, true, true);
      ctx.ArcTo(p1Outer, new Size(rxOuter, ryOuter), 0, isLargeArc, SweepDirection.Clockwise, true, false);
      ctx.LineTo(p1Inner, true, false);
      if (rxInner > 0.5 && ryInner > 0.5)
        ctx.ArcTo(p0Inner, new Size(rxInner, ryInner), 0, isLargeArc, SweepDirection.Counterclockwise, true, false);
      else
        ctx.LineTo(p0Inner, true, false);
    }
    sg.Freeze();
    return sg;
  }

  /// <summary>Converts a CW-from-north angle to a screen point on an
  /// elliptical orbit around (cx, cy). 0 = 12-o'clock, π/2 = 3-o'clock.</summary>
  private static Point AngleToPoint(double cx, double cy, double rx, double ry, double angle)
    => new(cx + rx * Math.Sin(angle), cy - ry * Math.Cos(angle));

  // ── Hover / tooltip ────────────────────────────────────────────────

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);

    // Hit-testing assumes the linear tile grid; the projected (platter/cylinder)
    // views map screen position non-linearly, so we don't offer per-tile hover
    // there (the grid's row/col math would point at the wrong block).
    if (ViewMode is Compression.Core.Layout.BlockMapView.CircularPlatter
                 or Compression.Core.Layout.BlockMapView.CylinderStack) {
      SetTooltipContent(null);
      return;
    }

    var kinds = this._tileKinds;
    var classes = this._tileClasses;
    var files = this._tileFiles;
    var lengths = this._tileLengths;
    if (kinds == null || classes == null || files == null || lengths == null) {
      SetTooltipContent(null);
      return;
    }

    var p = e.GetPosition(this);
    var tileW = this._tileW;
    var tileH = this._tileH;
    if (tileW <= 0 || tileH <= 0) {
      SetTooltipContent(null);
      return;
    }

    var col = (int)(p.X / tileW);
    var row = (int)(p.Y / tileH);
    if (col < 0 || col >= this._cols || row < 0 || row >= this._rows) {
      SetTooltipContent(null);
      return;
    }

    var tileIndex = row * this._cols + col;
    if (tileIndex < 0 || tileIndex >= kinds.Length) {
      SetTooltipContent(null);
      return;
    }

    // Resolve the actual block at this tile's byte midpoint. We rely on the
    // cached tile arrays for the dominant kind/file (matches what OnRender
    // colored the tile), so no second walk of the BlockMap is required.
    var kind = kinds[tileIndex];
    var cls = classes[tileIndex];
    var name = files[tileIndex];
    var length = lengths[tileIndex];

    SetTooltipContent(BuildTooltipText(kind, cls, name, length));
  }

  protected override void OnMouseLeave(MouseEventArgs e) {
    base.OnMouseLeave(e);
    SetTooltipContent(null);
  }

  // ── Click-to-drill-down ─────────────────────────────────────────────
  // Single left-click on a tile reverses the cursor → tile coords, derives
  // the [start, end) byte range that tile covers, and walks the current
  // BlockMap once to gather every entry whose own range intersects the
  // tile's range. The collected list is dispatched via the TileClicked
  // routed event for hosts (e.g. DefragmentWindow) to display.

  /// <summary>
  /// Routed event fired on single left-click over a tile. Carries the byte
  /// range covered by the tile and every <see cref="DefragBlockInfo"/> from
  /// the current <see cref="BlockMap"/> whose offset range intersects it.
  /// </summary>
  public static readonly RoutedEvent TileClickedEvent = EventManager.RegisterRoutedEvent(
    nameof(TileClicked), RoutingStrategy.Bubble,
    typeof(EventHandler<TileClickedEventArgs>), typeof(BlockMapControl));

  /// <summary>CLR wrapper for <see cref="TileClickedEvent"/>.</summary>
  public event EventHandler<TileClickedEventArgs> TileClicked {
    add => AddHandler(TileClickedEvent, value);
    remove => RemoveHandler(TileClickedEvent, value);
  }

  protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
    base.OnMouseLeftButtonDown(e);

    var map = BlockMap;
    if (map == null || map.Count == 0) return;
    var bytesPerTile = this._bytesPerTile;
    var tileW = this._tileW;
    var tileH = this._tileH;
    if (bytesPerTile <= 0 || tileW <= 0 || tileH <= 0) return;

    var p = e.GetPosition(this);
    var col = (int)(p.X / tileW);
    var row = (int)(p.Y / tileH);
    if (col < 0 || col >= this._cols || row < 0 || row >= this._rows) return;

    var tileIndex = (long)row * this._cols + col;
    var startOffset = (long)Math.Floor(tileIndex * bytesPerTile);
    var endOffset = (long)Math.Floor((tileIndex + 1) * bytesPerTile);
    var imageSize = ImageSize;
    if (imageSize > 0 && endOffset > imageSize) endOffset = imageSize;
    if (startOffset >= endOffset) return;

    // Resolve the block list for this tile. For large maps (>= threshold)
    // we use the CSR cache built in OnRender → O(k) where k = blocks in
    // this tile. For small maps we fall back to a linear scan, matching
    // the original behaviour and avoiding cache-build cost.
    var offsets = this._tileBlockOffsets;
    var indices = this._tileBlockIndices;
    List<DefragBlockInfo> contents;
    if (offsets != null && indices != null
        && tileIndex >= 0 && tileIndex < offsets.Length - 1) {
      var lo = offsets[(int)tileIndex];
      var hi = offsets[(int)tileIndex + 1];
      contents = new List<DefragBlockInfo>(hi - lo);
      for (var i = lo; i < hi; i++) {
        var bIdx = indices[i];
        if (bIdx >= 0 && bIdx < map.Count) contents.Add(map[bIdx]);
      }
    } else {
      // Small-map path: walk the BlockMap once. Cheap when N is small
      // (< 100K) — the cost is O(N) on click, but click is a discrete
      // user gesture so it's acceptable.
      contents = new List<DefragBlockInfo>();
      foreach (var b in map) {
        var bStart = b.Offset;
        var bEnd = b.Offset + b.Length;
        if (bStart < endOffset && bEnd > startOffset)
          contents.Add(b);
      }
    }

    Focus();
    RaiseEvent(new TileClickedEventArgs(TileClickedEvent, this) {
      StartOffset = startOffset,
      EndOffset = endOffset,
      Contents = contents,
    });
  }

  private void SetTooltipContent(string? text) {
    var tip = ToolTip as ToolTip;
    if (tip == null) {
      ToolTip = tip = new ToolTip();
    }
    if (string.IsNullOrEmpty(text)) {
      tip.IsOpen = false;
      tip.Content = "";
      return;
    }
    if (!Equals(tip.Content, text)) tip.Content = text;
  }

  private static string BuildTooltipText(DefragBlockKind kind, DefragBlockClass? cls, string? name, long length) {
    var sizeStr = FormatBytes(length);
    return kind switch {
      DefragBlockKind.Free => $"Free space — {sizeStr}",
      DefragBlockKind.Bad => $"Bad / quarantined — {sizeStr}",
      DefragBlockKind.MetadataReserved => $"Metadata / reserved — {sizeStr}",
      DefragBlockKind.InProgress => name != null
        ? $"{name} — {sizeStr} — In progress"
        : $"In progress — {sizeStr}",
      DefragBlockKind.Used => name != null
        ? $"{name} — {length:N0} bytes — {cls ?? DefragBlockClass.Normal}"
        : $"Used — {length:N0} bytes — {cls ?? DefragBlockClass.Normal}",
      _ => $"{kind} — {sizeStr}",
    };
  }

  private static string FormatBytes(long bytes) => bytes switch {
    < 1024 => $"{bytes:N0} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  private static void DrawHead(DrawingContext dc, long offset, long imageSize, double w, double h, Pen pen) {
    if (offset < 0 || imageSize <= 0) return;
    // Map the offset to a position on the linearised grid: sweep left-to-right,
    // top-to-bottom through tiles, then convert to (x,y) in pixels. Approximation
    // — we render a vertical bar at the linear x-position regardless of row.
    var fraction = (double)offset / imageSize;
    var x = fraction * w;
    dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
  }

  private static Brush BrushFor(DefragBlockKind kind, DefragBlockClass? cls) => kind switch {
    DefragBlockKind.Free => FreeBrush,
    DefragBlockKind.Bad => BadBrush,
    DefragBlockKind.MetadataReserved => MetaBrush,
    DefragBlockKind.InProgress => InProgressBrush,
    DefragBlockKind.Used => cls switch {
      DefragBlockClass.Hot => HotBrush,
      DefragBlockClass.Cold => ColdBrush,
      DefragBlockClass.Frozen => FrozenBrush,
      DefragBlockClass.Directory => DirectoryBrush,
      _ => NormalBrush,
    },
    _ => FreeBrush,
  };

  /// <summary>
  /// Determines which kind "wins" when multiple block kinds share a single
  /// rendered tile (common for small files on big images). Higher = wins.
  /// Drives the hover tooltip and click-through selection. The actual rendered
  /// color uses byte-weighted blending instead of winner-take-all.
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
  /// Blends the per-kind colors by their byte-count weights. With a tiny
  /// "minimum representation" floor for Used / Bad so a 1% file in a 99%
  /// Free tile still tints visibly toward blue / red. Pure-single-kind tiles
  /// render exactly the same as before; only mixed tiles get the blend.
  /// </summary>
  private static Brush BlendedBrush(DefragBlockKind dominantKind, DefragBlockClass? cls,
      long freeBytes, long usedBytes, long metaBytes, long badBytes) {
    var total = freeBytes + usedBytes + metaBytes + badBytes;
    if (total <= 0) return BrushFor(dominantKind, cls);

    // Tiny-feature visibility floor: ensure Used/Bad contribute at least 15%
    // weight if they're present at all. Without this, a 16-byte file in a
    // 100 KB tile renders as pure Free / pure Metadata — invisible.
    const double MinFeatureWeight = 0.15;
    var fw = (double)freeBytes;
    var uw = (double)usedBytes;
    var mw = (double)metaBytes;
    var bw = (double)badBytes;

    if (uw > 0) uw = Math.Max(uw, total * MinFeatureWeight);
    if (bw > 0) bw = Math.Max(bw, total * MinFeatureWeight);
    var sum = fw + uw + mw + bw;
    if (sum <= 0) return BrushFor(dominantKind, cls);

    var freeC = ((SolidColorBrush)FreeBrush).Color;
    var metaC = ((SolidColorBrush)MetaBrush).Color;
    var badC = ((SolidColorBrush)BadBrush).Color;
    var usedC = ((SolidColorBrush)(cls switch {
      DefragBlockClass.Hot => HotBrush,
      DefragBlockClass.Cold => ColdBrush,
      DefragBlockClass.Frozen => FrozenBrush,
      DefragBlockClass.Directory => DirectoryBrush,
      _ => NormalBrush,
    })).Color;

    var r = (fw * freeC.R + uw * usedC.R + mw * metaC.R + bw * badC.R) / sum;
    var g = (fw * freeC.G + uw * usedC.G + mw * metaC.G + bw * badC.G) / sum;
    var b = (fw * freeC.B + uw * usedC.B + mw * metaC.B + bw * badC.B) / sum;
    return new SolidColorBrush(Color.FromRgb(
      (byte)Math.Clamp((int)r, 0, 255),
      (byte)Math.Clamp((int)g, 0, 255),
      (byte)Math.Clamp((int)b, 0, 255)));
  }
}

/// <summary>
/// Payload for <see cref="BlockMapControl.TileClickedEvent"/>. Reports the
/// byte range covered by the clicked tile plus every block that intersects
/// it. Hosts use this to render a drill-down view (popup or side panel).
/// </summary>
public sealed class TileClickedEventArgs : RoutedEventArgs {
  public TileClickedEventArgs(RoutedEvent routedEvent, object source) : base(routedEvent, source) { }

  /// <summary>First byte offset (inclusive) covered by the clicked tile.</summary>
  public long StartOffset { get; init; }

  /// <summary>One past the last byte offset (exclusive) covered by the clicked tile.</summary>
  public long EndOffset { get; init; }

  /// <summary>Span (in bytes) covered by the tile.</summary>
  public long ByteSpan => Math.Max(0, EndOffset - StartOffset);

  /// <summary>Every <see cref="DefragBlockInfo"/> from the source map whose own range intersects [<see cref="StartOffset"/>, <see cref="EndOffset"/>).</summary>
  public IReadOnlyList<DefragBlockInfo> Contents { get; init; } = Array.Empty<DefragBlockInfo>();
}
