using System.Drawing;
using Compression.Analysis.Visualization;
using Compression.NativeUI.Views;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// A 16x16 map of a file, one tile per region, coloured by what the bytes look like. Clicking a
/// large tile drills into it; a small one opens its bytes in the preview window.
/// </summary>
internal sealed class HeatmapGridControl : Panel {
  private const int LegendWidth = 110;
  private const int ToolbarHeight = 30;
  private const int InfoHeight = 40;
  private const int SmallTileBytes = 4096;

  private static readonly (TileClass Class, Color Color, string Label)[] Legend = [
    (TileClass.Zeros, Color.FromArgb(0x27, 0x40, 0x8B), "Zeros"),
    (TileClass.KnownFormat, Color.FromArgb(0x8B, 0x5C, 0xF6), "Format"),
    (TileClass.VeryLowEntropy, Color.FromArgb(0x41, 0x69, 0xE1), "Very Low"),
    (TileClass.LowEntropy, Color.FromArgb(0x20, 0xB2, 0xAA), "Low"),
    (TileClass.MediumEntropy, Color.FromArgb(0x22, 0x8B, 0x22), "Medium"),
    (TileClass.HighEntropy, Color.FromArgb(0xDA, 0xA5, 0x20), "High"),
    (TileClass.Compressed, Color.FromArgb(0xCD, 0x66, 0x00), "Compressed"),
    (TileClass.Encrypted, Color.FromArgb(0xCD, 0x26, 0x26), "Encrypted"),
    (TileClass.Empty, Color.FromArgb(0xAA, 0xAA, 0xAA), "Empty"),
  ];

  private static readonly Font InfoFont = new("Cascadia Mono", 8f, FontStyle.Regular);
  private static readonly Font TileFont = new("Cascadia Mono", 6.5f, FontStyle.Bold);

  private readonly ToolStrip _toolbar = new();
  private readonly ToolStripButton _back = new("Back");
  private readonly ToolStripButton _root = new("Root");
  private readonly ToolStripStatusLabel _breadcrumb = new();
  private readonly ToolStripButton _extract = new("Extract Region...");

  private readonly TileMap _tiles = new();
  private readonly LegendPanel _legend = new();
  private readonly Label _info = new() { Font = InfoFont };

  private readonly Stack<(long Offset, long Length)> _navStack = new();
  private Stream? _stream;
  private long _currentOffset;
  private long _currentLength;
  private HeatmapTile[]? _currentTiles;

  public HeatmapGridControl() {
    this._back.Click += (_, _) => this.GoBack();
    this._root.Click += (_, _) => this.GoRoot();
    this._extract.Click += (_, _) => this.ExtractRegion();
    this._toolbar.Items.AddRange([
      this._back, this._root, new ToolStripSeparator(), this._breadcrumb,
      new ToolStripSeparator(), this._extract,
    ]);

    this._tiles.TileClicked += this.OnTileClicked;
    this._tiles.TileHovered += this.UpdateInfo;

    this.Controls.AddRange(this._toolbar, this._tiles, this._legend, this._info);

    this._toolbar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
    this._tiles.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
    this._legend.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
    this._info.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

    this.LayoutChildren();
  }

  /// <summary>True when a drill-down can be undone.</summary>
  public bool CanGoBack => this._navStack.Count > 0;

  private void LayoutChildren() {
    var w = Math.Max(200, this.Width);
    var h = Math.Max(200, this.Height);

    this._toolbar.Bounds = new(0, 0, w, ToolbarHeight);
    this._legend.Bounds = new(w - LegendWidth, ToolbarHeight, LegendWidth, h - ToolbarHeight - InfoHeight);
    this._tiles.Bounds = new(2, ToolbarHeight + 2, w - LegendWidth - 6, h - ToolbarHeight - InfoHeight - 4);
    this._info.Bounds = new(4, h - InfoHeight + 2, w - 8, InfoHeight - 4);
  }

  /// <summary>Opens a file and shows its top-level map.</summary>
  public void OpenFile(string path) {
    this._stream?.Dispose();
    this._stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.RandomAccess);
    this._navStack.Clear();
    this.NavigateTo(0, this._stream.Length);
    this.UpdateBreadcrumb(path);
  }

  /// <summary>Opens an already-open stream, e.g. extracted partition data.</summary>
  public void OpenStream(Stream stream, string label) {
    this._stream = stream;
    this._navStack.Clear();
    this.NavigateTo(0, stream.Length);
    this.UpdateBreadcrumb(label);
  }

  private void NavigateTo(long offset, long length) {
    if (this._stream is null) return;

    this._currentOffset = offset;
    this._currentLength = length;
    this._currentTiles = HeatmapComputer.ComputeGrid(this._stream, offset, length);
    this._tiles.SetTiles(this._currentTiles);
    this.UpdateInfo(null);
  }

  private void OnTileClicked(HeatmapTile tile) {
    if (tile.Length <= 0) return;

    // A tile small enough to read whole opens directly rather than drilling further.
    if (tile.Length < SmallTileBytes) {
      this.ShowTileDetail(tile);
      return;
    }

    this._navStack.Push((this._currentOffset, this._currentLength));
    this.NavigateTo(tile.Offset, tile.Length);
    this.UpdateBreadcrumb(null);
  }

  private void UpdateInfo(HeatmapTile? tile) {
    if (tile is null) {
      this._info.Text = $"Region: 0x{this._currentOffset:X} - 0x{this._currentOffset + this._currentLength:X} "
                      + $"({FormatSize(this._currentLength)}) | {this._navStack.Count} levels deep";
      return;
    }

    var parts = new List<string> {
      $"Offset: 0x{tile.Offset:X}",
      $"Size: {FormatSize(tile.Length)}",
      $"Entropy: {tile.Entropy:F2}",
      $"Zeros: {tile.ZeroFraction:P0}",
      $"ASCII: {tile.AsciiFraction:P0}",
      $"Class: {tile.Classification}",
    };
    if (tile.DetectedFormat is not null) parts.Add($"Format: {tile.DetectedFormat}");

    this._info.Text = string.Join(" | ", parts);
  }

  private void ShowTileDetail(HeatmapTile tile) {
    if (this._stream is null) return;

    var readLength = (int)Math.Min(tile.Length, 64 * 1024);
    var data = HeatmapComputer.ReadRegion(this._stream, tile.Offset, readLength);

    var title = $"0x{tile.Offset:X} — {FormatSize(tile.Length)}, entropy {tile.Entropy:F2}";
    if (tile.DetectedFormat is not null) title += $" [{tile.DetectedFormat}]";

    var preview = new PreviewWindow();
    preview.ShowData(title, data, hex: tile.Entropy > 5.0 || tile.AsciiFraction < 0.5, analyzeMode: true);
    preview.Show();
  }

  private void UpdateBreadcrumb(string? rootLabel) {
    var parts = new List<string>();
    if (rootLabel is not null) parts.Add(Path.GetFileName(rootLabel));
    if (this._navStack.Count > 0) parts.Add($"depth {this._navStack.Count}");
    parts.Add($"0x{this._currentOffset:X}-0x{this._currentOffset + this._currentLength:X} ({FormatSize(this._currentLength)})");

    this._breadcrumb.Text = string.Join(" > ", parts);
  }

  private void GoBack() {
    if (this._navStack.Count == 0) return;
    var (offset, length) = this._navStack.Pop();
    this.NavigateTo(offset, length);
    this.UpdateBreadcrumb(null);
  }

  private void GoRoot() {
    if (this._stream is null || this._navStack.Count == 0) return;
    this._navStack.Clear();
    this.NavigateTo(0, this._stream.Length);
    this.UpdateBreadcrumb(null);
  }

  private void ExtractRegion() {
    if (this._stream is null) return;

    var dialog = new SaveFileDialog {
      FileName = $"extract_0x{this._currentOffset:X}_{this._currentLength}.bin",
      Filter = "Binary files|*.bin|All files|*.*",
    };
    if (dialog.ShowDialog() != DialogResult.OK) return;

    HeatmapComputer.ExtractRegion(this._stream, this._currentOffset, this._currentLength, dialog.FileName);
    MessageBox.Show($"Extracted {FormatSize(this._currentLength)} to {dialog.FileName}",
      "Extract", MessageBoxButtons.OK, MessageBoxIcon.Information);
  }

  internal static Color TileColor(TileClass classification) {
    foreach (var (cls, color, _) in Legend)
      if (cls == classification) return color;
    return Color.FromArgb(0xAA, 0xAA, 0xAA);
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  /// <summary>The colour key beside the grid.</summary>
  private sealed class LegendPanel : OwnerDrawnControl {
    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      var theme = DefaultTheme.Instance;
      g.FillRectangle(theme.ControlBackground, new(0, 0, this.Width, this.Height));
      g.DrawLine(theme.Border, 0, 0, 0, this.Height, 1);

      var font = theme.DefaultFont;
      g.DrawText("Legend", new(font.Family, font.SizeInPoints, FontStyle.Bold), theme.ControlText,
        new(8, 6, this.Width - 12, 18), ContentAlignment.MiddleLeft);

      var y = 28;
      foreach (var (_, color, label) in Legend) {
        g.FillRectangle(color, new(8, y + 2, 12, 12));
        g.DrawText(label, font, theme.ControlText, new(26, y, this.Width - 30, 16), ContentAlignment.MiddleLeft);
        y += 18;
      }
    }
  }

  /// <summary>The 16x16 grid itself, drawn as one control rather than 256 child controls.</summary>
  private sealed class TileMap : OwnerDrawnControl {
    private HeatmapTile[] _tiles = [];
    private int _hoverIndex = -1;

    public event Action<HeatmapTile>? TileClicked;
    public event Action<HeatmapTile?>? TileHovered;

    public void SetTiles(HeatmapTile[] tiles) {
      this._tiles = tiles;
      this._hoverIndex = -1;
      this.Invalidate();
    }

    private int TileWidth => Math.Max(1, this.Width / HeatmapComputer.GridSize);
    private int TileHeight => Math.Max(1, this.Height / HeatmapComputer.GridSize);

    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.FillRectangle(DefaultTheme.Instance.ControlBackground, new(0, 0, this.Width, this.Height));
      if (this._tiles.Length == 0) return;

      var tw = this.TileWidth;
      var th = this.TileHeight;

      for (var i = 0; i < this._tiles.Length && i < HeatmapComputer.GridSize * HeatmapComputer.GridSize; ++i) {
        var row = i / HeatmapComputer.GridSize;
        var col = i % HeatmapComputer.GridSize;
        var bounds = new Rectangle(col * tw, row * th, tw, th);

        g.FillRectangle(TileColor(this._tiles[i].Classification), bounds);

        // The hovered tile gets a heavier white outline, as the WPF trigger did.
        var hovered = i == this._hoverIndex;
        g.DrawRectangle(hovered ? Color.White : Color.FromArgb(0x99, 0x99, 0x99), bounds, hovered ? 2 : 1);

        if (this._tiles[i].DetectedFormat is not { } format) continue;
        g.DrawText(format[..Math.Min(4, format.Length)], TileFont, Color.White, bounds, ContentAlignment.MiddleCenter);
      }
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      var index = this.IndexAt(e.X, e.Y);
      if (index == this._hoverIndex) return;

      this._hoverIndex = index;
      this.TileHovered?.Invoke(index >= 0 && index < this._tiles.Length ? this._tiles[index] : null);
      this.Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      this._hoverIndex = -1;
      this.TileHovered?.Invoke(null);
      this.Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      var index = this.IndexAt(e.X, e.Y);
      if (index >= 0 && index < this._tiles.Length) this.TileClicked?.Invoke(this._tiles[index]);
    }

    private int IndexAt(int x, int y) {
      var col = x / this.TileWidth;
      var row = y / this.TileHeight;
      if (col < 0 || col >= HeatmapComputer.GridSize || row < 0 || row >= HeatmapComputer.GridSize) return -1;
      return row * HeatmapComputer.GridSize + col;
    }
  }
}
