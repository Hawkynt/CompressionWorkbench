using System.Drawing;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Maintenance;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// The client area of the Defragment tab: the block map filling the window, an optional files panel
/// beside it, the colour key under it and one status line with the progress of whatever is running.
/// It only shows what it is given; <see cref="MaintenancePresenter"/> decides what that is.
/// </summary>
internal sealed class DefragmentView : Panel {
  private const int StatusHeight = 22;
  private const int LegendHeight = 40;
  private const int ProgressHeight = 4;
  private const int FilesPanelWidth = 340;
  private const int LogHeight = 120;

  private static readonly Font MonoFont = new("Cascadia Mono", 8f, FontStyle.Regular);

  private readonly Label _status = new() { Text = "—", Dock = DockStyle.Top, Height = StatusHeight };
  private readonly SplitContainer _split = new() { FixedPanel = FixedPanel.Panel2, Panel1MinSize = 160, Panel2MinSize = 200, Dock = DockStyle.Fill };
  private readonly Label _filesStatus = new() { Text = "—", ForeColor = Color.DimGray, Dock = DockStyle.Top, Height = 18 };
  private readonly DataGridView _files = new() { ReadOnly = true, ShowGridLines = true, Dock = DockStyle.Fill };
  private readonly LogView _log = new() { Font = MonoFont, Dock = DockStyle.Bottom, Height = LogHeight };
  private readonly LegendStrip _legend = new() { Dock = DockStyle.Bottom, Height = LegendHeight };
  private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Dock = DockStyle.Bottom, Height = ProgressHeight };
  private TileContentsWindow? _tileWindow;
  private bool _splitSized;

  public DefragmentView() {
    this.BackColor = DefaultTheme.Instance.ControlBackground;
    this.Map.TileClicked += this.OnTileClicked;

    AddColumn("Name", static o => ((FileRow)o!).Name, 150);
    AddColumn("Size", static o => ((FileRow)o!).SizeDisplay, 70, (a, b) => ((FileRow)a!).Size.CompareTo(((FileRow)b!).Size));
    var fragments = AddColumn("Frag", static o => ((FileRow)o!).FragmentsDisplay, 40);
    fragments.Alignment = ContentAlignment.MiddleCenter;
    fragments.TooltipSelector = static _ => "How many separate stretches of the container this file's data occupies. 1 is contiguous; higher is fragmented. An em dash means the format offers no layout to count from.";
    AddColumn("Method", static o => ((FileRow)o!).MethodDisplay, 70);
    AddColumn("Modified", static o => ((FileRow)o!).ModifiedDisplay, 110, (a, b) => Nullable.Compare(((FileRow)a!).Modified, ((FileRow)b!).Modified));
    AddColumn("Class", static o => ((FileRow)o!).Class, 56);

    // Docked children claim their edges in reverse order of addition: the status line takes the
    // top, the progress line the very bottom, the colour key sits above it and the map fills the rest.
    this.Map.Dock = DockStyle.Fill;
    this._split.Panel1.Controls.Add(this.Map);
    this._split.Panel2.Controls.AddRange(this._files, this._log, this._filesStatus);
    this.Controls.AddRange(this._split, this._legend, this._progress, this._status);

    DataGridViewColumn AddColumn(string header, Func<object?, object?> selector, int width, Comparison<object?>? sort = null) {
      var column = new DataGridViewColumn(header, selector) { Width = width };
      if (sort is not null) column.SortComparison = sort;
      this._files.Columns.Add(column);
      return column;
    }
  }

  /// <summary>The block map itself.</summary>
  public BlockMapControl Map { get; } = new();

  /// <summary>The line above the map: how honest the picture is, or what the running operation is doing.</summary>
  public string StatusText {
    get => this._statusText;
    set {
      this._statusText = value;
      this.ShowStatusLine();
    }
  }

  /// <summary>What is being maintained — "disk.img — FAT · 16.0 MB" — shown at the start of the status line.</summary>
  public string TargetText {
    get => this._targetText;
    set {
      this._targetText = value;
      this.ShowStatusLine();
    }
  }

  private string _statusText = "—";
  private string _targetText = "";

  private void ShowStatusLine()
    => this._status.Text = this._targetText.Length == 0 ? this._statusText : $"{this._targetText}  │  {this._statusText}";

  /// <summary>Whether the files panel shows beside the map.</summary>
  public bool FilesPanelVisible {
    get => !this._split.Panel2Collapsed;
    set => this._split.Panel2Collapsed = !value;
  }

  /// <summary>Whether the colour key shows under the map.</summary>
  /// <remarks>
  /// Kept as its own flag: <see cref="Control.Visible"/> reports the effective state, which is false
  /// for as long as the whole view is hidden behind the browser.
  /// </remarks>
  public bool LegendVisible {
    get => this._legendVisible;
    set {
      this._legendVisible = value;
      this._legend.Visible = value;
      this.PerformLayout();
    }
  }

  private bool _legendVisible = true;

  /// <summary>Every line logged since the view was last reset.</summary>
  public string LogText => this._log.LogText;

  public int ProgressValue {
    get => this._progress.Value;
    set => this._progress.Value = Math.Clamp(value, 0, 100);
  }

  /// <summary>Shows an image at rest: its layout, its files and how the layout was obtained.</summary>
  public void ShowSnapshot(BlockMapSnapshot snapshot) {
    this.Map.ReadHead = -1;
    this.Map.WriteHead = -1;
    this.Map.BlockMap = snapshot.Map;
    this.Map.ImageSize = snapshot.ImageSize;
    this._files.DataSource = snapshot.Rows;
    this._filesStatus.Text = snapshot.Rows.Count switch {
      0 => "—",
      1 => "1 file",
      var n => $"{n:N0} files",
    };
    this.StatusText = snapshot.Status;
  }

  /// <summary>Moves the picture along with a running operation.</summary>
  public void Apply(MaintenanceProgress progress) {
    if (progress.Map is not null) {
      this.Map.BlockMap = progress.Map;
      this.Map.ImageSize = progress.ImageSize;
    } else if (progress.ImageSize > 0 && this.Map.ImageSize <= 0) {
      this.Map.ImageSize = progress.ImageSize;
    }

    this.Map.ReadHead = progress.ReadHead;
    this.Map.WriteHead = progress.WriteHead;
    if (progress.Fraction >= 0) this.ProgressValue = (int)(Math.Clamp(progress.Fraction, 0, 1) * 100);
    if (!string.IsNullOrWhiteSpace(progress.Status)) this.StatusText = progress.Status!;
  }

  public void Log(string line) {
    this._log.Append(line);
  }

  /// <summary>Forgets the previous image: no map, no rows, no log.</summary>
  public void Clear() {
    this.ShowSnapshot(BlockMapSnapshot.Empty("—"));
    this._log.Clear();
    this.ProgressValue = 0;
  }

  /// <summary>
  /// Opens the files panel at a fixed width, once, when the view first has a real size; from then on
  /// the map takes whatever the window gains, and a width the user dragged to is theirs.
  /// </summary>
  public void SizeFilesPanelOnce() {
    if (this._splitSized || this.Width <= FilesPanelWidth + this._split.Panel1MinSize) return;
    this._splitSized = true;
    this._split.SplitterDistance = this.Width - FilesPanelWidth - this._split.SplitterWidth;
  }

  /// <summary>
  /// Opens a modeless drill-down listing every block under the clicked tile. Modeless so the user can
  /// keep clicking tiles to compare; each click re-targets the same window.
  /// </summary>
  private void OnTileClicked(object? sender, TileClickedEventArgs e) {
    if (this._tileWindow is null) {
      this._tileWindow = new TileContentsWindow();
      this._tileWindow.FormClosed += (_, _) => this._tileWindow = null;
    }

    this._tileWindow.SetContents(e.StartOffset, e.EndOffset, e.Contents);
    this._tileWindow.Show();
  }

  /// <summary>The block-map colour key.</summary>
  private sealed class LegendStrip : OwnerDrawnControl {
    private static readonly (Color Color, string Label)[] Swatches = [
      (Color.FromArgb(0xE6, 0x4A, 0x19), "Hot"),
      (Color.FromArgb(0x42, 0xA5, 0xF5), "Normal"),
      (Color.FromArgb(0x66, 0xBB, 0x6A), "Cold"),
      (Color.FromArgb(0x90, 0xA4, 0xAE), "Frozen"),
      (Color.FromArgb(0xDA, 0xA5, 0x20), "Directory"),
      (Color.FromArgb(0xE0, 0xE0, 0xE0), "Free"),
      (Color.FromArgb(0xC0, 0x30, 0x30), "Bad"),
      (Color.FromArgb(0x70, 0x70, 0x70), "Reserved"),
    ];

    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      var theme = DefaultTheme.Instance;
      var font = theme.DefaultFont;
      g.FillRectangle(theme.ControlBackground, new(0, 0, this.Width, this.Height));

      // Rows and labels are measured, so the key stays whole at any display scaling.
      var row = Math.Max(16, g.MeasureText("Ag", font).Height);
      var x = 0;
      var y = 2;
      foreach (var (color, label) in Swatches) {
        var text = g.MeasureText(label, font).Width;
        if (!this.Fit(ref x, ref y, 14 + text + 12, row)) return;
        g.FillRectangle(color, new(x, y + (row - 10) / 2, 10, 10));
        g.DrawText(label, font, theme.ControlText, new(x + 14, y, text + 4, row), ContentAlignment.MiddleLeft);
        x += 14 + text + 12;
      }

      // The two heads are lines rather than swatches, matching how they are drawn on the map.
      foreach (var (color, label) in new[] { (Color.LimeGreen, "Read head"), (Color.Orange, "Write head") }) {
        var text = g.MeasureText(label, font).Width;
        if (!this.Fit(ref x, ref y, 8 + text + 12, row)) return;
        g.DrawLine(color, x + 2, y + 2, x + 2, y + row - 2, 2);
        g.DrawText(label, font, theme.ControlText, new(x + 8, y, text + 4, row), ContentAlignment.MiddleLeft);
        x += 8 + text + 12;
      }
    }

    /// <summary>Wraps to the next row when <paramref name="width"/> does not fit; false once out of rows.</summary>
    private bool Fit(ref int x, ref int y, int width, int row) {
      if (x > 0 && x + width > this.Width) {
        x = 0;
        y += row + 2;
      }

      return y + row <= this.Height;
    }
  }
}
