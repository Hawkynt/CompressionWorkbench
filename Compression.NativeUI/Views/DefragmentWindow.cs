using System.Diagnostics;
using System.Drawing;
using Compression.Core.Layout;
using Compression.Lib;
using Compression.Lib.Layout;
using Compression.NativeUI.Controls;
using Compression.Registry;
using Compression.Registry.Layout;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// The five canonical maintenance verbs from <c>docs/ARCHIVE-MODEL.md</c>, plus the two composites.
/// Used to pre-focus the matching action when the window is opened from a context-menu entry.
/// </summary>
internal enum MaintenanceVerb {
  /// <summary>Find and apply the best layout, or re-encode the payload; size preserved where possible.</summary>
  Optimize,

  /// <summary>Keep the parameter set; minimise the stored footprint.</summary>
  Shrink,

  /// <summary>Re-order entries and extents so files are contiguous; size preserved.</summary>
  Defragment,

  /// <summary>Erase all live data, leaving a valid empty container.</summary>
  Purge,

  /// <summary>Overwrite only unused space — free clusters, slack, deleted entries; size preserved.</summary>
  WipeEmpty,

  /// <summary>Defragment, optimize and shrink in one pass: the smallest valid container.</summary>
  Compact,

  /// <summary>
  /// Scatter every allocation block across the volume — fragmentation on purpose, so
  /// <see cref="Defragment"/> has something real to undo. Content is preserved; only its place changes.
  /// </summary>
  Scramble,
}

/// <summary>
/// One row of the file-list panel: name, size, fragment count, modification date and thermal class.
/// The fragment count comes from whatever layout the format offered; formats that offer none show an
/// em dash rather than a figure nothing measured.
/// </summary>
internal sealed class FileRow {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public string SizeDisplay { get; init; } = "";
  public string FragmentsDisplay { get; init; } = "";
  public string MethodDisplay { get; init; } = "";
  public DateTime? Modified { get; init; }
  public string ModifiedDisplay { get; init; } = "";
  public string Class { get; init; } = "";
}

/// <summary>
/// The single surface behind the explorer's maintenance menu: shows the detected format and what it
/// supports, lets the user pick a layout strategy, and runs the descriptor's optimize, shrink,
/// defragment, purge, wipe, compact and scramble paths through the matching capability interface.
/// </summary>
internal sealed class DefragmentWindow : Form {
  /// <summary>Cap on extents fed to the block map; adjacent same-kind regions merge beyond it.</summary>
  private const int MaxExtents = 50_000;

  private static readonly Color HeadingColor = Color.FromArgb(0x44, 0x66, 0xAA);
  private static readonly Font MonoFont = new("Cascadia Mono", 8f, FontStyle.Regular);

  // Image picker.
  private readonly GroupBox _imageBox = new() { Text = "Image" };
  private readonly TextBox _imagePathBox = new() { ReadOnly = true, Text = "Select a filesystem image to defragment...", ForeColor = Color.Gray };
  private readonly Button _browse = new() { Text = "Browse..." };

  // Format and capability summary.
  private readonly Panel _infoPanel = new() { BorderStyle = BorderStyle.FixedSingle };
  private readonly Label _formatLabel = new() { Text = "—" };
  private readonly Label _supportLabel = new() { Text = "—" };
  private readonly Label _sizeLabel = new() { Text = "—" };

  // Filesystem layout strategy.
  private readonly GroupBox _fsModesGroup = new() { Text = "Layout strategy" };
  private readonly RadioButton _modePackStart = new() { Text = "Pack at start (default)", Checked = true };
  private readonly RadioButton _modePackEnd = new() { Text = "Pack at end" };
  private readonly RadioButton _modeFillHoles = new() { Text = "Fill holes (lazy)" };
  private readonly RadioButton _modeAscending = new() { Text = "Ascending order only" };
  private readonly RadioButton _modeCarveHole = new() { Text = "Carve a hole" };
  private readonly TextBox _interleaveStride = new() { Text = "1" };
  private readonly ComboBox _metadataZone = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _layoutProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly Button _editProfiles = new() { Text = "Edit profiles…" };

  // Archive repack.
  private readonly GroupBox _archiveRepackGroup = new() { Text = "Archive optimization", Visible = false };
  private readonly Label _archiveRepackNote = new() { ForeColor = Color.DimGray };
  private readonly CheckBox _smartSolidRepack = new() { Text = "Smart solid-block repack (7z only)", Visible = false };
  private readonly ComboBox _metadataPlacement = new() { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
  private readonly Label _metadataPlacementCaption = new() { Text = "Metadata placement:", Visible = false };

  // Carve-hole options.
  private readonly GroupBox _carveOptsGroup = new() { Text = "Carve-hole options", Visible = false };
  private readonly TextBox _holeSize = new() { Text = "64m" };
  private readonly TextBox _holeAt = new() { Text = "auto" };

  // Block map and file panel.
  private readonly SplitContainer _split = new();
  private readonly GroupBox _blockMapGroup = new() { Text = "Block map" };
  private readonly Label _layoutStatus = new() { Text = "—", ForeColor = Color.DimGray };
  private readonly ComboBox _blockMapView = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly BlockMapControl _blockMap = new();
  private readonly LegendStrip _legend = new();
  private readonly CheckBox _filesPanelToggle = new() { Text = "Files panel", Checked = true };
  private readonly GroupBox _filesPanelGroup = new() { Text = "Files" };
  private readonly Label _filesPanelStatus = new() { Text = "—", ForeColor = Color.DimGray };
  private readonly DataGridView _filesGrid = new() { ReadOnly = true, ShowGridLines = true };

  // Output and progress.
  private readonly GroupBox _outputGroup = new() { Text = "Output" };
  private readonly TextBox _outputBox = new() { ReadOnly = true, Multiline = true, Font = MonoFont, BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA) };
  private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };

  // Actions.
  private readonly TextBox _scrambleSeed = new() { Text = "1", Enabled = false };
  private readonly Button _scramble = new() { Text = "Scramble", Enabled = false };
  private readonly Button _run = new() { Text = "Defragment", Enabled = false };
  private readonly Button _cancel = new() { Text = "Cancel", Enabled = false };
  private readonly Button _close = new() { Text = "Close" };
  private readonly CheckBox _minimalGeometry = new() { Text = "Minimal geometry", Enabled = false };
  private readonly Button _compact = new() { Text = "Compact", Enabled = false };
  private readonly Button _purge = new() { Text = "Purge", Enabled = false };
  private readonly Button _wipeEmpty = new() { Text = "Wipe Empty", Enabled = false };
  private readonly Button _shrink = new() { Text = "Shrink", Enabled = false };
  private readonly ToolTip _toolTips = new();

  private readonly List<LayoutProfileEntry?> _layoutProfileEntries = [];

  private MaintenanceVerb? _requestedVerb;
  private string? _imagePath;
  private IArchiveDefragmentable? _defragmentable;
  private IArchiveFormatOperations? _archiveOps;
  private string? _formatId;
  private bool _isArchiveMode;
  private bool _isFileInternalMode;
  private bool _isSevenZipFormat;
  private IFileInternalChunkMover? _chunkMover;
  private int _fileRowCount;
  private string _filesSortDescription = "listing order";
  private LayoutTemplate? _selectedLayoutProfile;
  private TileContentsWindow? _tileContentsWindow;

  private CancellationTokenSource? _maintenanceCancellation;
  private bool _maintenanceIsStaged;
  private bool _maintenanceCommitStarted;
  private string? _maintenanceOperationName;

  public DefragmentWindow() {
    this.Text = "Maintenance — Optimize / Shrink / Defragment / Purge / Wipe";
    this.ClientSize = new(900, 780);
    this.MinimumSize = new(720, 560);
    this.StartPosition = FormStartPosition.CenterParent;

    this.BuildImageBox();
    this.BuildInfoPanel();
    this.BuildFsModes();
    this.BuildArchiveRepack();
    this.BuildCarveOptions();
    this.BuildBlockMapArea();
    this.BuildOutput();
    this.BuildActions();

    this.RefreshLayoutProfilesCombo();
    this.FormClosing += this.OnFormClosing;
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
  }

  public DefragmentWindow(string preselectedImage) : this() {
    if (!string.IsNullOrEmpty(preselectedImage) && File.Exists(preselectedImage))
      this.LoadImage(preselectedImage);
  }

  /// <summary>
  /// Opens the window pre-targeted at <paramref name="verb"/>: the matching action is highlighted
  /// and focused so it runs on Enter. It deliberately does not auto-run — maintenance mutates the
  /// image in place, so the user confirms by clicking.
  /// </summary>
  public DefragmentWindow(string preselectedImage, MaintenanceVerb verb) : this(preselectedImage) {
    this._requestedVerb = verb;
    this.ApplyRequestedVerb();
    this.CorrectArchiveOptimizeAmbiguity();
  }

  /// <summary>
  /// Path of the image last successfully mutated by this window. Stays null while the window only
  /// previews.
  /// </summary>
  public string? MutatedImagePath { get; private set; }

  /// <summary>
  /// Raised once an operation has modified the image on disk, carrying the path that changed. Hosts
  /// compare it against their loaded archive and reload if they match.
  /// </summary>
  public event Action<string>? ArchiveMutated;

  private void NotifyMutated(string path) {
    this.MutatedImagePath = path;
    this.ArchiveMutated?.Invoke(path);
  }

  // ── Construction ────────────────────────────────────────────────────────────────────────────

  private void BuildImageBox() {
    this._browse.Click += (_, _) => {
      var dialog = new OpenFileDialog {
        Title = "Select filesystem image or archive",
        Filter = "Supported files|*.img;*.iso;*.d64;*.d71;*.d81;*.adf;*.dsk;*.po;*.atr;*.ssd;*.dsd;*.zip;*.7z;*.tar;*.lzh;*.arj"
               + "|Filesystem images|*.img;*.iso;*.d64;*.d71;*.d81;*.adf;*.dsk;*.po;*.atr;*.ssd;*.dsd"
               + "|Archives|*.zip;*.7z;*.tar;*.lzh;*.arj|All Files|*.*",
      };
      if (dialog.ShowDialog() == DialogResult.OK) this.LoadImage(dialog.FileName);
    };

    this._imageBox.Controls.AddRange(this._imagePathBox, this._browse);
    this.Controls.Add(this._imageBox);
  }

  private void BuildInfoPanel() {
    this._infoPanel.Controls.AddRange(
      Caption("Format:", 0), this._formatLabel,
      Caption("Defrag support:", 22), this._supportLabel,
      Caption("Image size:", 44), this._sizeLabel);
    this.Controls.Add(this._infoPanel);

    static Label Caption(string text, int y) => new() {
      Bounds = new(8, y + 4, 120, 20),
      Text = text,
      Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold),
    };
  }

  private void BuildFsModes() {
    var modes = new (RadioButton Button, string Detail)[] {
      (this._modePackStart, "Move every file to the front of the image. Free space ends up after the last file. Closest match to a traditional defrag tool."),
      (this._modePackEnd, "Move every file to the back of the image. Free space stays at the front. Useful before injecting a bootloader or installer payload at low offsets."),
      (this._modeFillHoles, "Best-fit fill of existing free regions from tail extents. Doesn't guarantee contiguity but moves the minimum number of bytes. Use on huge images with a few small holes."),
      (this._modeAscending, "Move only what reads backwards, so every file's blocks end up in ascending order and a sequential read never seeks back. A file left in three ascending pieces stays in three pieces — but this moves about a third of the bytes packing does, because it touches only what is out of order."),
      (this._modeCarveHole, "Reserve a contiguous free region of the chosen size. Live data in the way is relocated to other free space, or appended."),
    };

    foreach (var (button, detail) in modes) {
      this._toolTips.SetToolTip(button, detail);
      button.CheckedChanged += (_, _) => this.OnModeChanged();
      this._fsModesGroup.Controls.Add(button);
    }

    this._toolTips.SetToolTip(this._interleaveStride,
      "Block interleave factor. 1 = contiguous (default), 2 = every-other-block, N = place each file's Kth block at (start + K*stride). Range 1-256.");

    this._metadataZone.Items.AddRange([
      "Don't move metadata",
      "Metadata at front (fast access)",
      "Metadata at back (data-first)",
      "Metadata in middle (min seek)",
      "Before file content (read-ahead)",
    ]);
    this._metadataZone.SelectedIndex = 0;
    this._toolTips.SetToolTip(this._metadataZone,
      "Controls where filesystem metadata and directory entries are placed during defragmentation.");

    this._layoutProfile.SelectedIndexChanged += (_, _) => this.OnLayoutProfileChanged();
    this._toolTips.SetToolTip(this._layoutProfile,
      "Optional zone-based layout template. When set, files are placed into named byte ranges with per-zone sort orders. Overrides the layout strategy and metadata placement above.");

    this._editProfiles.Click += (_, _) => {
      new LayoutProfileEditor().ShowDialog(this);
      this.RefreshLayoutProfilesCombo();
    };
    this._toolTips.SetToolTip(this._editProfiles, "Open the layout profile editor to create, modify, or delete profiles.");

    this._fsModesGroup.Controls.AddRange(
      new Label { Text = "Block interleave:" }, this._interleaveStride,
      new Label { Text = "1 = contiguous, 2 = every-other-block, up to 256", ForeColor = Color.DimGray },
      new Label { Text = "Metadata placement:" }, this._metadataZone,
      new Label { Text = "Layout profile:" }, this._layoutProfile, this._editProfiles);

    this.Controls.Add(this._fsModesGroup);
  }

  private void BuildArchiveRepack() {
    this._archiveRepackNote.Text =
      "Archive repack: entries will be extracted and re-created with optimal compression settings. "
      + "The layout strategy modes above do not apply to archive formats.";
    this._toolTips.SetToolTip(this._smartSolidRepack,
      "Tries 5 different file grouping strategies and picks the one that produces the smallest archive. More thorough but slower than a plain repack.");

    this._metadataPlacement.Items.AddRange(["Format default", "Metadata first", "Data first"]);
    this._metadataPlacement.SelectedIndex = 0;

    this._archiveRepackGroup.Controls.AddRange(
      this._archiveRepackNote, this._smartSolidRepack, this._metadataPlacementCaption, this._metadataPlacement);
    this.Controls.Add(this._archiveRepackGroup);
  }

  private void BuildCarveOptions() {
    this._toolTips.SetToolTip(this._holeSize, "Size in bytes. Suffixes: k=KiB, m=MiB, g=GiB. Examples: 64m, 1g, 524288.");
    this._toolTips.SetToolTip(this._holeAt, "Byte offset where the hole should start. 'auto' = at the end after the last live extent.");

    this._carveOptsGroup.Controls.AddRange(
      new Label { Text = "Hole size:" }, this._holeSize,
      new Label { Text = "(64m / 1g / 524288)", ForeColor = Color.DimGray },
      new Label { Text = "Hole offset:" }, this._holeAt,
      new Label { Text = "(auto / decimal byte offset)", ForeColor = Color.DimGray });

    this.Controls.Add(this._carveOptsGroup);
  }

  private void BuildBlockMapArea() {
    this._blockMapView.Items.AddRange(["Linear blocks", "Circular platter (2-D)", "Cylinder stack (3-D)"]);
    this._blockMapView.SelectedIndex = 0;
    this._blockMapView.SelectedIndexChanged += (_, _) => this._blockMap.ViewMode = this._blockMapView.SelectedIndex switch {
      1 => BlockMapView.CircularPlatter,
      2 => BlockMapView.CylinderStack,
      _ => BlockMapView.LinearBlocks,
    };

    this._blockMap.TileClicked += this.OnBlockMapTileClicked;
    this._toolTips.SetToolTip(this._filesPanelToggle, "Show or hide the file list side panel.");
    this._filesPanelToggle.CheckedChanged += (_, _) => {
      this._split.Panel2Collapsed = !this._filesPanelToggle.Checked;
      this.LayoutChildren();
    };

    this._blockMapGroup.Controls.AddRange(
      this._layoutStatus, new Label { Text = "View:" }, this._blockMapView,
      this._blockMap, this._legend, this._filesPanelToggle);

    AddColumn(this._filesGrid, "Name", static o => ((FileRow)o!).Name, 160);
    AddColumn(this._filesGrid, "Size", static o => ((FileRow)o!).SizeDisplay, 80, (a, b) => ((FileRow)a!).Size.CompareTo(((FileRow)b!).Size));
    var fragments = AddColumn(this._filesGrid, "Frag", static o => ((FileRow)o!).FragmentsDisplay, 44);
    fragments.Alignment = ContentAlignment.MiddleCenter;
    fragments.TooltipSelector = static _ => "How many separate stretches of the container this file's data occupies. 1 is contiguous; higher is fragmented. An em dash means the format offers no layout to count from.";
    AddColumn(this._filesGrid, "Method", static o => ((FileRow)o!).MethodDisplay, 80);
    AddColumn(this._filesGrid, "Modified", static o => ((FileRow)o!).ModifiedDisplay, 120, (a, b) => Nullable.Compare(((FileRow)a!).Modified, ((FileRow)b!).Modified));
    AddColumn(this._filesGrid, "Class", static o => ((FileRow)o!).Class, 64);

    // The grid sorts itself; this only observes the result so the status line can report it.
    this._filesGrid.CellClick += (_, _) => this.TrackSort();

    this._filesPanelGroup.Controls.AddRange(this._filesPanelStatus, this._filesGrid);

    this._split.Panel1.Controls.Add(this._blockMapGroup);
    this._split.Panel2.Controls.Add(this._filesPanelGroup);
    this._blockMapGroup.Dock = DockStyle.Fill;
    this._filesPanelGroup.Dock = DockStyle.Fill;
    this._split.Panel2MinSize = 220;
    this.Controls.Add(this._split);
  }

  private static DataGridViewColumn AddColumn(DataGridView grid, string header, Func<object?, object?> selector, int width, Comparison<object?>? sort = null) {
    var column = new DataGridViewColumn(header, selector) { Width = width };
    if (sort is not null) column.SortComparison = sort;
    grid.Columns.Add(column);
    return column;
  }

  private void BuildOutput() {
    this._outputGroup.Controls.Add(this._outputBox);
    this.Controls.AddRange(this._outputGroup, this._progress);
  }

  private void BuildActions() {
    this._toolTips.SetToolTip(this._scrambleSeed, "Seeds Scramble's shuffle. The same seed deals the same layout every run.");
    this._toolTips.SetToolTip(this._scramble, "Scatter every block of every file across the volume — fragmentation on purpose, so Defragment has something real to work against. Content is preserved; only the layout changes. Asks first.");
    this._toolTips.SetToolTip(this._cancel, "Cancel the active maintenance pass. Staged rebuilds are discarded and leave the original unchanged; native in-place moves cancel at a safe boundary when supported.");
    this._toolTips.SetToolTip(this._minimalGeometry, "Compact rebuilds at the smallest geometry the format allows (e.g. a 1.44 MB FAT floppy → a few KB). Contents preserved, but the result may not be a standard mountable image.");
    this._toolTips.SetToolTip(this._compact, "Defragment + optimize + shrink in one pass — the smallest valid container that still holds the same contents. Tick 'Minimal geometry' for the bare-minimum (non-standard) rebuild.");
    this._toolTips.SetToolTip(this._purge, "Erase ALL live data, leaving a valid empty container. Distinct from Wipe Empty (which only zeros dead space).");
    this._toolTips.SetToolTip(this._wipeEmpty, "Zero-fill all unused space: free clusters, cluster-tip slack, dead bytes. Forensic cleanliness tool.");
    this._toolTips.SetToolTip(this._shrink, "Defragment + truncate trailing free space to minimize image size.");

    this._run.Click += (_, _) => this.OnRunWithBlockProgress();
    this._cancel.Click += (_, _) => this.RequestMaintenanceCancellation(confirmNativeInPlace: true);
    this._scramble.Click += (_, _) => this.OnScramble();
    this._compact.Click += (_, _) => this.OnCompact();
    this._purge.Click += (_, _) => this.OnPurge();
    this._wipeEmpty.Click += (_, _) => this.OnWipeEmpty();
    this._shrink.Click += (_, _) => this.OnShrink();
    this._close.Click += (_, _) => this.Close();

    this.Controls.AddRange(
      new Label { Text = "Seed" }, this._scrambleSeed, this._scramble, this._run, this._cancel, this._close,
      this._minimalGeometry, this._compact, this._purge, this._wipeEmpty, this._shrink);
    this.AcceptButton = this._run;
  }

  // ── Layout ──────────────────────────────────────────────────────────────────────────────────

  private void LayoutChildren() {
    const int Gutter = 12;
    var width = this.ClientSize.Width - 2 * Gutter;

    this._imageBox.Bounds = new(Gutter, Gutter, width, 52);
    this._imagePathBox.Bounds = new(8, 20, Math.Max(80, width - 110), 24);
    this._browse.Bounds = new(width - 96, 20, 88, 24);

    var y = Gutter + 60;
    this._infoPanel.Bounds = new(Gutter, y, width, 70);
    this._formatLabel.Bounds = new(132, 4, width - 140, 20);
    this._supportLabel.Bounds = new(132, 26, width - 140, 20);
    this._sizeLabel.Bounds = new(132, 48, width - 140, 20);

    y += 78;
    var strategyHeight = this._fsModesGroup.Visible ? 240 : 0;
    this._fsModesGroup.Bounds = new(Gutter, y, width, 240);
    this.LayoutFsModes(width);

    this._archiveRepackGroup.Bounds = new(Gutter, y, width, 110);
    this._archiveRepackNote.Bounds = new(8, 20, width - 16, 34);
    this._smartSolidRepack.Bounds = new(8, 58, 300, 20);
    this._metadataPlacementCaption.Bounds = new(8, 82, 120, 20);
    this._metadataPlacement.Bounds = new(132, 80, 180, 24);
    if (this._archiveRepackGroup.Visible) strategyHeight = 110;

    y += strategyHeight + (strategyHeight > 0 ? 8 : 0);
    if (this._carveOptsGroup.Visible) {
      this._carveOptsGroup.Bounds = new(Gutter, y, width, 76);
      LayoutCarveRow(this._carveOptsGroup, 0, 20, width);
      LayoutCarveRow(this._carveOptsGroup, 3, 46, width);
      y += 84;
    }

    var actionsHeight = 76;
    var outputHeight = 84;
    var mapHeight = Math.Max(160, this.ClientSize.Height - y - outputHeight - actionsHeight - Gutter);
    this._split.Bounds = new(Gutter, y, width, mapHeight);
    if (!this._split.Panel2Collapsed) this._split.SplitterDistance = Math.Max(240, width * 2 / 3);

    this._layoutStatus.Bounds = new(6, 18, Math.Max(50, this._blockMapGroup.Width - 12), 18);
    this._blockMapGroup.Controls[1].Bounds = new(6, 40, 36, 20); // "View:"
    this._blockMapView.Bounds = new(44, 38, 200, 24);
    this._legend.Bounds = new(6, this._blockMapGroup.Height - 46, Math.Max(50, this._blockMapGroup.Width - 130), 40);
    this._filesPanelToggle.Bounds = new(this._blockMapGroup.Width - 120, this._blockMapGroup.Height - 36, 110, 22);
    this._blockMap.Bounds = new(6, 66, Math.Max(50, this._blockMapGroup.Width - 12), Math.Max(60, this._blockMapGroup.Height - 66 - 50));

    this._filesPanelStatus.Bounds = new(4, 18, Math.Max(50, this._filesPanelGroup.Width - 8), 18);
    this._filesGrid.Bounds = new(4, 40, Math.Max(50, this._filesPanelGroup.Width - 8), Math.Max(40, this._filesPanelGroup.Height - 46));

    y += mapHeight + 8;
    this._outputGroup.Bounds = new(Gutter, y, width, outputHeight - 8);
    this._outputBox.Bounds = new(6, 18, Math.Max(50, width - 12), outputHeight - 32);
    this._progress.Bounds = new(Gutter, y + outputHeight - 12, width, 4);

    this.LayoutActionButtons(Gutter, this.ClientSize.Height - actionsHeight + 8, width);

    static void LayoutCarveRow(GroupBox box, int first, int top, int width) {
      box.Controls[first].Bounds = new(8, top + 2, 110, 20);
      box.Controls[first + 1].Bounds = new(122, top, Math.Max(60, width - 260), 22);
      box.Controls[first + 2].Bounds = new(width - 130, top + 2, 124, 20);
    }
  }

  private void LayoutFsModes(int width) {
    var controls = this._fsModesGroup.Controls;
    var y = 20;

    foreach (var button in new[] { this._modePackStart, this._modePackEnd, this._modeFillHoles, this._modeAscending, this._modeCarveHole }) {
      button.Bounds = new(8, y, Math.Max(100, width - 20), 20);
      y += 24;
    }

    y += 6;
    controls[5].Bounds = new(8, y + 2, 118, 20);          // "Block interleave:"
    this._interleaveStride.Bounds = new(128, y, 76, 22);
    controls[7].Bounds = new(212, y + 2, 300, 20);        // hint

    y += 28;
    controls[8].Bounds = new(8, y + 2, 118, 20);          // "Metadata placement:"
    this._metadataZone.Bounds = new(128, y, 200, 24);

    y += 30;
    controls[10].Bounds = new(8, y + 2, 118, 20);         // "Layout profile:"
    this._layoutProfile.Bounds = new(128, y, Math.Max(120, width - 280), 24);
    this._editProfiles.Bounds = new(width - 140, y, 120, 24);
  }

  private void LayoutActionButtons(int gutter, int top, int width) {
    // Second row first, so the primary actions sit closest to the bottom edge.
    var right = gutter + width;
    var x = right - 108;
    this._shrink.Bounds = new(x, top, 100, 28);
    x -= 108;
    this._wipeEmpty.Bounds = new(x, top, 100, 28);
    x -= 108;
    this._purge.Bounds = new(x, top, 100, 28);
    x -= 108;
    this._compact.Bounds = new(x, top, 100, 28);
    x -= 140;
    this._minimalGeometry.Bounds = new(x, top + 4, 134, 20);

    var second = top + 34;
    x = right - 88;
    this._close.Bounds = new(x, second, 80, 28);
    x -= 88;
    this._cancel.Bounds = new(x, second, 80, 28);
    x -= 128;
    this._run.Bounds = new(x, second, 120, 28);
    x -= 108;
    this._scramble.Bounds = new(x, second, 100, 28);
    x -= 68;
    this._scrambleSeed.Bounds = new(x, second + 3, 60, 22);
    this.Controls[^11].Bounds = new(x - 40, second + 5, 36, 20); // "Seed"
  }

  // ── Layout profiles ─────────────────────────────────────────────────────────────────────────

  private void RefreshLayoutProfilesCombo() {
    var previousPath = this._layoutProfile.SelectedIndex >= 0 && this._layoutProfile.SelectedIndex < this._layoutProfileEntries.Count
      ? this._layoutProfileEntries[this._layoutProfile.SelectedIndex]?.FilePath
      : null;

    this._layoutProfile.Items.Clear();
    this._layoutProfileEntries.Clear();

    this._layoutProfile.Items.Add("(none)");
    this._layoutProfileEntries.Add(null);
    foreach (var entry in LayoutProfileStore.List()) {
      this._layoutProfile.Items.Add($"{entry.Name} [{(entry.Origin == ProfileOrigin.Builtin ? "Built-in" : "User")}]");
      this._layoutProfileEntries.Add(entry);
    }

    // Preserve the user's previous pick across refreshes.
    if (!string.IsNullOrEmpty(previousPath))
      for (var i = 0; i < this._layoutProfileEntries.Count; ++i)
        if (string.Equals(this._layoutProfileEntries[i]?.FilePath, previousPath, StringComparison.OrdinalIgnoreCase)) {
          this._layoutProfile.SelectedIndex = i;
          return;
        }

    this._layoutProfile.SelectedIndex = 0;
  }

  private void OnLayoutProfileChanged() {
    var index = this._layoutProfile.SelectedIndex;
    if (index < 0 || index >= this._layoutProfileEntries.Count || this._layoutProfileEntries[index] is not { } entry) {
      this._selectedLayoutProfile = null;
      return;
    }

    try {
      this._selectedLayoutProfile = LayoutProfileStore.Load(entry);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Failed to load layout profile '{entry.Name}':\n{ex.Message}",
        "Layout profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
      this._selectedLayoutProfile = null;
      this._layoutProfile.SelectedIndex = 0;
    }
  }

  // ── Image loading ───────────────────────────────────────────────────────────────────────────

  private void ApplyRequestedVerb() {
    if (this._requestedVerb is not { } verb) return;

    this.Text = verb switch {
      MaintenanceVerb.Optimize => "Optimize",
      MaintenanceVerb.Shrink => "Shrink",
      MaintenanceVerb.Defragment => "Defragment",
      MaintenanceVerb.Purge => "Purge",
      MaintenanceVerb.WipeEmpty => "Wipe Empty",
      MaintenanceVerb.Compact => "Compact",
      MaintenanceVerb.Scramble => "Scramble",
      _ => "Maintenance",
    };

    var target = verb switch {
      MaintenanceVerb.Shrink => this._shrink,
      MaintenanceVerb.Purge => this._purge,
      MaintenanceVerb.WipeEmpty => this._wipeEmpty,
      MaintenanceVerb.Compact => this._compact,
      MaintenanceVerb.Scramble => this._scramble,
      // Optimize and Defragment both live on the morphing Run button.
      _ => this._run,
    };

    if (!target.Enabled) return;
    this.AcceptButton = target;
    target.Focus();
  }

  /// <summary>
  /// The loader sees <see cref="IArchiveDefragmentable"/> before archive-repack support. When the
  /// caller explicitly asked to optimize, correct that ambiguity so ZIP and 7z expose their repack
  /// controls instead of looking like filesystem-only defraggers.
  /// </summary>
  private void CorrectArchiveOptimizeAmbiguity() {
    if (this._requestedVerb != MaintenanceVerb.Optimize || this._formatId is not { Length: > 0 } id) return;

    var descriptor = FormatRegistry.GetById(id);
    var ops = FormatRegistry.GetArchiveOps(id);
    if (descriptor?.Category is not (FormatCategory.Archive or FormatCategory.CompoundTar) || ops is not IArchiveCreatable) return;

    this._isArchiveMode = true;
    this._archiveOps = ops;
    this._isSevenZipFormat = string.Equals(id, "SevenZip", StringComparison.Ordinal);
    this._fsModesGroup.Visible = false;
    this._archiveRepackGroup.Visible = true;
    this._smartSolidRepack.Visible = this._isSevenZipFormat;
    this._run.Text = "Optimize";
    this._run.Enabled = true;
    this._supportLabel.Text = "Archive re-layout/repack with live staged-target visualization.";
    this._supportLabel.ForeColor = Color.DarkGreen;
    this._layoutStatus.Text = "Source + staged-target address spaces share the chart for progress; offsets are projected, not physical equivalence.";
    this.LayoutChildren();
  }

  private void LoadImage(string path) {
    this._imagePath = path;
    this._isArchiveMode = false;
    this._isFileInternalMode = false;
    this._isSevenZipFormat = false;
    this._archiveOps = null;
    this._chunkMover = null;

    this._imagePathBox.Text = path;
    this._imagePathBox.ForeColor = Color.Black;

    FormatRegistration.EnsureInitialized();
    var format = FormatDetector.Detect(path);
    this._formatLabel.Text = format.ToString();
    this._formatId = format.ToString();

    var ops = FormatRegistry.GetArchiveOps(this._formatId);
    this._defragmentable = ops as IArchiveDefragmentable;

    var isArchiveLayout = ops is IArchiveLayoutMap;
    var isArchiveCreatable = ops is IArchiveCreatable;
    var isFileInternalLayout = ops is IFileInternalLayoutMap;
    var isFileInternalOptimizable = ops is IFileInternalChunkMover;

    if (this._defragmentable is not null) {
      var supported = ProbeSupportedModes(this._defragmentable);
      this._supportLabel.Text = supported.Count >= 4
        ? "All four modes (in-place defragment)."
        : $"{string.Join(", ", supported)} (other modes throw NotSupported)";
      this._supportLabel.ForeColor = Color.DarkGreen;
      this._run.Text = "Defragment";
      this._run.Enabled = true;
    } else if (isFileInternalLayout) {
      this._isFileInternalMode = true;
      this._archiveOps = ops;
      this._chunkMover = ops as IFileInternalChunkMover;
      this._supportLabel.Text = isFileInternalOptimizable
        ? "File-internal layout optimization (e.g. MP4 fast-start)."
        : "File-internal layout viewable but optimization not supported (read-only).";
      this._supportLabel.ForeColor = isFileInternalOptimizable ? Color.DarkGreen : Color.DarkOrange;
      this._run.Enabled = isFileInternalOptimizable;
      this._run.Text = "Optimize";
    } else if (isArchiveLayout || isArchiveCreatable) {
      this._isArchiveMode = true;
      this._isSevenZipFormat = this._formatId == "SevenZip";
      this._archiveOps = ops;
      this._supportLabel.Text = isArchiveCreatable
        ? "Archive optimization (extract + repack with optimal settings)."
        : "Archive layout viewable but format does not support creation (read-only).";
      this._supportLabel.ForeColor = isArchiveCreatable ? Color.DarkGreen : Color.DarkOrange;
      this._run.Enabled = isArchiveCreatable;
      this._run.Text = "Optimize";
    } else {
      this._supportLabel.Text = "Not supported by this format.";
      this._supportLabel.ForeColor = Color.OrangeRed;
      this._run.Text = "Defragment";
      this._run.Enabled = false;
    }

    var showFsModes = !this._isArchiveMode && !this._isFileInternalMode;
    this._fsModesGroup.Visible = showFsModes;
    this._archiveRepackGroup.Visible = !showFsModes;
    this._smartSolidRepack.Visible = this._isSevenZipFormat;
    this._metadataPlacement.Visible = this._isFileInternalMode;
    this._metadataPlacementCaption.Visible = this._isFileInternalMode;

    this._shrink.Enabled = this._formatId is "Fat" or "Ext" or "Ext1" or "Vhd" || ops is IArchiveShrinkable;
    this._wipeEmpty.Enabled = ops is IWipeEmpty or IFilesystemExtentMap or IArchiveLayoutMap;
    // Purge is realised through the modifier's Remove over every entry.
    this._purge.Enabled = ops is IArchiveModifiable;
    this._compact.Enabled = ops is IArchiveDefragmentable or IArchiveShrinkable or IArchiveCreatable;
    this._minimalGeometry.Enabled = ops is IArchiveCreatable and IFormatOptionsSchema;

    // Scramble has no fallback: a rebuild would pack the volume, the opposite of what it says.
    var canScramble = ops is IFilesystemScrambleable;
    this._scramble.Enabled = canScramble;
    this._scrambleSeed.Enabled = canScramble;

    var info = new FileInfo(path);
    this._sizeLabel.Text = $"{FormatSize(info.Length)} ({info.Length:N0} bytes)";

    this.PreviewBlockMap(path, ops);
    this.ApplyRequestedVerb();
    this.LayoutChildren();
  }

  // ── Block map preview ───────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds the initial block-map snapshot. A descriptor that exposes a real extent, archive or
  /// chunk layout gets the honest picture; anything else falls back to sizes laid end to end from
  /// zero, which the status line labels as approximate.
  /// </summary>
  private void PreviewBlockMap(string path, IArchiveFormatOperations? ops, bool wasMutated = false) {
    if (wasMutated) this.NotifyMutated(path);

    this._blockMap.BlockMap = null;
    this._blockMap.ImageSize = 0;
    this._filesGrid.DataSource = Array.Empty<object>();
    this._fileRowCount = 0;
    this._filesSortDescription = "listing order";
    this._filesPanelStatus.Text = "—";
    this._layoutStatus.Text = "—";
    if (ops is null) return;

    if (ops is IFilesystemExtentMap extentMap)
      try {
        using var stream = File.OpenRead(path);
        if (this.TryRenderExtentMap(extentMap, stream, ops)) return;
      } catch {
        // Extent walker failed mid-stream; fall through so the user still sees something.
      }

    if (ops is IArchiveLayoutMap archiveLayout)
      try {
        using var stream = File.OpenRead(path);
        if (this.TryRenderArchiveLayout(archiveLayout, stream, ops)) return;
      } catch {
        // Layout walker failed; fall through to the approximation.
      }

    if (ops is IFileInternalLayoutMap fileLayout)
      try {
        using var stream = File.OpenRead(path);
        if (this.TryRenderFileInternalLayout(fileLayout, stream)) return;
      } catch {
        // Chunk walker failed; fall through to the approximation.
      }

    this._layoutStatus.Text = "Approximate layout (post-defrag preview)";
    try {
      using var stream = File.OpenRead(path);
      var entries = ops.List(stream, password: null);
      var imageSize = stream.Length;
      var map = new List<DefragBlockInfo>();
      var rows = new List<FileRow>();
      var offset = 0L;

      var files = entries.Where(e => !e.IsDirectory).ToList();
      var thresholds = ComputeMtimeQuartiles(files.Select(f => f.LastModified));

      for (var k = 0; k < files.Count; ++k) {
        var e = files[k];
        var size = Math.Max(1, e.OriginalSize);
        var cls = Classify(e.LastModified, thresholds, k, files.Count);

        map.Add(new(offset, size, DefragBlockKind.Used, e.Name, cls));
        rows.Add(new() {
          Name = e.Name,
          Size = e.OriginalSize,
          SizeDisplay = FormatSize(e.OriginalSize),
          // The format reported no layout, so these tiles are sizes laid end to end rather than
          // where anything actually is. Counting runs off that would report one for everything.
          FragmentsDisplay = "—",
          Modified = e.LastModified,
          ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—",
          Class = cls.ToString(),
        });

        offset += size;
        if (offset >= imageSize) break;
      }

      if (offset < imageSize) map.Add(new(offset, imageSize - offset, DefragBlockKind.Free));

      this._blockMap.BlockMap = map;
      this._blockMap.ImageSize = imageSize;
      this.SetFileRows(rows);
    } catch {
      // Preview is best-effort; an empty map is an acceptable outcome.
    }
  }

  /// <summary>
  /// Splits the modification times into four equal-count buckets. The most recent quarter is hot,
  /// the oldest is frozen.
  /// </summary>
  private static (DateTime? Cold, DateTime? Normal, DateTime? Hot)? ComputeMtimeQuartiles(IEnumerable<DateTime?> times) {
    var sorted = times.Where(t => t.HasValue).Select(t => t!.Value).Order().ToList();
    if (sorted.Count == 0) return null;

    var n = sorted.Count;
    return (sorted[Math.Min(n - 1, n / 4)], sorted[Math.Min(n - 1, n / 2)], sorted[Math.Min(n - 1, 3 * n / 4)]);
  }

  private static DefragBlockClass Classify(DateTime? modified, (DateTime? Cold, DateTime? Normal, DateTime? Hot)? thresholds, int index, int count) {
    if (thresholds is not { } t)
      // No dates anywhere — fall back to listing-order quartile so the chart still has structure.
      return (index * 4 / Math.Max(1, count)) switch {
        0 => DefragBlockClass.Hot,
        1 => DefragBlockClass.Normal,
        2 => DefragBlockClass.Cold,
        _ => DefragBlockClass.Frozen,
      };

    // An undated entry in a dated set stands out as frozen rather than being guessed at.
    if (modified is not { } when_) return DefragBlockClass.Frozen;

    if (when_ < t.Cold) return DefragBlockClass.Frozen;
    if (when_ < t.Normal) return DefragBlockClass.Cold;
    return when_ < t.Hot ? DefragBlockClass.Normal : DefragBlockClass.Hot;
  }

  private bool TryRenderExtentMap(IFilesystemExtentMap extentMap, Stream stream, IArchiveFormatOperations ops) {
    var imageSize = stream.Length;
    if (imageSize <= 0) return false;

    var filled = NormalizeExtents(extentMap.EnumerateExtents(stream).ToList(), imageSize);
    if (filled is null) return false;

    Dictionary<string, DateTime?>? mtimeByName = null;
    List<ArchiveEntryInfo>? entries = null;
    try {
      stream.Position = 0;
      entries = ops.List(stream, password: null);
      mtimeByName = entries.Where(e => !e.IsDirectory)
                           .GroupBy(e => e.Name)
                           .ToDictionary(g => g.Key, g => g.First().LastModified);
    } catch {
      // Classification is best-effort; the layout itself is what matters here.
    }

    var thresholds = mtimeByName is null ? null : ComputeMtimeQuartiles(mtimeByName.Values);
    var classified = new List<DefragBlockInfo>(filled.Count);
    foreach (var ex in filled) {
      if (ex.Kind != DefragBlockKind.Used || ex.FileName is null || mtimeByName is null
          || !mtimeByName.TryGetValue(ex.FileName, out var mtime)) {
        classified.Add(ex);
        continue;
      }

      classified.Add(ex with { Classification = thresholds is null ? DefragBlockClass.Normal : Classify(mtime, thresholds, 0, 1) });
    }

    this._blockMap.BlockMap = classified.Count > MaxExtents ? BinExtents(classified, MaxExtents) : classified;
    this._blockMap.ImageSize = imageSize;

    var fragCount = CountRunsByOwner(classified);
    if (entries is not null)
      this.SetFileRows([.. entries.Where(e => !e.IsDirectory).Select(e => new FileRow {
        Name = e.Name,
        Size = e.OriginalSize,
        SizeDisplay = FormatSize(e.OriginalSize),
        FragmentsDisplay = fragCount.TryGetValue(e.Name, out var runs) && runs > 0 ? runs.ToString("N0") : "—",
        Modified = e.LastModified,
        ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—",
        Class = "—",
      })]);

    var usedExtents = classified.Count(ex => ex.Kind == DefragBlockKind.Used);
    var fragmentedFiles = entries is null ? 0 : fragCount.Values.Count(runs => runs > 1);
    this._layoutStatus.Text = fragmentedFiles > 0
      ? $"Real on-disk layout — {usedExtents:N0} extents, {fragmentedFiles:N0} fragmented file(s)"
      : $"Real on-disk layout — {usedExtents:N0} extents (no fragmentation detected)";
    return true;
  }

  private bool TryRenderArchiveLayout(IArchiveLayoutMap archiveLayout, Stream stream, IArchiveFormatOperations? ops) {
    var imageSize = stream.Length;
    if (imageSize <= 0) return false;

    var filled = NormalizeExtents(archiveLayout.EnumerateLayout(stream).ToList(), imageSize);
    if (filled is null) return false;

    Dictionary<string, string>? methodByName = null;
    List<ArchiveEntryInfo>? entries = null;
    try {
      stream.Position = 0;
      entries = ops?.List(stream, password: null);
      if (entries is not null)
        methodByName = entries.Where(e => !e.IsDirectory)
                              .GroupBy(e => e.Name)
                              .ToDictionary(g => g.Key, g => g.First().Method ?? "");
    } catch {
      // Classification is best-effort.
    }

    var classified = new List<DefragBlockInfo>(filled.Count);
    foreach (var ex in filled) {
      if (ex.Kind != DefragBlockKind.Used || ex.FileName is null || methodByName is null
          || !methodByName.TryGetValue(ex.FileName, out var method)) {
        classified.Add(ex);
        continue;
      }

      classified.Add(ex with { Classification = ClassifyByMethod(method) });
    }

    this._blockMap.BlockMap = classified.Count > MaxExtents ? BinExtents(classified, MaxExtents) : classified;
    this._blockMap.ImageSize = imageSize;

    if (entries is not null) {
      // Counted off the layout the container reported. An entry the map does not name is not one
      // run — it is unmeasured.
      var runsByName = CountRunsByOwner(classified);
      this.SetFileRows([.. entries.Where(e => !e.IsDirectory).Select(e => new FileRow {
        Name = e.Name,
        Size = e.OriginalSize,
        SizeDisplay = FormatSize(e.OriginalSize),
        FragmentsDisplay = runsByName.TryGetValue(e.Name, out var runs) ? runs.ToString("N0") : "—",
        MethodDisplay = e.Method ?? "",
        Modified = e.LastModified,
        ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "",
        Class = ClassifyByMethod(e.Method ?? "").ToString(),
      })]);
    }

    var regionCount = classified.Count(ex => ex.Kind == DefragBlockKind.Used);
    var freeBytes = classified.Where(ex => ex.Kind == DefragBlockKind.Free).Sum(ex => ex.Length);
    this._layoutStatus.Text = freeBytes > 0
      ? $"Real archive layout — {regionCount:N0} regions, {FormatSize(freeBytes)} wasted"
      : $"Real archive layout — {regionCount:N0} regions (tightly packed)";
    return true;
  }

  private bool TryRenderFileInternalLayout(IFileInternalLayoutMap fileLayout, Stream stream) {
    var imageSize = stream.Length;
    if (imageSize <= 0) return false;

    var filled = NormalizeExtents(fileLayout.EnumerateChunks(stream).ToList(), imageSize);
    if (filled is null) return false;

    this._blockMap.BlockMap = filled;
    this._blockMap.ImageSize = imageSize;

    // A row here is one stretch of the file, so the count belongs to the chunk's owner and comes
    // from the same layout the map was drawn from.
    var chunkRuns = CountRunsByOwner(filled, onlyKind: null);
    this.SetFileRows([.. filled.Select(ch => new FileRow {
      Name = ch.FileName ?? ch.Kind.ToString(),
      Size = ch.Length,
      SizeDisplay = FormatSize(ch.Length),
      FragmentsDisplay = ch.FileName is { } owner && chunkRuns.TryGetValue(owner, out var runs) ? runs.ToString("N0") : "—",
      MethodDisplay = ch.Kind.ToString(),
      Class = ch.Classification?.ToString() ?? "—",
    })]);

    this._layoutStatus.Text = $"File-internal layout — {filled.Count(c => c.Kind != DefragBlockKind.Free):N0} chunks";
    return true;
  }

  /// <summary>
  /// Sorts extents by offset, clips them to the image, fills the gaps as free space and trims
  /// overlaps so a later extent at the same offset wins. Returns null when nothing usable remains.
  /// </summary>
  private static List<DefragBlockInfo>? NormalizeExtents(List<DefragBlockInfo> raw, long imageSize) {
    if (raw.Count == 0) return null;
    raw.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

    var clipped = new List<DefragBlockInfo>(raw.Count);
    foreach (var ex in raw) {
      if (ex.Offset >= imageSize) continue;
      var length = Math.Min(ex.Length, imageSize - ex.Offset);
      if (length <= 0) continue;
      clipped.Add(ex with { Length = length });
    }
    if (clipped.Count == 0) return null;

    var filled = new List<DefragBlockInfo>(clipped.Count * 2);
    var cursor = 0L;
    foreach (var ex in clipped) {
      if (ex.Offset > cursor)
        filled.Add(new(cursor, ex.Offset - cursor, DefragBlockKind.Free));
      else if (ex.Offset < cursor) {
        if (ex.Offset + ex.Length <= cursor) continue;
        var trimmed = ex with { Length = ex.Length - (cursor - ex.Offset), Offset = cursor };
        filled.Add(trimmed);
        cursor = trimmed.Offset + trimmed.Length;
        continue;
      }

      filled.Add(ex);
      cursor = ex.Offset + ex.Length;
    }

    if (cursor < imageSize) filled.Add(new(cursor, imageSize - cursor, DefragBlockKind.Free));
    return filled;
  }

  /// <summary>Maps a compression method name to a thermal class for colour-coding archive entries.</summary>
  private static DefragBlockClass ClassifyByMethod(string method) {
    var m = method.ToUpperInvariant();
    if (m.Contains("STORE") || m.Contains("COPY") || m is "NONE" or "") return DefragBlockClass.Frozen;
    if (m.Contains("LZMA") || m.Contains("PPMD") || m.Contains("BZIP")) return DefragBlockClass.Hot;
    if (m.Contains("ZSTD")) return DefragBlockClass.Cold;
    return DefragBlockClass.Normal;
  }

  /// <summary>
  /// Bins an oversized extent list down to <paramref name="maxCount"/> by merging adjacent
  /// same-kind extents, then — if still over budget — by equal-sized byte bins taking each bin's
  /// dominant kind.
  /// </summary>
  private static List<DefragBlockInfo> BinExtents(List<DefragBlockInfo> input, int maxCount) {
    if (input.Count <= maxCount) return input;

    var merged = new List<DefragBlockInfo>(input.Count);
    foreach (var ex in input) {
      if (merged.Count == 0) {
        merged.Add(ex);
        continue;
      }

      var last = merged[^1];
      if (last.Kind == ex.Kind && last.Offset + last.Length == ex.Offset && last.FileName == ex.FileName)
        merged[^1] = last with { Length = last.Length + ex.Length };
      else
        merged.Add(ex);
    }
    if (merged.Count <= maxCount) return merged;

    var imageSize = merged[^1].Offset + merged[^1].Length;
    var bytesPerBin = (double)imageSize / maxCount;
    var binned = new List<DefragBlockInfo>(maxCount);
    var binStart = 0L;
    var binIndex = 0;
    var binKind = DefragBlockKind.Free;
    var binBestLength = 0L;
    string? binBestName = null;

    foreach (var ex in merged) {
      while (ex.Offset + ex.Length > binStart + bytesPerBin && binIndex < maxCount - 1) {
        if (ex.Length > binBestLength) {
          binBestLength = ex.Length;
          binKind = ex.Kind;
          binBestName = ex.FileName;
        }

        binned.Add(new(binStart, (long)bytesPerBin, binKind, binBestName));
        binStart += (long)bytesPerBin;
        ++binIndex;
        binKind = DefragBlockKind.Free;
        binBestLength = 0;
        binBestName = null;
      }

      if (ex.Length > binBestLength) {
        binBestLength = ex.Length;
        binKind = ex.Kind;
        binBestName = ex.FileName;
      }
    }

    binned.Add(new(binStart, imageSize - binStart, binKind, binBestName));
    return binned;
  }

  /// <summary>
  /// How many separate stretches of the container each owner's data occupies.
  /// <para>
  /// Counting the entries a map yields would measure the map rather than the layout: some merge an
  /// owner's consecutive blocks into one entry and some emit one per block, so a contiguous file and
  /// a scattered one would report the same figure. Stretches that end exactly where the next begins
  /// are one run either way.
  /// </para>
  /// </summary>
  private static Dictionary<string, int> CountRunsByOwner(
    IEnumerable<DefragBlockInfo> extents, DefragBlockKind? onlyKind = DefragBlockKind.Used) {
    var runs = new Dictionary<string, int>(StringComparer.Ordinal);
    var endOf = new Dictionary<string, long>(StringComparer.Ordinal);

    foreach (var extent in extents) {
      if (onlyKind is { } wanted && extent.Kind != wanted) continue;
      if (extent.FileName is not { } owner) continue;

      if (!endOf.TryGetValue(owner, out var previousEnd) || previousEnd != extent.Offset) {
        runs.TryGetValue(owner, out var count);
        runs[owner] = count + 1;
      }

      endOf[owner] = extent.Offset + extent.Length;
    }

    return runs;
  }

  private void SetFileRows(List<FileRow> rows) {
    this._filesGrid.DataSource = rows;
    this._fileRowCount = rows.Count;
    this._filesSortDescription = "listing order";
    this.UpdateFilesPanelStatus();
  }

  /// <summary>
  /// Refreshes the row-count and ordering line above the file grid, so a very large filesystem still
  /// gives a sense of scale that scrolling alone does not.
  /// </summary>
  private void UpdateFilesPanelStatus() {
    if (this._fileRowCount <= 0) {
      this._filesPanelStatus.Text = "—";
      return;
    }

    var countLabel = this._fileRowCount == 1 ? "1 file" : $"{this._fileRowCount:N0} files";
    this._filesPanelStatus.Text = $"{countLabel} · sorted by {this._filesSortDescription}";
  }

  private void TrackSort() {
    if (this._filesGrid.SortedColumn is not { } column) return;
    var arrow = this._filesGrid.SortOrder == SortOrder.Ascending ? "↑" : "↓";
    this._filesSortDescription = $"{column.HeaderText} {arrow}";
    this.UpdateFilesPanelStatus();
  }

  /// <summary>
  /// Opens a modeless drill-down listing every block intersecting the clicked tile. Modeless so the
  /// user can keep clicking tiles to compare; each click re-targets the same window.
  /// </summary>
  private void OnBlockMapTileClicked(object? sender, TileClickedEventArgs e) {
    this._tileContentsWindow ??= CreateTileWindow(this);
    this._tileContentsWindow.SetContents(e.StartOffset, e.EndOffset, e.Contents);
    this._tileContentsWindow.Show();

    TileContentsWindow CreateTileWindow(Form owner) {
      var window = new TileContentsWindow();
      window.FormClosed += (_, _) => this._tileContentsWindow = null;
      return window;
    }
  }

  private void OnModeChanged() {
    this._carveOptsGroup.Visible = this._modeCarveHole.Checked;
    this.LayoutChildren();
  }

  // ── Operation plumbing ──────────────────────────────────────────────────────────────────────

  private void OnFormClosing(object? sender, FormClosingEventArgs e) {
    if (e.Cancel || this._maintenanceCancellation is null) return;
    e.Cancel = true;
    this.RequestMaintenanceCancellation(confirmNativeInPlace: true);
  }

  private CancellationToken BeginMaintenanceOperation(string name, bool staged) {
    this._maintenanceCancellation?.Dispose();
    this._maintenanceCancellation = new();
    this._maintenanceOperationName = name;
    this._maintenanceIsStaged = staged;
    this._maintenanceCommitStarted = false;

    this._cancel.Enabled = true;
    foreach (var button in new[] { this._browse, this._run, this._shrink, this._wipeEmpty, this._purge, this._compact })
      button.Enabled = false;

    if (staged)
      this._layoutStatus.Text = $"{name}: building a staged target — original remains unchanged until commit.";

    return this._maintenanceCancellation.Token;
  }

  private void EndMaintenanceOperation() {
    this._maintenanceCancellation?.Dispose();
    this._maintenanceCancellation = null;
    this._maintenanceOperationName = null;
    this._maintenanceIsStaged = false;
    this._maintenanceCommitStarted = false;
    this._cancel.Enabled = false;
    this._browse.Enabled = true;

    // Reloading restores every button to what the format supports, rather than to whatever the last
    // operation happened to leave behind.
    if (this._imagePath is { Length: > 0 } path && File.Exists(path)) this.LoadImage(path);
  }

  private void RequestMaintenanceCancellation(bool confirmNativeInPlace) {
    if (this._maintenanceCancellation is not { } cts || cts.IsCancellationRequested) return;

    if (this._maintenanceCommitStarted) {
      this.Append("Cancellation ignored: verified target commit has started; completing it is safer than interrupting the only commit step.");
      this._cancel.Enabled = false;
      return;
    }

    if (!this._maintenanceIsStaged && confirmNativeInPlace) {
      var answer = MessageBox.Show(this,
        $"Cancel {this._maintenanceOperationName ?? "maintenance"}?\n\n"
        + "This operation is moving data in place. Moves already completed are not rolled back. "
        + "Cancellation is best-effort at the next safe boundary, so the layout may be partially changed although the container should remain valid.",
        "Cancel in-place maintenance", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
      if (answer != DialogResult.Yes) return;
      this.Append("Cancellation requested — already-completed in-place moves will remain moved.");
    } else {
      this.Append("Cancellation requested — staged target will be discarded; existing archive remains unchanged.");
    }

    this._cancel.Enabled = false;
    cts.Cancel();
  }

  private void OnRunWithBlockProgress() {
    if (this._imagePath is null || this._maintenanceCancellation is not null) return;

    if (this._isFileInternalMode) {
      this.OnRunFileInternalOptimize();
      return;
    }

    var ops = this._formatId is { Length: > 0 } id ? FormatRegistry.GetArchiveOps(id) : this._archiveOps;
    var descriptor = this._formatId is { Length: > 0 } formatId ? FormatRegistry.GetById(formatId) : null;
    var explicitlyOptimizingArchive = this._requestedVerb == MaintenanceVerb.Optimize
      && descriptor?.Category is FormatCategory.Archive or FormatCategory.CompoundTar
      && ops is IArchiveCreatable;

    if (this._isArchiveMode || explicitlyOptimizingArchive) {
      this.RunArchiveOptimizeWithBlockProgress(ops);
      return;
    }

    this.RunDefragWithBlockProgress();
  }

  private void RunDefragWithBlockProgress() {
    if (this._imagePath is not { } path || this._defragmentable is not { } defragmentable) return;

    var mode = this.SelectedMode();
    var holeSize = mode == DefragMode.CarveHole ? ParseSize(this._holeSize.Text) : 0L;
    var holeAt = mode == DefragMode.CarveHole ? ParseHoleAt(this._holeAt.Text) : -1L;
    var interleaveStride = ParseInterleaveStride(this._interleaveStride.Text);
    var metadataZone = this.SelectedMetadataZone();
    var layoutProfile = this._selectedLayoutProfile;
    var staged = UsesGenericStagedDefrag(defragmentable);
    var token = this.BeginMaintenanceOperation("Defragment / re-layout", staged);

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Defragmenting {Path.GetFileName(path)} ===");
    this.Append($"Mode: {mode}");
    if (staged) this.Append("Strategy: verified staged rebuild; original is unchanged until commit.");
    if (interleaveStride > 1) this.Append($"Block interleave: {interleaveStride}");
    if (metadataZone != MetadataZone.Unchanged) this.Append($"Metadata zone: {metadataZone}");
    if (layoutProfile is not null) this.Append($"Layout profile: {layoutProfile.Name} ({layoutProfile.Zones.Count} zone(s))");
    if (mode == DefragMode.CarveHole) {
      this.Append($"Hole size: {holeSize:N0} bytes");
      this.Append($"Hole at: {(holeAt < 0 ? "auto (end)" : holeAt.ToString("N0"))}");
    }

    this._progress.Style = ProgressBarStyle.Blocks;
    this._progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var cancelled = false;
      string? finalStatus = null;

      void OnProgress(DefragProgressEvent ev) {
        if (ev.Phase == "complete" && !string.IsNullOrWhiteSpace(ev.Status)) finalStatus = ev.Status;
        this.BeginInvoke(() => {
          if (ev.Phase == "committing") {
            this._maintenanceCommitStarted = true;
            this._cancel.Enabled = false;
          }

          if (ev.BlockMap is not null) {
            this._blockMap.BlockMap = ev.BlockMap;
            this._blockMap.ImageSize = ev.ImageSize;
          } else if (ev.ImageSize > 0 && this._blockMap.ImageSize <= 0) {
            this._blockMap.ImageSize = ev.ImageSize;
          }

          this._blockMap.ReadHead = ev.CurrentReadOffset;
          this._blockMap.WriteHead = ev.CurrentWriteOffset;
          if (ev.Fraction >= 0) this._progress.Value = (int)(Math.Clamp(ev.Fraction, 0, 1) * 100);
          if (!string.IsNullOrWhiteSpace(ev.Status)) this._layoutStatus.Text = ev.Status;
        });
      }

      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        defragmentable.Defragment(stream, new DefragOptions {
          Mode = mode,
          HoleSize = holeSize,
          HoleAt = holeAt,
          InterleaveStride = interleaveStride,
          MetadataZonePlacement = metadataZone,
          LayoutTemplate = layoutProfile,
          OnProgress = OnProgress,
          CancellationToken = token,
        });
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();

      this.Invoke(() => {
        this._blockMap.ReadHead = -1;
        this._blockMap.WriteHead = -1;
        this._progress.Value = 100;

        if (cancelled)
          this.Append(staged
            ? $"CANCELLED ({sw.ElapsedMilliseconds} ms) — staged rebuild discarded; existing container unchanged."
            : $"CANCELLED ({sw.ElapsedMilliseconds} ms) — completed native moves remain in place.");
        else if (error is not null)
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        else {
          this.Append($"OK ({sw.ElapsedMilliseconds} ms)");
          if (token.IsCancellationRequested)
            this.Append("Cancellation was requested, but the native operation completed before reaching a cancellable boundary.");
          if (!string.IsNullOrWhiteSpace(finalStatus)) this.Append($"Status: {finalStatus}");
          if (mode == DefragMode.ConsolidateAtEnd)
            this.Append("Tip: image byte size doesn't change for in-place end-pack — see the block chart for the new layout.");
          this.NotifyMutated(path);
        }

        this.Append("");
        this.EndMaintenanceOperation();
      });
    });
  }

  /// <summary>
  /// True when the descriptor inherits the generic staged rebuild rather than moving blocks itself,
  /// which is what makes a cancellation safe to discard.
  /// </summary>
  private static bool UsesGenericStagedDefrag(IArchiveDefragmentable defragmentable) {
    var type = defragmentable.GetType();
    return type.GetMethod(nameof(IArchiveDefragmentable.Defragment), [typeof(Stream)]) is null
        && type.GetMethod(nameof(IArchiveDefragmentable.Defragment), [typeof(Stream), typeof(DefragOptions)]) is null;
  }

  private void RunArchiveOptimizeWithBlockProgress(IArchiveFormatOperations? ops) {
    if (this._imagePath is not { } path || ops is null) return;

    if (this._isSevenZipFormat && this._smartSolidRepack.Checked) {
      this.RunSmartSevenZipWithBlockProgress(ops);
      return;
    }

    // CVF formats have a dedicated per-cluster optimizer whose writer already chooses compress or
    // store per cluster; trying every method at the container level would only waste CPU.
    if (this._formatId is "DoubleSpace" or "DriveSpace" or "DriveSpace3") {
      this.OnRunCvfOptimize(path, this._formatId);
      return;
    }

    var originalSize = new FileInfo(path).Length;
    var tempOut = path + ".opt.tmp";
    AtomicFileWriter.TryDelete(tempOut);
    var token = this.BeginMaintenanceOperation("Archive optimize / repack", staged: true);
    this.SetStagedArchiveMap(path, ops, originalSize);

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Optimizing {Path.GetFileName(path)} ===");
    this.Append("Staged rebuild: green head = projected source consumption; orange head = staged-target bytes written.");
    this._progress.Style = ProgressBarStyle.Blocks;
    this._progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var cancelled = false;
      var newSize = originalSize;
      var entriesOptimized = 0;

      try {
        var worker = Task.Run(() => ArchiveOperations.Optimize(path, tempOut, password: null));
        while (!worker.Wait(100)) {
          var stagedBytes = FindStagedOutputLength(tempOut);
          var fraction = originalSize > 0 ? Math.Clamp((double)stagedBytes / originalSize, 0, 0.95) : 0;
          this.BeginInvoke(() => {
            this._progress.Value = (int)(fraction * 100);
            var displaySize = Math.Max(1L, this._blockMap.ImageSize > 0 ? this._blockMap.ImageSize : originalSize);
            this._blockMap.ReadHead = Math.Clamp((long)(fraction * displaySize), 0, displaySize - 1);
            this._blockMap.WriteHead = stagedBytes > 0 ? Math.Clamp(stagedBytes, 0, displaySize - 1) : -1;
            this._layoutStatus.Text = token.IsCancellationRequested
              ? "Cancellation pending — current codec unit will finish, then the staged target is discarded."
              : $"Rebuilding staged target — {FormatSize(stagedBytes)} written; original unchanged.";
          });
        }

        var result = worker.GetAwaiter().GetResult();
        newSize = result.OptimizedSize;
        entriesOptimized = result.EntriesOptimized;

        if (token.IsCancellationRequested) {
          cancelled = true;
        } else {
          this.Invoke(() => {
            this._maintenanceCommitStarted = true;
            this._cancel.Enabled = false;
            this._progress.Value = 99;
            this._layoutStatus.Text = "Staged target complete — committing; cancellation is no longer safe.";
          });
          AtomicFileWriter.ReplaceTarget(tempOut, path);
        }
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        error = ex;
      } finally {
        AtomicFileWriter.TryDelete(tempOut);
      }
      sw.Stop();

      this.Invoke(() => {
        this._blockMap.ReadHead = -1;
        this._blockMap.WriteHead = -1;
        this._progress.Value = 100;

        if (cancelled)
          this.Append($"CANCELLED ({sw.ElapsedMilliseconds} ms) — staged target discarded; existing archive unchanged.");
        else if (error is not null)
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        else {
          var delta = newSize - originalSize;
          var pct = originalSize > 0 ? (double)delta / originalSize * 100 : 0;
          this.Append($"OK ({sw.ElapsedMilliseconds} ms) — {entriesOptimized} entries re-encoded");
          this.Append($"Archive size: {originalSize:N0} -> {newSize:N0} bytes (Δ {delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");
          this.NotifyMutated(path);
        }

        this.Append("");
        this.EndMaintenanceOperation();
      });
    });
  }

  private void RunSmartSevenZipWithBlockProgress(IArchiveFormatOperations ops) {
    if (this._imagePath is not { } path) return;

    var originalSize = new FileInfo(path).Length;
    var token = this.BeginMaintenanceOperation("7z solid-block re-group", staged: true);
    this.SetStagedArchiveMap(path, ops, originalSize);

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Smart solid-block repack: {Path.GetFileName(path)} ===");
    this.Append("All candidate layouts are staged. Cancel discards them and leaves the current 7z untouched.");
    this._progress.Style = ProgressBarStyle.Blocks;
    this._progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var cancelled = false;
      FileFormat.SevenZip.SolidBlockOptimizer.OptimizeResult? result = null;

      try {
        using var fs = File.OpenRead(path);
        result = FileFormat.SevenZip.SolidBlockOptimizer.Optimize(
          fs,
          maxTrials: 5,
          onProgress: (index, total, name) => this.BeginInvoke(() => this.Append($"  Trying strategy {index + 1}/{total}: {name}...")),
          onDetailedProgress: detail => this.BeginInvoke(() => {
            var displaySize = Math.Max(1L, this._blockMap.ImageSize > 0 ? this._blockMap.ImageSize : originalSize);
            double fraction;

            switch (detail.Phase) {
              case "extracting":
                fraction = 0.30 * detail.BytesDone / Math.Max(1.0, detail.BytesTotal);
                this._blockMap.ReadHead = Math.Clamp((long)(detail.BytesDone / Math.Max(1.0, detail.BytesTotal) * displaySize), 0, displaySize - 1);
                this._blockMap.WriteHead = -1;
                break;
              case "strategy":
                fraction = 0.30 + 0.10 * detail.Current / Math.Max(1.0, detail.Total);
                this._blockMap.ReadHead = -1;
                break;
              case "building":
                fraction = 0.40 + 0.55 * detail.Current / Math.Max(1.0, detail.Total);
                this._blockMap.ReadHead = -1;
                this._blockMap.WriteHead = Math.Clamp((long)(detail.Current / Math.Max(1.0, detail.Total) * displaySize), 0, displaySize - 1);
                break;
              default:
                fraction = 0;
                break;
            }

            this._progress.Value = (int)(Math.Clamp(fraction, 0, 0.95) * 100);
            this._layoutStatus.Text = detail.Phase switch {
              "extracting" => $"Reading source entry: {detail.Name}",
              "strategy" => $"Planning solid grouping: {detail.Name}",
              "building" => $"Building staged solid candidate: {detail.Name}",
              _ => "Staged 7z regrouping",
            };
          }),
          cancellationToken: token);
      } catch (OperationCanceledException) {
        cancelled = true;
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();

      this.Invoke(() => {
        this._blockMap.ReadHead = -1;
        this._blockMap.WriteHead = -1;
        this._progress.Value = 100;

        if (cancelled) {
          this.Append($"CANCELLED ({sw.ElapsedMilliseconds} ms) — candidate regroup discarded; existing 7z unchanged.");
        } else if (error is not null) {
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        } else if (result is not null) {
          foreach (var trial in result.Trials)
            this.Append($"    {trial.StrategyName}: {FormatSize(trial.OutputSize)} ({trial.Elapsed.TotalMilliseconds:F0} ms)");

          var newSize = (long)result.Data.Length;
          var delta = newSize - originalSize;
          var pct = originalSize > 0 ? (double)delta / originalSize * 100 : 0;
          this.Append($"  Winner: {result.WinningStrategy}");
          this.Append($"Archive size: {originalSize:N0} -> {newSize:N0} bytes ({delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");

          if (newSize < originalSize) {
            this._maintenanceCommitStarted = true;
            this._cancel.Enabled = false;
            this._layoutStatus.Text = "Winning staged layout selected — committing; cancellation disabled.";
            try {
              AtomicFileWriter.WriteAllBytesAtomic(path, result.Data);
              this.Append("Optimized archive written.");
              this.NotifyMutated(path);
            } catch (Exception writeError) {
              this.Append($"FAILED while committing winner: {writeError.Message}");
            }
          } else {
            this.Append("No strategy improved on the original size; archive unchanged.");
          }
        }

        this.Append("");
        this.EndMaintenanceOperation();
      });
    });
  }

  private void OnRunCvfOptimize(string path, string formatId) {
    var ops = this._archiveOps;
    var descriptor = FormatRegistry.GetById(formatId);
    if (descriptor is null) {
      this.Append($"FAILED: format descriptor '{formatId}' not registered.");
      this.Append("");
      this._run.Enabled = true;
      return;
    }

    Task.Run(() => {
      Exception? error = null;
      CvfOptimizer.OptimizeResult? result = null;
      try {
        result = CvfOptimizer.Optimize(path, descriptor);
      } catch (Exception ex) {
        error = ex;
      }

      this.Invoke(() => {
        this._progress.Value = 100;
        this._run.Enabled = true;
        this._blockMap.ReadHead = -1;
        this._blockMap.WriteHead = -1;

        if (error is not null || result is null) {
          this.Append($"FAILED: {error?.GetType().Name}: {error?.Message}");
        } else {
          var delta = result.OptimizedSize - result.OriginalSize;
          var pct = result.OriginalSize > 0 ? (double)delta / result.OriginalSize * 100 : 0;
          this.Append($"Optimized via {result.MethodUsed}: {result.OriginalSize / (1024.0 * 1024.0):F2} MB → {result.OptimizedSize / (1024.0 * 1024.0):F2} MB (Δ {pct:+0.0;-0.0;0.0}%)");
          if (result.FilesCompressed >= 0 || result.FilesStoredVerbatim >= 0)
            this.Append($"Cluster mix: {result.FilesCompressed} compressed, {result.FilesStoredVerbatim} stored verbatim (per-cluster shrink-or-store fallback)");
          this.Append($"Elapsed: {result.Elapsed.TotalMilliseconds:F0} ms");
          this.NotifyMutated(path);
        }

        this.Append("");
        this.PreviewBlockMap(path, ops, wasMutated: error is null);
      });
    });
  }

  private void OnRunFileInternalOptimize() {
    if (this._imagePath is not { } path || this._chunkMover is not { } chunkMover) return;

    var ops = this._archiveOps;
    var placementProfile = this.SelectedMetadataPlacement();

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Optimizing {Path.GetFileName(path)} (file-internal) ===");
    if (placementProfile is not null) this.Append($"Metadata placement: {placementProfile.Name}");

    this._run.Enabled = false;
    this._progress.Style = ProgressBarStyle.Marquee;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var originalSize = new FileInfo(path).Length;

      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        chunkMover.Optimize(stream, placementProfile);
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();
      var newSize = File.Exists(path) ? new FileInfo(path).Length : 0L;

      this.Invoke(() => {
        this._progress.Style = ProgressBarStyle.Blocks;
        this._progress.Value = 100;
        this._run.Enabled = true;
        this._blockMap.ReadHead = -1;
        this._blockMap.WriteHead = -1;

        if (error is not null)
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        else {
          this.Append($"OK ({sw.ElapsedMilliseconds} ms)");
          this.Append($"File size: {originalSize:N0} -> {newSize:N0} bytes (Δ {newSize - originalSize:+#,#;-#,#;0})");
        }

        this.Append("");
        this.PreviewBlockMap(path, ops, wasMutated: error is null);
      });
    });
  }

  /// <summary>
  /// Projects the archive's entries onto the image's byte span, weighted by size, so the staged
  /// rebuild has something to animate against before any output exists.
  /// </summary>
  private void SetStagedArchiveMap(string path, IArchiveFormatOperations ops, long displaySize) {
    try {
      using var stream = File.OpenRead(path);
      var entries = ops.List(stream, null).Where(e => !e.IsDirectory).ToArray();
      if (entries.Length == 0) return;

      var weights = entries.Select(e => Math.Max(1L, e.OriginalSize)).ToArray();
      var totalWeight = Math.Max(1L, weights.Sum());
      var size = Math.Max(1L, displaySize);
      var map = new List<DefragBlockInfo>(entries.Length);
      long cumulative = 0;
      long offset = 0;

      for (var i = 0; i < entries.Length; ++i) {
        cumulative += weights[i];
        var end = i == entries.Length - 1 ? size : (long)((double)cumulative / totalWeight * size);
        end = Math.Clamp(end, offset, size);
        if (end > offset)
          map.Add(new(offset, end - offset, DefragBlockKind.Used, entries[i].Name, ClassifyByMethod(entries[i].Method ?? "")));
        offset = end;
      }

      this._blockMap.BlockMap = map;
      this._blockMap.ImageSize = size;
      this._blockMap.ReadHead = 0;
      this._blockMap.WriteHead = -1;
      this._layoutStatus.Text = "Staged rebuild projection — colored target blocks; green source head / orange target head use separate byte-spaces.";
    } catch {
      // The existing preview stays usable when a synthetic target map cannot be built.
    }
  }

  /// <summary>
  /// The optimizer writes through its own temporary file, so the staged size is the largest of the
  /// target and any sibling scratch file it is currently filling.
  /// </summary>
  private static long FindStagedOutputLength(string target) {
    try {
      var best = File.Exists(target) ? new FileInfo(target).Length : 0L;
      var directory = Path.GetDirectoryName(target);
      if (string.IsNullOrEmpty(directory)) directory = Directory.GetCurrentDirectory();

      foreach (var candidate in Directory.EnumerateFiles(directory, Path.GetFileName(target) + ".tmp.*"))
        best = Math.Max(best, new FileInfo(candidate).Length);

      return best;
    } catch {
      return 0;
    }
  }

  // ── Individual verbs ────────────────────────────────────────────────────────────────────────

  private void OnShrink() {
    if (this._imagePath is not { } path) return;

    var formatId = this._formatLabel.Text;
    var ops = FormatRegistry.GetArchiveOps(formatId);

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Shrinking {Path.GetFileName(path)} ===");
    this._shrink.Enabled = false;
    this._run.Enabled = false;
    this._progress.Style = ProgressBarStyle.Blocks;
    this._progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var originalSize = new FileInfo(path).Length;
      long newSize = 0;
      var summary = "";

      try {
        this.BeginInvoke(() => this._progress.Value = 20);

        if (formatId == "Fat") {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          var result = FileSystem.Fat.FatShrinkHelper.Shrink(stream);
          newSize = result.NewSize;
          summary = result.WasReduced
            ? $"Reduced: {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)}"
            : "No reduction (image was already compact)";
        } else if (formatId is "Ext" or "Ext1") {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          var result = FileSystem.Ext.ExtShrinkHelper.Shrink(stream);
          newSize = result.NewSize;
          summary = result.WasReduced
            ? $"Reduced: {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)}"
            : "No reduction (image was already compact)";
        } else if (formatId == "Vhd") {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          var result = FileFormat.Vhd.VhdCompactor.Compact(stream);
          newSize = result.NewSize;
          summary = result.WasReduced
            ? $"Compacted: {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)} ({result.BlocksFreed} blocks freed)"
            : "No reduction (already compact)";
        } else if (ops is IArchiveShrinkable shrinkable) {
          // Shrink to a temp file then swap it in, so a crash mid-write cannot corrupt the source.
          var tempOut = path + ".shrink.tmp";
          try {
            using (var input = File.OpenRead(path))
            using (var output = File.Create(tempOut))
              shrinkable.Shrink(input, output);

            newSize = new FileInfo(tempOut).Length;
            AtomicFileWriter.WriteAllBytesAtomic(path, File.ReadAllBytes(tempOut));
            summary = newSize < originalSize
              ? $"Reduced: {FormatSize(originalSize)} -> {FormatSize(newSize)}"
              : "No reduction (already compact)";
          } finally {
            AtomicFileWriter.TryDelete(tempOut);
          }
        } else {
          throw new NotSupportedException($"Shrink not supported for format: {formatId}");
        }
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();
      if (newSize == 0 && File.Exists(path)) newSize = new FileInfo(path).Length;

      this.Invoke(() => {
        this._progress.Value = 100;
        this._shrink.Enabled = true;
        this._run.Enabled = true;

        if (error is not null)
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        else {
          this.Append($"OK ({sw.ElapsedMilliseconds} ms)");
          this.Append($"  {summary}");
          this._sizeLabel.Text = $"{FormatSize(newSize)} ({newSize:N0} bytes)";
        }

        this.Append("");
        this.PreviewBlockMap(path, ops, wasMutated: error is null);
      });
    });
  }

  private void OnWipeEmpty() {
    if (this._imagePath is not { } path) return;

    var formatId = this._formatLabel.Text;
    var ops = FormatRegistry.GetArchiveOps(formatId);

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Wiping unused space in {Path.GetFileName(path)} ===");
    this._wipeEmpty.Enabled = false;
    this._shrink.Enabled = false;
    this._run.Enabled = false;
    this._progress.Style = ProgressBarStyle.Blocks;
    this._progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var originalSize = new FileInfo(path).Length;
      long wiped = 0;

      // Total span of unused bytes. Stays negative when the format cannot expose its extent map
      // separately from the wipe itself.
      var totalUnused = -1L;

      try {
        this.BeginInvoke(() => this._progress.Value = 20);

        if (ops is IWipeEmpty wiper) {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);

          // The wiper reports only the bytes it had to overwrite. On a mostly-empty image that
          // figure and the actual free space diverge sharply, and the smaller one reads as "this
          // image is barely empty", which is wrong — so compute the total up front when we can.
          if (ops is IFilesystemExtentMap fsMap) {
            stream.Position = 0;
            totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(fsMap.EnumerateExtents(stream), stream.Length);
          } else if (ops is IArchiveLayoutMap arMap) {
            stream.Position = 0;
            totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(arMap.EnumerateLayout(stream), stream.Length);
          }

          stream.Position = 0;
          wiped = wiper.WipeUnusedSpace(stream);
        } else if (ops is IFilesystemExtentMap extentMap) {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          stream.Position = 0;
          var extents = extentMap.EnumerateExtents(stream).ToList();
          totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(extents, stream.Length);
          wiped = UnusedSpaceWiper.Wipe(stream, extents, stream.Length);
        } else if (ops is IArchiveLayoutMap layoutMap) {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          stream.Position = 0;
          var extents = layoutMap.EnumerateLayout(stream).ToList();
          totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(extents, stream.Length);
          wiped = UnusedSpaceWiper.Wipe(stream, extents, stream.Length, wipeClusterTips: false);
        } else {
          throw new NotSupportedException($"Format {formatId} does not support wipe-empty.");
        }

        this.BeginInvoke(() => this._progress.Value = 80);
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();

      this.Invoke(() => {
        this._progress.Value = 100;
        this._wipeEmpty.Enabled = true;
        this._run.Enabled = true;

        if (error is not null) {
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        } else {
          this.Append($"OK ({sw.ElapsedMilliseconds} ms)");
          if (totalUnused >= 0) {
            var unusedPct = originalSize > 0 ? 100.0 * totalUnused / originalSize : 0;
            var alreadyZero = Math.Max(0, totalUnused - wiped);
            this.Append($"  Unused space: {FormatSize(totalUnused)} ({unusedPct:F1}% of image)");
            this.Append($"  Newly zeroed: {FormatSize(wiped)} ({wiped:N0} bytes); {FormatSize(alreadyZero)} was already zero");
          } else {
            var writtenPct = originalSize > 0 ? 100.0 * wiped / originalSize : 0;
            this.Append($"  Wiped {FormatSize(wiped)} ({wiped:N0} bytes, {writtenPct:F1}% of image)");
          }
        }

        this.Append("");
        this.PreviewBlockMap(path, ops, wasMutated: error is null);
      });
    });
  }

  private void OnPurge() {
    if (this._imagePath is not { } path) return;

    var formatId = this._formatLabel.Text;
    var ops = FormatRegistry.GetArchiveOps(formatId);
    if (ops is not IArchiveModifiable) {
      this.Append($"Purge not supported: {formatId} is not modifiable.");
      this.Append("");
      return;
    }

    // Enumerate what is about to be erased so the confirmation is honest.
    List<ArchiveEntryInfo> entries;
    try {
      using var probe = File.OpenRead(path);
      entries = ops.List(probe, password: null);
    } catch (Exception ex) {
      this.Append($"Purge aborted — could not list entries: {ex.Message}");
      this.Append("");
      return;
    }

    var liveNames = entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToArray();
    var allNames = entries.Select(e => e.Name).ToArray();
    if (allNames.Length == 0) {
      this.Append("Container is already empty — nothing to purge.");
      this.Append("");
      return;
    }

    var confirm = MessageBox.Show(this,
      $"Erase ALL {liveNames.Length} file(s) from {Path.GetFileName(path)}?\n\n"
      + "This leaves a valid but empty container. The data cannot be recovered.",
      "Confirm Purge", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
    if (confirm != DialogResult.Yes) return;

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Purging {Path.GetFileName(path)} ===");
    foreach (var button in new[] { this._purge, this._wipeEmpty, this._shrink, this._run }) button.Enabled = false;
    this._progress.Style = ProgressBarStyle.Marquee;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      var originalSize = new FileInfo(path).Length;

      try {
        ArchiveOperations.Remove(path, allNames);
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();
      var newSize = File.Exists(path) ? new FileInfo(path).Length : 0L;

      this.Invoke(() => {
        this._progress.Style = ProgressBarStyle.Blocks;
        this._progress.Value = 100;
        this._purge.Enabled = true;
        this._run.Enabled = true;

        if (error is not null)
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}");
        else {
          this.Append($"OK ({sw.ElapsedMilliseconds} ms) — {liveNames.Length} file(s) erased");
          this.Append($"Container size: {originalSize:N0} -> {newSize:N0} bytes (Δ {newSize - originalSize:+#,#;-#,#;0})");
        }

        this.Append("");
        this.PreviewBlockMap(path, FormatRegistry.GetArchiveOps(formatId), wasMutated: error is null);
      });
    });
  }

  /// <summary>
  /// Scatters every allocation block across the volume, so the map shows the interleaving this
  /// window exists to fix.
  /// </summary>
  /// <remarks>
  /// It asks first, and it names the file. Every other button here either improves the image or
  /// leaves it as it was; this one deliberately makes it worse, and only a test wants that done to
  /// a real image.
  /// </remarks>
  private void OnScramble() {
    if (this._imagePath is not { } path) return;

    var formatId = this._formatLabel.Text;
    if (FormatRegistry.GetArchiveOps(formatId) is not IFilesystemScrambleable scrambler) {
      this.Append($"Scramble not supported: {formatId} cannot scatter a volume in place.");
      this.Append("");
      return;
    }

    if (!int.TryParse(this._scrambleSeed.Text, out var seed)) {
      this.Append($"Scramble aborted — '{this._scrambleSeed.Text}' is not a seed.");
      this.Append("");
      return;
    }

    var confirm = MessageBox.Show(this,
      $"Fragment {Path.GetFileName(path)} on purpose?\n\n"
      + "Every file's blocks are scattered across the volume. The contents are preserved "
      + "exactly -- only the layout changes, and Defragment undoes it -- but this is a "
      + "testing tool, not a repair.",
      "Confirm Scramble", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
    if (confirm != DialogResult.Yes) return;

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Scrambling {Path.GetFileName(path)} (seed {seed}) ===");

    // Everything else is locked out while the layout is rewritten, then put back exactly as it was:
    // which buttons apply to this image is a property of the format, not of what was just run.
    var others = new[] { this._compact, this._purge, this._wipeEmpty, this._shrink, this._run };
    var wasEnabled = others.Select(b => b.Enabled).ToArray();
    foreach (var button in others) button.Enabled = false;
    this._scramble.Enabled = false;
    this._scrambleSeed.Enabled = false;
    this._progress.Style = ProgressBarStyle.Marquee;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;

      // The same bridge the defragment run uses, so the map animates the scattering as it happens
      // rather than jumping to the finished layout.
      void OnProgress(DefragProgressEvent ev) => this.BeginInvoke(() => {
        if (ev.BlockMap is not null) {
          this._blockMap.BlockMap = ev.BlockMap;
          this._blockMap.ImageSize = ev.ImageSize;
        }

        this._blockMap.ReadHead = ev.CurrentReadOffset;
        this._blockMap.WriteHead = ev.CurrentWriteOffset;
        if (ev.Fraction >= 0) this._progress.Value = (int)(Math.Clamp(ev.Fraction, 0, 1) * 100);
      });

      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        scrambler.Scramble(stream, new ScrambleOptions { Seed = seed, OnProgress = OnProgress });
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();

      this.Invoke(() => {
        this._progress.Style = ProgressBarStyle.Blocks;
        this._progress.Value = 100;
        for (var i = 0; i < others.Length; ++i) others[i].Enabled = wasEnabled[i];
        this._scramble.Enabled = true;
        this._scrambleSeed.Enabled = true;

        this.Append(error is not null
          ? $"FAILED ({sw.ElapsedMilliseconds} ms): {error.GetType().Name}: {error.Message}"
          : $"OK ({sw.ElapsedMilliseconds} ms) — the volume is fragmented; Defragment puts it back");
        this.Append("");
        this.PreviewBlockMap(path, FormatRegistry.GetArchiveOps(formatId), wasMutated: error is null);
      });
    });
  }

  private void OnCompact() {
    if (this._imagePath is not { } path) return;

    var minimal = this._minimalGeometry.Checked;
    var formatId = this._formatLabel.Text;

    if (minimal) {
      var confirm = MessageBox.Show(this,
        "Minimal geometry rebuilds the container at the smallest size the format allows "
        + "(e.g. a 1.44 MB FAT floppy collapses to a few KB).\n\n"
        + "Contents are preserved, but the result may no longer be a standard, mountable image. Continue?",
        "Compact — minimal geometry", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
      if (confirm != DialogResult.Yes) return;
    }

    this.Append($"=== {DateTime.Now:HH:mm:ss}  Compacting {Path.GetFileName(path)}{(minimal ? " (minimal geometry)" : "")} ===");
    foreach (var button in new[] { this._compact, this._run, this._shrink, this._wipeEmpty, this._purge }) button.Enabled = false;
    this._progress.Style = ProgressBarStyle.Marquee;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? error = null;
      CompactOperation.CompactResult? result = null;

      try {
        result = CompactOperation.Compact(path, new CompactOperation.CompactOptions {
          Minimal = minimal,
          Log = line => this.BeginInvoke(() => this.Append("  " + line)),
        });
      } catch (Exception ex) {
        error = ex;
      }
      sw.Stop();

      this.Invoke(() => {
        this._progress.Style = ProgressBarStyle.Blocks;
        this._progress.Value = 100;
        this._compact.Enabled = true;
        this._run.Enabled = true;

        if (error is not null || result is null) {
          this.Append($"FAILED ({sw.ElapsedMilliseconds} ms): {error?.GetType().Name}: {error?.Message}");
        } else {
          var delta = result.NewSize - result.OriginalSize;
          var pct = result.OriginalSize > 0 ? 100.0 * delta / result.OriginalSize : 0;
          this.Append($"OK ({sw.ElapsedMilliseconds} ms) — steps: {(result.StepsRun.Count > 0 ? string.Join(", ", result.StepsRun) : "none")}");
          this.Append($"Container size: {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)} ({pct:+0.0;-0.0;0.0}%)");
        }

        this.Append("");
        this.PreviewBlockMap(path, FormatRegistry.GetArchiveOps(formatId), wasMutated: error is null);
      });
    });
  }

  // ── Option readers ──────────────────────────────────────────────────────────────────────────

  private DefragMode SelectedMode() {
    if (this._modePackEnd.Checked) return DefragMode.ConsolidateAtEnd;
    if (this._modeFillHoles.Checked) return DefragMode.FillHolesLazy;
    if (this._modeCarveHole.Checked) return DefragMode.CarveHole;
    if (this._modeAscending.Checked) return DefragMode.AscendingOrder;
    return DefragMode.ConsolidateAtStart;
  }

  private MetadataPlacementProfile? SelectedMetadataPlacement() => this._metadataPlacement.SelectedIndex switch {
    1 => MetadataPlacementProfile.MetadataFirst,
    2 => MetadataPlacementProfile.DataFirst,
    // Index 0 is the format default, which the optimizer expresses as null.
    _ => null,
  };

  private MetadataZone SelectedMetadataZone() => this._metadataZone.SelectedIndex switch {
    1 => MetadataZone.Front,
    2 => MetadataZone.Back,
    3 => MetadataZone.Middle,
    4 => MetadataZone.BeforeContent,
    _ => MetadataZone.Unchanged,
  };

  private static long ParseSize(string s) {
    if (string.IsNullOrWhiteSpace(s)) return 0;
    s = s.Trim().ToLowerInvariant();

    long mult = 1;
    if (s.EndsWith('k')) { mult = 1024L; s = s[..^1]; }
    else if (s.EndsWith('m')) { mult = 1024L * 1024; s = s[..^1]; }
    else if (s.EndsWith('g')) { mult = 1024L * 1024 * 1024; s = s[..^1]; }

    return long.TryParse(s, out var n) ? n * mult : 0;
  }

  private static long ParseHoleAt(string s)
    => string.IsNullOrWhiteSpace(s) || s.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
      ? -1
      : long.TryParse(s, out var n) ? n : -1;

  private static int ParseInterleaveStride(string s)
    => string.IsNullOrWhiteSpace(s) ? 1
      : int.TryParse(s.Trim(), out var n) ? Math.Clamp(n, 1, 256) : 1;

  private void Append(string line) {
    this._outputBox.AppendText(line + Environment.NewLine);
    this._outputBox.SelectionStart = this._outputBox.Text.Length;
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  // ── Mode probing ────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Asks each mode whether the descriptor accepts it, without mutating anything. There is no way
  /// to ask directly, so the descriptor is handed a stream that throws on first touch: reaching the
  /// I/O means the mode was accepted, while throwing <see cref="NotSupportedException"/> before any
  /// I/O means it was not.
  /// </summary>
  private static List<string> ProbeSupportedModes(IArchiveDefragmentable defragmentable) {
    var supported = new List<string>();

    foreach (var mode in Enum.GetValues<DefragMode>())
      try {
        using var probe = new ProbeStream();
        defragmentable.Defragment(probe, new DefragOptions { Mode = mode });
        supported.Add(mode.ToString()); // Unlikely: the probe should have thrown first.
      } catch (NotSupportedException) {
        // Mode unsupported by the descriptor.
      } catch (ProbeAbortException) {
        supported.Add(mode.ToString());
      } catch {
        // Anything else: assume unsupported rather than guess.
      }

    return supported;
  }

  private sealed class ProbeAbortException : Exception;

  private sealed class ProbeStream : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => 1024;
    public override long Position { get => 0; set => throw new ProbeAbortException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new ProbeAbortException();
    public override long Seek(long offset, SeekOrigin origin) => throw new ProbeAbortException();
    public override void SetLength(long value) => throw new ProbeAbortException();
    public override void Write(byte[] buffer, int offset, int count) => throw new ProbeAbortException();
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
      g.FillRectangle(theme.ControlBackground, new(0, 0, this.Width, this.Height));

      var x = 0;
      var y = 2;
      foreach (var (color, label) in Swatches) {
        var width = 10 + 4 + Math.Max(30, g.MeasureText(label, theme.DefaultFont).Width) + 12;
        if (x + width > this.Width) {
          x = 0;
          y += 18;
          if (y + 14 > this.Height) return;
        }

        g.FillRectangle(color, new(x, y + 3, 10, 10));
        g.DrawText(label, theme.DefaultFont, theme.ControlText, new(x + 14, y, width - 18, 16), ContentAlignment.MiddleLeft);
        x += width;
      }

      // The two heads are lines rather than swatches, matching how they are drawn on the map.
      if (x + 90 > this.Width) {
        x = 0;
        y += 18;
      }
      if (y + 14 > this.Height) return;

      g.DrawLine(Color.LimeGreen, x + 2, y + 2, x + 2, y + 14, 2);
      g.DrawText("Read head", theme.DefaultFont, theme.ControlText, new(x + 8, y, 70, 16), ContentAlignment.MiddleLeft);
      g.DrawLine(Color.Orange, x + 84, y + 2, x + 84, y + 14, 2);
      g.DrawText("Write head", theme.DefaultFont, theme.ControlText, new(x + 90, y, 74, 16), ContentAlignment.MiddleLeft);
    }
  }
}
