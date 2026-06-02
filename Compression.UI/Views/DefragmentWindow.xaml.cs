using System.Diagnostics;
using System.IO;
using System.Windows;
using Compression.Lib;
using Compression.Lib.Layout;
using Compression.Registry;
using Compression.Registry.Layout;
using Compression.UI.Controls;

namespace Compression.UI.Views;

/// <summary>
/// User-initiated defragmentation pass over a filesystem image. Shows the
/// detected format + capability, lets the user pick one of four layout
/// strategies (mirroring the CLI's <c>cwb defragment --mode</c> options),
/// and runs the descriptor's <see cref="IArchiveDefragmentable"/> path.
/// </summary>
public partial class DefragmentWindow : Window {

  private string? _imagePath;
  private IArchiveDefragmentable? _defragmentable;
  private IArchiveFormatOperations? _archiveOps;
  private bool _isArchiveMode;
  private bool _isFileInternalMode;
  private bool _isSevenZipFormat;
  private IFileInternalChunkMover? _chunkMover;
  private int _fileRowCount;
  private string _filesSortDescription = "listing order";
  private LayoutTemplate? _selectedLayoutProfile;

  public DefragmentWindow() {
    InitializeComponent();
    RefreshLayoutProfilesCombo();
  }

  /// <summary>
  /// Re-populates the <c>LayoutProfileCombo</c> from
  /// <see cref="LayoutProfileStore.List"/>. Called at startup and after the
  /// editor closes so newly created / renamed profiles surface immediately.
  /// </summary>
  private void RefreshLayoutProfilesCombo() {
    if (LayoutProfileCombo == null) return;
    var previousPath = (LayoutProfileCombo.SelectedItem as LayoutProfileComboItem)?.Entry?.FilePath;
    LayoutProfileCombo.Items.Clear();
    LayoutProfileCombo.Items.Add(new LayoutProfileComboItem(null) { Display = "(none)" });
    foreach (var entry in LayoutProfileStore.List())
      LayoutProfileCombo.Items.Add(new LayoutProfileComboItem(entry));

    // Try to preserve the user's previous pick across refreshes.
    if (!string.IsNullOrEmpty(previousPath)) {
      for (var i = 0; i < LayoutProfileCombo.Items.Count; i++) {
        if (LayoutProfileCombo.Items[i] is LayoutProfileComboItem li
            && string.Equals(li.Entry?.FilePath, previousPath, StringComparison.OrdinalIgnoreCase)) {
          LayoutProfileCombo.SelectedIndex = i;
          return;
        }
      }
    }
    LayoutProfileCombo.SelectedIndex = 0;
  }

  private void OnLayoutProfileChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    if (LayoutProfileCombo?.SelectedItem is not LayoutProfileComboItem item || item.Entry == null) {
      this._selectedLayoutProfile = null;
      return;
    }
    try {
      this._selectedLayoutProfile = LayoutProfileStore.Load(item.Entry);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Failed to load layout profile '{item.Entry.Name}':\n{ex.Message}",
        "Layout profile", MessageBoxButton.OK, MessageBoxImage.Error);
      this._selectedLayoutProfile = null;
      LayoutProfileCombo.SelectedIndex = 0;
    }
  }

  private void OnEditLayoutProfiles(object sender, RoutedEventArgs e) {
    var editor = new LayoutProfileEditor { Owner = this };
    editor.ShowDialog();
    RefreshLayoutProfilesCombo();
  }

  /// <summary>
  /// Row binding for the layout profile picker combo. <see cref="Entry"/>
  /// is <c>null</c> for the <c>(none)</c> sentinel item.
  /// </summary>
  private sealed class LayoutProfileComboItem(LayoutProfileEntry? entry) {
    public LayoutProfileEntry? Entry { get; } = entry;
    public string Display { get; set; } = entry == null
      ? "(none)"
      : $"{entry.Name} [{(entry.Origin == ProfileOrigin.Builtin ? "Built-in" : "User")}]";
    public override string ToString() => this.Display;
  }

  public DefragmentWindow(string preselectedImage) : this() {
    if (!string.IsNullOrEmpty(preselectedImage) && File.Exists(preselectedImage))
      LoadImage(preselectedImage);
  }

  private void OnBrowse(object sender, RoutedEventArgs e) {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Select filesystem image or archive",
      Filter = "Supported files|*.img;*.iso;*.d64;*.d71;*.d81;*.adf;*.dsk;*.po;*.atr;*.ssd;*.dsd;*.zip;*.7z;*.tar;*.lzh;*.arj|Filesystem images|*.img;*.iso;*.d64;*.d71;*.d81;*.adf;*.dsk;*.po;*.atr;*.ssd;*.dsd|Archives|*.zip;*.7z;*.tar;*.lzh;*.arj|All Files|*.*",
    };
    if (dlg.ShowDialog() != true) return;
    LoadImage(dlg.FileName);
  }

  private void LoadImage(string path) {
    this._imagePath = path;
    this._isArchiveMode = false;
    this._isFileInternalMode = false;
    this._isSevenZipFormat = false;
    this._archiveOps = null;
    this._chunkMover = null;
    ImagePathBox.Text = path;
    ImagePathBox.Foreground = System.Windows.Media.Brushes.Black;

    Compression.Lib.FormatRegistration.EnsureInitialized();
    var format = FormatDetector.Detect(path);
    FormatLbl.Text = format.ToString();

    var ops = FormatRegistry.GetArchiveOps(format.ToString());
    this._defragmentable = ops as IArchiveDefragmentable;

    // Determine whether this is an archive with layout-map support.
    var isArchiveLayout = ops is IArchiveLayoutMap;
    var isArchiveCreatable = ops is IArchiveCreatable;

    // File-internal layout (MP4 atoms, RIFF chunks, etc.)
    var isFileInternalLayout = ops is IFileInternalLayoutMap;
    var isFileInternalOptimizable = ops is IFileInternalChunkMover;

    if (this._defragmentable != null) {
      // FS defrag path (existing)
      this._isArchiveMode = false;
      var supported = ProbeSupportedModes(this._defragmentable);
      SupportLbl.Text = supported.Count >= 4
        ? "All four modes (in-place defragment)."
        : $"{string.Join(", ", supported)} (other modes throw NotSupported)";
      SupportLbl.Foreground = System.Windows.Media.Brushes.DarkGreen;
      RunBtn.Content = "Defragment";
      RunBtn.IsEnabled = true;
    } else if (isFileInternalLayout) {
      // File-internal chunk layout path (MP4 fast-start, RIFF, JPEG, etc.)
      this._isFileInternalMode = true;
      this._archiveOps = ops;
      this._chunkMover = ops as IFileInternalChunkMover;
      if (isFileInternalOptimizable) {
        SupportLbl.Text = "File-internal layout optimization (e.g. MP4 fast-start).";
        SupportLbl.Foreground = System.Windows.Media.Brushes.DarkGreen;
        RunBtn.IsEnabled = true;
      } else {
        SupportLbl.Text = "File-internal layout viewable but optimization not supported (read-only).";
        SupportLbl.Foreground = System.Windows.Media.Brushes.DarkOrange;
        RunBtn.IsEnabled = false;
      }
      RunBtn.Content = "Optimize";
    } else if (isArchiveLayout || isArchiveCreatable) {
      // Archive optimization path (new)
      this._isArchiveMode = true;
      this._isSevenZipFormat = format.ToString() == "SevenZip";
      this._archiveOps = ops;
      if (isArchiveCreatable) {
        SupportLbl.Text = "Archive optimization (extract + repack with optimal settings).";
        SupportLbl.Foreground = System.Windows.Media.Brushes.DarkGreen;
        RunBtn.IsEnabled = true;
      } else {
        SupportLbl.Text = "Archive layout viewable but format does not support creation (read-only).";
        SupportLbl.Foreground = System.Windows.Media.Brushes.DarkOrange;
        RunBtn.IsEnabled = false;
      }
      RunBtn.Content = "Optimize";
    } else {
      SupportLbl.Text = "Not supported by this format.";
      SupportLbl.Foreground = System.Windows.Media.Brushes.OrangeRed;
      RunBtn.Content = "Defragment";
      RunBtn.IsEnabled = false;
    }

    // Toggle mode-picker visibility: FS modes vs archive note.
    var showFsModes = !this._isArchiveMode && !this._isFileInternalMode;
    if (FsModesGroup != null)
      FsModesGroup.Visibility = showFsModes ? Visibility.Visible : Visibility.Collapsed;
    if (ArchiveRepackGroup != null)
      ArchiveRepackGroup.Visibility = (this._isArchiveMode || this._isFileInternalMode) ? Visibility.Visible : Visibility.Collapsed;
    if (SmartSolidRepackCheck != null)
      SmartSolidRepackCheck.Visibility = this._isSevenZipFormat ? Visibility.Visible : Visibility.Collapsed;
    if (MetadataPlacementPanel != null)
      MetadataPlacementPanel.Visibility = this._isFileInternalMode ? Visibility.Visible : Visibility.Collapsed;

    // Enable Shrink button for formats that support it
    var formatStr = format.ToString();
    var supportsShrink = formatStr is "Fat" or "Ext" or "Ext1" or "Vhd";
    if (ShrinkBtn != null)
      ShrinkBtn.IsEnabled = supportsShrink;

    // Enable Wipe Empty button for formats that support it (IWipeEmpty, IFilesystemExtentMap, or IArchiveLayoutMap)
    var supportsWipe = ops is IWipeEmpty || ops is IFilesystemExtentMap || ops is IArchiveLayoutMap;
    if (WipeEmptyBtn != null)
      WipeEmptyBtn.IsEnabled = supportsWipe;

    var fi = new FileInfo(path);
    SizeLbl.Text = $"{FormatSize(fi.Length)} ({fi.Length:N0} bytes)";

    // Pre-populate the block map with the current state so the user can see
    // what they're about to defragment/optimize.
    PreviewBlockMap(path, ops);
  }

  /// <summary>
  /// Builds an initial block-map snapshot for the loaded image. Two paths:
  /// <list type="bullet">
  ///   <item>If the descriptor implements <see cref="IFilesystemExtentMap"/>
  ///   (FAT, ext, ext1, D64), call <c>EnumerateExtents</c> to render the
  ///   <em>real on-disk layout</em> — every file's actual cluster/block runs
  ///   at their actual offsets, plus metadata-reserved regions, with gaps
  ///   filled as free. Status line announces "Real on-disk layout".</item>
  ///   <item>Otherwise fall back to a contiguous-from-zero approximation
  ///   driven by <c>List</c> output. Status line says "Approximate layout
  ///   (post-defrag preview)".</item>
  /// </list>
  ///
  /// <para>Classification: when entries expose a non-null
  /// <see cref="ArchiveEntryInfo.LastModified"/>, the Hot / Normal / Cold /
  /// Frozen tiles are computed from the actual mtime quartiles across the
  /// non-directory file set so the chart reflects real "thermal" zones.
  /// When all entries lack mtime, we fall back to listing-order quartile
  /// (the original proxy behavior) so the visualization still has some
  /// structure.</para>
  /// </summary>
  private void PreviewBlockMap(string path, IArchiveFormatOperations? ops) {
    BlockMap.BlockMap = null;
    BlockMap.ImageSize = 0;
    FilesGrid.ItemsSource = null;
    this._fileRowCount = 0;
    this._filesSortDescription = "listing order";
    FilesPanelStatus.Text = "—";
    if (LayoutStatusLbl != null) LayoutStatusLbl.Text = "—";
    if (ops == null) return;

    // Path 1: descriptor exposes a real extent map → use the honest layout.
    if (ops is IFilesystemExtentMap extentMap) {
      try {
        using var stream = File.OpenRead(path);
        if (TryRenderExtentMap(extentMap, stream, ops, path)) return;
      } catch {
        // Extent walker failed mid-stream — fall through to the contiguous
        // approximation so the user still sees something.
      }
    }

    // Path 1b: descriptor exposes an archive layout map → render byte-level layout.
    if (ops is IArchiveLayoutMap archiveLayout) {
      try {
        using var stream = File.OpenRead(path);
        if (TryRenderArchiveLayout(archiveLayout, stream, ops, path)) return;
      } catch {
        // Layout walker failed — fall through to the contiguous approximation.
      }
    }

    // Path 1c: file-internal chunk layout (MP4 atoms, RIFF chunks, etc.)
    if (ops is IFileInternalLayoutMap fileLayout) {
      try {
        using var stream = File.OpenRead(path);
        if (TryRenderFileInternalLayout(fileLayout, stream, path)) return;
      } catch {
        // Chunk walker failed — fall through to the contiguous approximation.
      }
    }

    if (LayoutStatusLbl != null)
      LayoutStatusLbl.Text = "Approximate layout (post-defrag preview)";
    try {
      using var stream = File.OpenRead(path);
      var entries = ops.List(stream, password: null);
      var imageSize = stream.Length;
      var map = new List<DefragBlockInfo>();
      var rows = new List<FileRow>();
      var offset = 0L;

      // Filter to the non-directory subset we'll actually plot. Track the
      // original index so we can preserve listing order for layout while
      // still classifying by mtime.
      var files = new List<(int Index, ArchiveEntryInfo Entry)>(entries.Count);
      for (var i = 0; i < entries.Count; i++) {
        var e = entries[i];
        if (!e.IsDirectory) files.Add((i, e));
      }

      // Compute mtime quartile thresholds across the file set, if any
      // entry exposes a LastModified. Three thresholds split the sorted
      // mtime list into four equal-count buckets: [Frozen | Cold | Normal | Hot].
      // Most-recent → Hot, oldest → Frozen.
      DateTime? hotThreshold = null, normalThreshold = null, coldThreshold = null;
      var mtimes = files.Where(f => f.Entry.LastModified.HasValue)
                        .Select(f => f.Entry.LastModified!.Value)
                        .OrderBy(static d => d)
                        .ToList();
      var useMtime = mtimes.Count > 0;
      if (useMtime) {
        // mtimes is ascending: oldest first. Quartile boundaries:
        //   index < 25%  → Frozen
        //   index < 50%  → Cold
        //   index < 75%  → Normal
        //   else         → Hot
        var n = mtimes.Count;
        coldThreshold = mtimes[Math.Min(n - 1, n / 4)];     // Frozen → Cold boundary
        normalThreshold = mtimes[Math.Min(n - 1, n / 2)];   // Cold   → Normal boundary
        hotThreshold = mtimes[Math.Min(n - 1, 3 * n / 4)];  // Normal → Hot boundary
      }

      var fileCount = files.Count;
      for (var k = 0; k < fileCount; k++) {
        var e = files[k].Entry;
        var size = Math.Max(1, e.OriginalSize);
        DefragBlockClass cls;
        if (useMtime && e.LastModified.HasValue) {
          var t = e.LastModified.Value;
          if (t < coldThreshold) cls = DefragBlockClass.Frozen;
          else if (t < normalThreshold) cls = DefragBlockClass.Cold;
          else if (t < hotThreshold) cls = DefragBlockClass.Normal;
          else cls = DefragBlockClass.Hot;
        } else if (useMtime) {
          // Mixed set: this entry has no mtime but others do. Treat it as
          // unknown-age → Frozen so it visibly stands out from the dated set.
          cls = DefragBlockClass.Frozen;
        } else {
          // No mtime info anywhere — fall back to listing-order quartile.
          cls = (k * 4 / Math.Max(1, fileCount)) switch {
            0 => DefragBlockClass.Hot,
            1 => DefragBlockClass.Normal,
            2 => DefragBlockClass.Cold,
            _ => DefragBlockClass.Frozen,
          };
        }
        map.Add(new DefragBlockInfo(offset, size, DefragBlockKind.Used, e.Name, cls));
        rows.Add(new FileRow {
          Name = e.Name,
          Size = e.OriginalSize,
          SizeDisplay = FormatSize(e.OriginalSize),
          // Fragment-count display requires a per-FS extent walk that
          // ArchiveEntryInfo doesn't expose today; show a placeholder.
          // TODO: clicking a row should highlight that file's tile range
          // on the block map.
          FragmentsDisplay = "—",
          Modified = e.LastModified,
          ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—",
          Class = cls.ToString(),
        });
        offset += size;
        if (offset >= imageSize) break;
      }
      if (offset < imageSize)
        map.Add(new DefragBlockInfo(offset, imageSize - offset, DefragBlockKind.Free));
      BlockMap.BlockMap = map;
      BlockMap.ImageSize = imageSize;
      // DataGrid row virtualization handles the visual side; the underlying
      // List<FileRow> is fine even at millions of rows because the grid
      // realises only the visible viewport.
      FilesGrid.ItemsSource = rows;
      this._fileRowCount = rows.Count;
      this._filesSortDescription = "listing order";
      UpdateFilesPanelStatus();
    } catch {
      // Preview is best-effort; if List fails, just leave the map empty.
    }
  }

  /// <summary>
  /// Cap on extent count fed to the block-map control. Real-world fragmented
  /// images can produce hundreds of thousands of small extents; binning
  /// adjacent same-kind regions keeps the rendered chart fluid.
  /// </summary>
  private const int MaxExtents = 50_000;

  /// <summary>
  /// Renders the real on-disk layout via <see cref="IFilesystemExtentMap.EnumerateExtents"/>.
  /// Sorts extents by offset, fills any gaps as <see cref="DefragBlockKind.Free"/>,
  /// then bins adjacent same-kind regions when the count exceeds
  /// <see cref="MaxExtents"/>. Also re-uses the descriptor's <c>List</c>
  /// output to populate the file-list side panel and (where extents per
  /// file are available) emit a fragment count.
  /// </summary>
  private bool TryRenderExtentMap(IFilesystemExtentMap extentMap, Stream stream,
      IArchiveFormatOperations ops, string path) {
    var imageSize = stream.Length;
    if (imageSize <= 0) return false;
    var rawExtents = extentMap.EnumerateExtents(stream).ToList();
    if (rawExtents.Count == 0) return false;

    // Sort + clip to image bounds.
    rawExtents.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
    var clipped = new List<DefragBlockInfo>(rawExtents.Count);
    foreach (var ex in rawExtents) {
      if (ex.Offset >= imageSize) continue;
      var len = ex.Length;
      if (ex.Offset + len > imageSize) len = imageSize - ex.Offset;
      if (len <= 0) continue;
      clipped.Add(ex with { Length = len });
    }
    if (clipped.Count == 0) return false;

    // Fill gaps with Free, dropping overlaps (a later extent at the same
    // offset wins — most readers can return overlapping ranges if a file
    // was appended into a location previously claimed by metadata).
    var filled = new List<DefragBlockInfo>(clipped.Count * 2);
    var cursor = 0L;
    foreach (var ex in clipped) {
      if (ex.Offset > cursor)
        filled.Add(new DefragBlockInfo(cursor, ex.Offset - cursor, DefragBlockKind.Free));
      else if (ex.Offset < cursor) {
        // Overlap: shrink current extent so it starts at cursor.
        if (ex.Offset + ex.Length <= cursor) continue;
        var trimmed = ex with {
          Length = ex.Length - (cursor - ex.Offset),
          Offset = cursor,
        };
        filled.Add(trimmed);
        cursor = trimmed.Offset + trimmed.Length;
        continue;
      }
      filled.Add(ex);
      cursor = ex.Offset + ex.Length;
    }
    if (cursor < imageSize)
      filled.Add(new DefragBlockInfo(cursor, imageSize - cursor, DefragBlockKind.Free));

    // Apply Hot/Normal/Cold/Frozen classification to Used extents using
    // mtimes from List(). We only need names → mtime; ignore directories.
    Dictionary<string, DateTime?>? mtimeByName = null;
    List<ArchiveEntryInfo>? entries = null;
    try {
      stream.Position = 0;
      entries = ops.List(stream, password: null);
      mtimeByName = entries.Where(e => !e.IsDirectory)
                           .GroupBy(e => e.Name)
                           .ToDictionary(g => g.Key, g => g.First().LastModified);
    } catch { /* best-effort */ }

    DateTime? hotThreshold = null, normalThreshold = null, coldThreshold = null;
    var useMtime = false;
    if (mtimeByName != null) {
      var mtimes = mtimeByName.Values.Where(v => v.HasValue).Select(v => v!.Value)
                                     .OrderBy(static d => d).ToList();
      useMtime = mtimes.Count > 0;
      if (useMtime) {
        var n = mtimes.Count;
        coldThreshold = mtimes[Math.Min(n - 1, n / 4)];
        normalThreshold = mtimes[Math.Min(n - 1, n / 2)];
        hotThreshold = mtimes[Math.Min(n - 1, 3 * n / 4)];
      }
    }

    var classified = new List<DefragBlockInfo>(filled.Count);
    foreach (var ex in filled) {
      if (ex.Kind != DefragBlockKind.Used || ex.FileName == null
          || mtimeByName == null || !mtimeByName.TryGetValue(ex.FileName, out var mt)) {
        classified.Add(ex);
        continue;
      }
      DefragBlockClass cls;
      if (useMtime && mt.HasValue) {
        var t = mt.Value;
        if (t < coldThreshold) cls = DefragBlockClass.Frozen;
        else if (t < normalThreshold) cls = DefragBlockClass.Cold;
        else if (t < hotThreshold) cls = DefragBlockClass.Normal;
        else cls = DefragBlockClass.Hot;
      } else
        cls = DefragBlockClass.Normal;
      classified.Add(ex with { Classification = cls });
    }

    // Bin to max-extents budget if needed: merge adjacent same-kind regions.
    var final = classified.Count > MaxExtents ? BinExtents(classified, MaxExtents) : classified;

    BlockMap.BlockMap = final;
    BlockMap.ImageSize = imageSize;

    // File panel rows: include a fragment count derived from the extent map.
    if (entries != null) {
      var fragCount = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var ex in classified)
        if (ex.Kind == DefragBlockKind.Used && ex.FileName != null) {
          fragCount.TryGetValue(ex.FileName, out var n);
          fragCount[ex.FileName] = n + 1;
        }

      var rows = new List<FileRow>(entries.Count);
      foreach (var e in entries) {
        if (e.IsDirectory) continue;
        var n = fragCount.TryGetValue(e.Name, out var fc) ? fc : 0;
        rows.Add(new FileRow {
          Name = e.Name,
          Size = e.OriginalSize,
          SizeDisplay = FormatSize(e.OriginalSize),
          FragmentsDisplay = n > 0 ? n.ToString("N0") : "—",
          Modified = e.LastModified,
          ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "—",
          Class = "—",
        });
      }
      FilesGrid.ItemsSource = rows;
      this._fileRowCount = rows.Count;
      this._filesSortDescription = "listing order";
      UpdateFilesPanelStatus();
    }

    if (LayoutStatusLbl != null) {
      var fragmentedFiles = entries == null ? 0 :
        classified.Where(ex => ex.Kind == DefragBlockKind.Used && ex.FileName != null)
                  .GroupBy(ex => ex.FileName!).Count(g => g.Count() > 1);
      LayoutStatusLbl.Text = fragmentedFiles > 0
        ? $"Real on-disk layout — {classified.Count(ex => ex.Kind == DefragBlockKind.Used):N0} extents, {fragmentedFiles:N0} fragmented file(s)"
        : $"Real on-disk layout — {classified.Count(ex => ex.Kind == DefragBlockKind.Used):N0} extents (no fragmentation detected)";
    }
    return true;
  }

  /// <summary>
  /// Renders the real byte-level layout of an archive via
  /// <see cref="IArchiveLayoutMap.EnumerateLayout"/>. Classifies entries
  /// by compression method: Deflate/Normal (blue), LZMA/Hot (orange),
  /// Store/Frozen (gray), BZip2/Cold (green). Populates the file-list
  /// side panel with per-entry codec in the "Method" column.
  /// </summary>
  private bool TryRenderArchiveLayout(IArchiveLayoutMap archiveLayout, Stream stream,
      IArchiveFormatOperations? ops, string path) {
    var imageSize = stream.Length;
    if (imageSize <= 0) return false;
    var rawExtents = archiveLayout.EnumerateLayout(stream).ToList();
    if (rawExtents.Count == 0) return false;

    // Sort + clip to image bounds.
    rawExtents.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
    var clipped = new List<DefragBlockInfo>(rawExtents.Count);
    foreach (var ex in rawExtents) {
      if (ex.Offset >= imageSize) continue;
      var len = ex.Length;
      if (ex.Offset + len > imageSize) len = imageSize - ex.Offset;
      if (len <= 0) continue;
      clipped.Add(ex with { Length = len });
    }
    if (clipped.Count == 0) return false;

    // Fill gaps with Free, handle overlaps.
    var filled = new List<DefragBlockInfo>(clipped.Count * 2);
    var cursor = 0L;
    foreach (var ex in clipped) {
      if (ex.Offset > cursor)
        filled.Add(new DefragBlockInfo(cursor, ex.Offset - cursor, DefragBlockKind.Free));
      else if (ex.Offset < cursor) {
        if (ex.Offset + ex.Length <= cursor) continue;
        var trimmed = ex with {
          Length = ex.Length - (cursor - ex.Offset),
          Offset = cursor,
        };
        filled.Add(trimmed);
        cursor = trimmed.Offset + trimmed.Length;
        continue;
      }
      filled.Add(ex);
      cursor = ex.Offset + ex.Length;
    }
    if (cursor < imageSize)
      filled.Add(new DefragBlockInfo(cursor, imageSize - cursor, DefragBlockKind.Free));

    // Classify Used extents by compression method. We use the entry method
    // from List() to determine colors: Deflate → Normal/blue, LZMA → Hot/orange,
    // Store → Frozen/gray, BZip2 → Cold/green, others → Normal.
    Dictionary<string, string>? methodByName = null;
    List<ArchiveEntryInfo>? entries = null;
    try {
      stream.Position = 0;
      entries = ops?.List(stream, password: null);
      if (entries != null)
        methodByName = entries.Where(e => !e.IsDirectory)
                              .GroupBy(e => e.Name)
                              .ToDictionary(g => g.Key, g => g.First().Method ?? "");
    } catch { /* best-effort */ }

    var classified = new List<DefragBlockInfo>(filled.Count);
    foreach (var ex in filled) {
      if (ex.Kind != DefragBlockKind.Used || ex.FileName == null
          || methodByName == null || !methodByName.TryGetValue(ex.FileName, out var method)) {
        classified.Add(ex);
        continue;
      }
      var cls = ClassifyByMethod(method);
      classified.Add(ex with { Classification = cls });
    }

    // Bin to max-extents budget if needed.
    var final = classified.Count > MaxExtents ? BinExtents(classified, MaxExtents) : classified;

    BlockMap.BlockMap = final;
    BlockMap.ImageSize = imageSize;

    // File panel rows with Method column.
    if (entries != null) {
      var rows = new List<FileRow>(entries.Count);
      foreach (var e in entries) {
        if (e.IsDirectory) continue;
        var cls = ClassifyByMethod(e.Method ?? "");
        rows.Add(new FileRow {
          Name = e.Name,
          Size = e.OriginalSize,
          SizeDisplay = FormatSize(e.OriginalSize),
          FragmentsDisplay = "1",
          MethodDisplay = e.Method ?? "",
          Modified = e.LastModified,
          ModifiedDisplay = e.LastModified is { } dt ? dt.ToString("yyyy-MM-dd HH:mm") : "",
          Class = cls.ToString(),
        });
      }
      FilesGrid.ItemsSource = rows;
      this._fileRowCount = rows.Count;
      this._filesSortDescription = "listing order";
      UpdateFilesPanelStatus();
    }

    if (LayoutStatusLbl != null) {
      var entryCount = classified.Count(ex => ex.Kind == DefragBlockKind.Used);
      var freeBytes = classified.Where(ex => ex.Kind == DefragBlockKind.Free).Sum(ex => ex.Length);
      LayoutStatusLbl.Text = freeBytes > 0
        ? $"Real archive layout — {entryCount:N0} regions, {FormatSize(freeBytes)} wasted"
        : $"Real archive layout — {entryCount:N0} regions (tightly packed)";
    }
    return true;
  }

  /// <summary>
  /// Renders the internal chunk layout of a single file (MP4 atoms, RIFF
  /// chunks, etc.) via <see cref="IFileInternalLayoutMap.EnumerateChunks"/>.
  /// Each chunk maps to a <see cref="DefragBlockInfo"/> tile in the chart.
  /// </summary>
  private bool TryRenderFileInternalLayout(IFileInternalLayoutMap fileLayout, Stream stream, string path) {
    var imageSize = stream.Length;
    if (imageSize <= 0) return false;
    var chunks = fileLayout.EnumerateChunks(stream).ToList();
    if (chunks.Count == 0) return false;

    // Sort by offset and fill gaps with Free.
    chunks.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
    var filled = new List<DefragBlockInfo>(chunks.Count * 2);
    var cursor = 0L;
    foreach (var ch in chunks) {
      if (ch.Offset > cursor)
        filled.Add(new DefragBlockInfo(cursor, ch.Offset - cursor, DefragBlockKind.Free));
      else if (ch.Offset < cursor) {
        if (ch.Offset + ch.Length <= cursor) continue;
        var trimmed = ch with {
          Length = ch.Length - (cursor - ch.Offset),
          Offset = cursor,
        };
        filled.Add(trimmed);
        cursor = trimmed.Offset + trimmed.Length;
        continue;
      }
      filled.Add(ch);
      cursor = ch.Offset + ch.Length;
    }
    if (cursor < imageSize)
      filled.Add(new DefragBlockInfo(cursor, imageSize - cursor, DefragBlockKind.Free));

    BlockMap.BlockMap = filled;
    BlockMap.ImageSize = imageSize;

    // File panel: show each chunk as a row.
    var rows = new List<FileRow>(filled.Count);
    foreach (var ch in filled) {
      rows.Add(new FileRow {
        Name = ch.FileName ?? ch.Kind.ToString(),
        Size = ch.Length,
        SizeDisplay = FormatSize(ch.Length),
        FragmentsDisplay = "1",
        MethodDisplay = ch.Kind.ToString(),
        Class = ch.Classification?.ToString() ?? "—",
      });
    }
    FilesGrid.ItemsSource = rows;
    this._fileRowCount = rows.Count;
    this._filesSortDescription = "listing order";
    UpdateFilesPanelStatus();

    if (LayoutStatusLbl != null) {
      var chunkCount = filled.Count(c => c.Kind != DefragBlockKind.Free);
      LayoutStatusLbl.Text = $"File-internal layout — {chunkCount:N0} chunks";
    }
    return true;
  }

  /// <summary>
  /// Maps a compression method name to a thermal classification for
  /// color-coding archive entries in the block chart.
  /// </summary>
  private static DefragBlockClass ClassifyByMethod(string method) {
    var m = method.ToUpperInvariant();
    if (m.Contains("STORE") || m.Contains("COPY") || m == "NONE" || m == "")
      return DefragBlockClass.Frozen;  // gray — uncompressed
    if (m.Contains("LZMA") || m.Contains("PPMD") || m.Contains("BZIP"))
      return DefragBlockClass.Hot;     // orange — heavy compression
    if (m.Contains("BZIP2") || m.Contains("BZ2") || m.Contains("ZSTD"))
      return DefragBlockClass.Cold;    // green — modern/alternative
    // Deflate, LZH, ARJ, and others → Normal/blue
    return DefragBlockClass.Normal;
  }

  /// <summary>
  /// Bins an over-sized extent list down to <paramref name="maxCount"/>
  /// entries by greedily merging adjacent same-kind extents. Used when an
  /// honest extent walk produces &gt;50k entries on a large fragmented image.
  /// </summary>
  private static List<DefragBlockInfo> BinExtents(List<DefragBlockInfo> input, int maxCount) {
    if (input.Count <= maxCount) return input;
    // First pass: merge adjacent extents with identical Kind. This collapses
    // small fragments of the same type into a single tile without losing
    // visual fidelity. If still over budget, fall back to size-based binning.
    var merged = new List<DefragBlockInfo>(input.Count);
    foreach (var ex in input) {
      if (merged.Count == 0) { merged.Add(ex); continue; }
      var last = merged[^1];
      if (last.Kind == ex.Kind && last.Offset + last.Length == ex.Offset
          && last.FileName == ex.FileName) {
        merged[^1] = last with { Length = last.Length + ex.Length };
      } else {
        merged.Add(ex);
      }
    }
    if (merged.Count <= maxCount) return merged;

    // Still over budget: collapse runs of small adjacent extents regardless
    // of kind, picking the dominant kind by length within each bin. We
    // partition the byte-space into maxCount equal-sized bins.
    var imageSize = merged[^1].Offset + merged[^1].Length;
    var bytesPerBin = (double)imageSize / maxCount;
    var binned = new List<DefragBlockInfo>(maxCount);
    var binStart = 0L;
    var binIdx = 0;
    var binKind = DefragBlockKind.Free;
    long binBestLen = 0;
    string? binBestName = null;
    foreach (var ex in merged) {
      while (ex.Offset + ex.Length > binStart + bytesPerBin && binIdx < maxCount - 1) {
        if (ex.Length > binBestLen) {
          binBestLen = ex.Length;
          binKind = ex.Kind;
          binBestName = ex.FileName;
        }
        binned.Add(new DefragBlockInfo(binStart, (long)bytesPerBin, binKind, binBestName));
        binStart += (long)bytesPerBin;
        binIdx++;
        binKind = DefragBlockKind.Free;
        binBestLen = 0;
        binBestName = null;
      }
      if (ex.Length > binBestLen) {
        binBestLen = ex.Length;
        binKind = ex.Kind;
        binBestName = ex.FileName;
      }
    }
    binned.Add(new DefragBlockInfo(binStart, imageSize - binStart, binKind, binBestName));
    return binned;
  }

  /// <summary>
  /// Refreshes the "X files · sorted by Y" status line above the file
  /// grid so users have an explicit row-count + ordering signal even on
  /// very large filesystems where scrolling-by-row gives no scale cue.
  /// </summary>
  private void UpdateFilesPanelStatus() {
    if (FilesPanelStatus == null) return;
    if (this._fileRowCount <= 0) {
      FilesPanelStatus.Text = "—";
      return;
    }
    var countLabel = this._fileRowCount == 1 ? "1 file" : $"{this._fileRowCount:N0} files";
    FilesPanelStatus.Text = $"{countLabel} · sorted by {this._filesSortDescription}";
  }

  /// <summary>
  /// Track the current DataGrid sort so we can surface it to the user in
  /// <see cref="UpdateFilesPanelStatus"/>. We do not preempt or override
  /// the grid's default sort behaviour — we only observe it.
  /// </summary>
  private void OnFilesGridSorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e) {
    var col = e.Column;
    if (col == null) return;
    var header = col.Header?.ToString() ?? "—";
    var nextDir = col.SortDirection == System.ComponentModel.ListSortDirection.Ascending
      ? System.ComponentModel.ListSortDirection.Descending
      : System.ComponentModel.ListSortDirection.Ascending;
    var arrow = nextDir == System.ComponentModel.ListSortDirection.Ascending ? "↑" : "↓";
    this._filesSortDescription = $"{header} {arrow}";
    // Defer the status refresh so the sort actually applies first; harmless
    // ordering, but the label feels right when it updates after the grid does.
    Dispatcher.BeginInvoke(new Action(UpdateFilesPanelStatus));
  }

  /// <summary>
  /// Handles a single-click on a tile in the block map. Opens a modeless
  /// drill-down popup listing every <see cref="DefragBlockInfo"/> whose
  /// range intersects the clicked tile. We deliberately keep the popup
  /// modeless (Show, not ShowDialog) so the user can keep clicking other
  /// tiles to compare contents — each click re-targets the same window if
  /// one is already open.
  /// </summary>
  private TileContentsWindow? _tileContentsWindow;

  private void OnBlockMapTileClicked(object sender, TileClickedEventArgs e) {
    var win = this._tileContentsWindow;
    if (win == null || !win.IsLoaded) {
      win = new TileContentsWindow { Owner = this };
      win.Closed += (_, _) => this._tileContentsWindow = null;
      this._tileContentsWindow = win;
    }
    win.SetContents(e.StartOffset, e.EndOffset, e.Contents);
    if (!win.IsVisible) win.Show();
    else win.Activate();
  }

  private void OnFilesPanelToggle(object sender, RoutedEventArgs e) {
    if (FilesPanelGroup == null || FilesPanelSplitter == null || FilesPanelColumn == null)
      return;
    var visible = FilesPanelToggle.IsChecked == true;
    FilesPanelGroup.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    FilesPanelSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    FilesPanelColumn.Width = visible
      ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
      : new System.Windows.GridLength(0);
    FilesPanelColumn.MinWidth = visible ? 220 : 0;
  }

  /// <summary>
  /// Row model for the file-list panel. Mirrors what Defraggler/UltraDefrag
  /// show: name, size, fragment count, last-modified date and a hot/cold
  /// classification. Fragment count is a placeholder for now — a real
  /// per-FS extent walk would need to plumb new metadata through
  /// <see cref="IArchiveFormatOperations"/>.
  /// </summary>
  private sealed class FileRow {
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
  /// Runs each of the four <see cref="DefragMode"/> values through a probe
  /// stream that throws on first read so we don't actually mutate the image
  /// — we only want to know whether the descriptor's overload accepts the
  /// mode at all (i.e. didn't throw <see cref="NotSupportedException"/> at
  /// the dispatch boundary).
  /// </summary>
  private static List<string> ProbeSupportedModes(IArchiveDefragmentable defragmentable) {
    var supported = new List<string>();
    foreach (var mode in Enum.GetValues<DefragMode>()) {
      try {
        using var probe = new ProbeStream();
        defragmentable.Defragment(probe, new DefragOptions { Mode = mode });
        supported.Add(mode.ToString()); // unlikely — probe should throw before completion
      } catch (NotSupportedException) {
        // Mode unsupported by descriptor — don't add.
      } catch (ProbeAbortException) {
        // Descriptor started reading: it accepts the mode.
        supported.Add(mode.ToString());
      } catch {
        // Any other exception: assume unsupported (safer than guessing).
      }
    }
    return supported;
  }

  private void OnModeChanged(object sender, RoutedEventArgs e) {
    if (CarveOptsGroup == null) return; // not yet inflated
    CarveOptsGroup.Visibility = ModeCarveHole != null && ModeCarveHole.IsChecked == true
      ? Visibility.Visible
      : Visibility.Collapsed;
  }

  /// <summary>Switches the block map between the linear grid, the 2-D circular
  /// platter, and the 3-D cylinder-stack projection.</summary>
  private void OnBlockMapViewModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    if (BlockMap == null || BlockMapViewCombo == null) return; // not yet inflated
    BlockMap.ViewMode = BlockMapViewCombo.SelectedIndex switch {
      1 => Compression.Core.Layout.BlockMapView.CircularPlatter,
      2 => Compression.Core.Layout.BlockMapView.CylinderStack,
      _ => Compression.Core.Layout.BlockMapView.LinearBlocks,
    };
  }

  private void OnRun(object sender, RoutedEventArgs e) {
    if (this._imagePath == null) return;

    if (this._isFileInternalMode) {
      OnRunFileInternalOptimize();
      return;
    }

    if (this._isArchiveMode) {
      OnRunArchiveOptimize();
      return;
    }

    if (this._defragmentable == null) return;

    var mode = SelectedMode();
    var holeSize = mode == DefragMode.CarveHole ? ParseSize(HoleSizeBox.Text) : 0L;
    var holeAt = mode == DefragMode.CarveHole ? ParseHoleAt(HoleAtBox.Text) : -1L;
    var interleaveStride = ParseInterleaveStride(InterleaveStrideBox.Text);
    var metadataZone = SelectedMetadataZone();
    var layoutProfile = this._selectedLayoutProfile;

    var path = this._imagePath;
    var defragmentable = this._defragmentable;

    Append($"=== {DateTime.Now:HH:mm:ss}  Defragmenting {Path.GetFileName(path)} ===");
    Append($"Mode: {mode}");
    if (interleaveStride > 1)
      Append($"Block interleave: {interleaveStride}");
    if (metadataZone != MetadataZone.Unchanged)
      Append($"Metadata zone: {metadataZone}");
    if (layoutProfile != null)
      Append($"Layout profile: {layoutProfile.Name} ({layoutProfile.Zones.Count} zone(s))");
    if (mode == DefragMode.CarveHole) {
      Append($"Hole size: {holeSize:N0} bytes");
      Append($"Hole at: {(holeAt < 0 ? "auto (end)" : holeAt.ToString("N0"))}");
    }

    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var origSize = new FileInfo(path).Length;
      string? lastCompleteStatus = null;

      // Bridge progress events from background thread to UI dispatcher.
      // We marshal each event onto the UI thread to update tile colors and
      // read/write head positions in real time.
      void OnProgress(DefragProgressEvent ev) {
        if (ev.Phase == "complete" && !string.IsNullOrEmpty(ev.Status))
          lastCompleteStatus = ev.Status;
        Dispatcher.BeginInvoke(() => {
          if (ev.BlockMap != null) {
            BlockMap.BlockMap = ev.BlockMap;
            BlockMap.ImageSize = ev.ImageSize;
          }
          BlockMap.ReadHead = ev.CurrentReadOffset;
          BlockMap.WriteHead = ev.CurrentWriteOffset;
          if (ev.Fraction >= 0)
            Progress.Value = Math.Clamp(ev.Fraction, 0, 1) * 100;
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
        });
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();
      var newSize = File.Exists(path) ? new FileInfo(path).Length : 0L;

      Dispatcher.Invoke(() => {
        Progress.Value = 100;
        RunBtn.IsEnabled = true;
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;
        if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else {
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          Append($"Image size: {origSize:N0} -> {newSize:N0} bytes (Δ {newSize - origSize:+#,#;-#,#;0})");
          if (!string.IsNullOrEmpty(lastCompleteStatus))
            Append($"Status: {lastCompleteStatus}");
          if (mode == DefragMode.ConsolidateAtEnd)
            Append("Tip: image byte size doesn't change for in-place end-pack — see the block chart for the new layout.");
        }
        Append("");
      });
    });
  }

  /// <summary>
  /// Runs the archive optimization path: extract all entries to a temp
  /// directory, then re-create the archive with optimal compression
  /// settings using <see cref="Compression.Lib.ArchiveOperations.Optimize"/>.
  /// Refreshes the block chart after completion to show the new layout.
  /// </summary>
  private void OnRunArchiveOptimize() {
    if (this._isSevenZipFormat && SmartSolidRepackCheck?.IsChecked == true) {
      OnRunSmartSolidRepack();
      return;
    }

    var path = this._imagePath!;
    var ops = this._archiveOps;

    Append($"=== {DateTime.Now:HH:mm:ss}  Optimizing {Path.GetFileName(path)} ===");

    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var origSize = new FileInfo(path).Length;
      long newSize = 0;
      var entriesOptimized = 0;

      try {
        // Use ArchiveOperations.Optimize which handles ZIP, Gzip, Zlib,
        // compound tar, and other stream formats with best-effort recompression.
        var tempOut = path + ".opt.tmp";
        try {
          var result = Compression.Lib.ArchiveOperations.Optimize(path, tempOut, password: null);
          newSize = result.OptimizedSize;
          entriesOptimized = result.EntriesOptimized;

          // Replace original with optimized version.
          File.Delete(path);
          File.Move(tempOut, path);
        } finally {
          if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
        }

        // Report progress midpoint.
        Dispatcher.BeginInvoke(() => Progress.Value = 80);
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();
      if (newSize == 0 && File.Exists(path)) newSize = new FileInfo(path).Length;

      Dispatcher.Invoke(() => {
        Progress.Value = 100;
        RunBtn.IsEnabled = true;
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;
        if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else {
          var delta = newSize - origSize;
          var pct = origSize > 0 ? (double)delta / origSize * 100 : 0;
          Append($"OK ({sw.ElapsedMilliseconds} ms) — {entriesOptimized} entries re-encoded");
          Append($"Archive size: {origSize:N0} -> {newSize:N0} bytes (Δ {delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");
        }
        Append("");

        // Refresh the block chart to show the new layout.
        PreviewBlockMap(path, ops);
      });
    });
  }

  /// <summary>
  /// Runs the 7z smart solid-block optimizer: tries multiple file grouping
  /// strategies and picks the one that produces the smallest archive.
  /// Shows per-strategy progress in the output log.
  /// </summary>
  private void OnRunSmartSolidRepack() {
    var path = this._imagePath!;
    var ops = this._archiveOps;

    Append($"=== {DateTime.Now:HH:mm:ss}  Smart solid-block repack: {Path.GetFileName(path)} ===");

    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var origSize = new FileInfo(path).Length;
      FileFormat.SevenZip.SolidBlockOptimizer.OptimizeResult? optimizeResult = null;

      try {
        using var fs = File.OpenRead(path);
        optimizeResult = FileFormat.SevenZip.SolidBlockOptimizer.Optimize(fs, maxTrials: 5,
          onProgress: (index, total, name) => {
            Dispatcher.BeginInvoke(() => {
              Progress.Value = (double)index / total * 100;
              Append($"  Trying strategy {index + 1}/{total}: {name}...");
            });
          });
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();

      Dispatcher.Invoke(() => {
        Progress.Value = 100;
        RunBtn.IsEnabled = true;
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;

        if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else if (optimizeResult != null) {
          // Report all trial results
          foreach (var trial in optimizeResult.Trials)
            Append($"    {trial.StrategyName}: {FormatSize(trial.OutputSize)} ({trial.Elapsed.TotalMilliseconds:F0} ms)");

          var newSize = (long)optimizeResult.Data.Length;
          var delta = newSize - origSize;
          var pct = origSize > 0 ? (double)delta / origSize * 100 : 0;
          Append($"  Winner: {optimizeResult.WinningStrategy}");
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          Append($"Archive size: {origSize:N0} -> {newSize:N0} bytes ({delta:+#,#;-#,#;0}, {pct:+0.0;-0.0;0.0}%)");

          // Write the winning archive if it's smaller. Use atomic rename so
          // a crash mid-write can't corrupt the source archive.
          if (newSize < origSize) {
            try {
              Compression.Lib.AtomicFileWriter.WriteAllBytesAtomic(path, optimizeResult.Data);
              Append("Optimized archive written.");
            } catch (Exception writeEx) {
              Append($"WARNING: Could not write optimized archive: {writeEx.Message}");
            }
          } else {
            Append("No strategy improved on the original size; archive unchanged.");
          }
        }
        Append("");

        // Refresh the block chart to show the new layout.
        PreviewBlockMap(path, ops);
      });
    });
  }

  /// <summary>
  /// Runs the file-internal optimization path: calls
  /// <see cref="IFileInternalChunkMover.Optimize"/> to rearrange internal
  /// chunks (e.g. MP4 fast-start). Refreshes the block chart after completion.
  /// </summary>
  private void OnRunFileInternalOptimize() {
    var path = this._imagePath!;
    var chunkMover = this._chunkMover;
    var ops = this._archiveOps;
    if (chunkMover == null) return;

    var placementProfile = SelectedMetadataPlacement();

    Append($"=== {DateTime.Now:HH:mm:ss}  Optimizing {Path.GetFileName(path)} (file-internal) ===");
    if (placementProfile != null)
      Append($"Metadata placement: {placementProfile.Name}");

    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = true;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var origSize = new FileInfo(path).Length;

      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        chunkMover.Optimize(stream, placementProfile);
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();
      var newSize = File.Exists(path) ? new FileInfo(path).Length : 0L;

      Dispatcher.Invoke(() => {
        Progress.IsIndeterminate = false;
        Progress.Value = 100;
        RunBtn.IsEnabled = true;
        BlockMap.ReadHead = -1;
        BlockMap.WriteHead = -1;
        if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else {
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          Append($"File size: {origSize:N0} -> {newSize:N0} bytes (Δ {newSize - origSize:+#,#;-#,#;0})");
        }
        Append("");

        // Refresh the block chart to show the new layout.
        PreviewBlockMap(path, ops);
      });
    });
  }

  private DefragMode SelectedMode() {
    if (ModePackEnd.IsChecked == true) return DefragMode.ConsolidateAtEnd;
    if (ModeFillHoles.IsChecked == true) return DefragMode.FillHolesLazy;
    if (ModeCarveHole.IsChecked == true) return DefragMode.CarveHole;
    return DefragMode.ConsolidateAtStart;
  }

  private static long ParseSize(string s) {
    if (string.IsNullOrWhiteSpace(s)) return 0;
    s = s.Trim().ToLowerInvariant();
    long mult = 1;
    if (s.EndsWith('k')) { mult = 1024L; s = s[..^1]; }
    else if (s.EndsWith('m')) { mult = 1024L * 1024; s = s[..^1]; }
    else if (s.EndsWith('g')) { mult = 1024L * 1024 * 1024; s = s[..^1]; }
    return long.TryParse(s, out var n) ? n * mult : 0;
  }

  private static long ParseHoleAt(string s) {
    if (string.IsNullOrWhiteSpace(s) || s.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
      return -1;
    return long.TryParse(s, out var n) ? n : -1;
  }

  private static int ParseInterleaveStride(string s) {
    if (string.IsNullOrWhiteSpace(s)) return 1;
    return int.TryParse(s.Trim(), out var n) ? Math.Clamp(n, 1, 256) : 1;
  }

  private void Append(string line) {
    OutputBox.AppendText(line + Environment.NewLine);
    OutputBox.ScrollToEnd();
  }

  private void OnMetadataPlacementChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    // No immediate action needed — the selection is read when the user clicks Run.
  }

  private MetadataPlacementProfile? SelectedMetadataPlacement() {
    if (MetadataPlacementCombo == null) return null;
    return MetadataPlacementCombo.SelectedIndex switch {
      1 => MetadataPlacementProfile.MetadataFirst,
      2 => MetadataPlacementProfile.DataFirst,
      _ => null, // 0 = Format default (null = use optimizer's default)
    };
  }

  private MetadataZone SelectedMetadataZone() {
    if (MetadataZoneCombo == null) return MetadataZone.Unchanged;
    return MetadataZoneCombo.SelectedIndex switch {
      1 => MetadataZone.Front,
      2 => MetadataZone.Back,
      3 => MetadataZone.Middle,
      4 => MetadataZone.BeforeContent,
      _ => MetadataZone.Unchanged,
    };
  }

  /// <summary>
  /// Runs the shrink operation: defragment + truncate trailing free space. Shows
  /// before/after image sizes in the output log. For FAT/ext images, uses the
  /// dedicated ShrinkHelper; for VHD, compacts the container.
  /// </summary>
  private void OnShrink(object sender, RoutedEventArgs e) {
    if (this._imagePath == null) return;
    var path = this._imagePath;
    // Capture FormatLbl.Text on the UI thread before Task.Run — accessing the
    // TextBlock from the worker thread throws the WPF "thread that owns the
    // object" exception (same pattern already used for `ops` above).
    var formatId = FormatLbl.Text;
    var ops = FormatRegistry.GetArchiveOps(formatId);

    Append($"=== {DateTime.Now:HH:mm:ss}  Shrinking {Path.GetFileName(path)} ===");

    ShrinkBtn.IsEnabled = false;
    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var origSize = new FileInfo(path).Length;
      long newSize = 0;
      var summary = "";

      try {
        if (formatId == "Fat") {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          Dispatcher.BeginInvoke(() => Progress.Value = 20);
          var result = FileSystem.Fat.FatShrinkHelper.Shrink(stream);
          newSize = result.NewSize;
          summary = result.WasReduced
            ? $"Reduced: {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)}"
            : "No reduction (image was already compact)";
        } else if (formatId is "Ext" or "Ext1") {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          Dispatcher.BeginInvoke(() => Progress.Value = 20);
          var result = FileSystem.Ext.ExtShrinkHelper.Shrink(stream);
          newSize = result.NewSize;
          summary = result.WasReduced
            ? $"Reduced: {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)}"
            : "No reduction (image was already compact)";
        } else if (formatId == "Vhd") {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          Dispatcher.BeginInvoke(() => Progress.Value = 20);
          var result = FileFormat.Vhd.VhdCompactor.Compact(stream);
          newSize = result.NewSize;
          summary = result.WasReduced
            ? $"Compacted: {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)} ({result.BlocksFreed} blocks freed)"
            : "No reduction (already compact)";
        } else {
          throw new NotSupportedException($"Shrink not supported for format: {formatId}");
        }
      } catch (Exception ex) {
        err = ex;
      }

      sw.Stop();
      if (newSize == 0 && File.Exists(path)) newSize = new FileInfo(path).Length;

      Dispatcher.Invoke(() => {
        Progress.Value = 100;
        ShrinkBtn.IsEnabled = true;
        RunBtn.IsEnabled = true;
        if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else {
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          Append($"  {summary}");
          SizeLbl.Text = $"{FormatSize(newSize)} ({newSize:N0} bytes)";
        }
        Append("");
        PreviewBlockMap(path, ops);
      });
    });
  }

  /// <summary>
  /// Runs the wipe-empty operation: zeros all unused space in the image or archive.
  /// Shows bytes wiped and percentage of image in the output log.
  /// </summary>
  private void OnWipeEmpty(object sender, RoutedEventArgs e) {
    if (this._imagePath == null) return;
    var path = this._imagePath;
    var formatStr = FormatLbl.Text;
    var ops = FormatRegistry.GetArchiveOps(formatStr);

    Append($"=== {DateTime.Now:HH:mm:ss}  Wiping unused space in {Path.GetFileName(path)} ===");

    WipeEmptyBtn.IsEnabled = false;
    ShrinkBtn.IsEnabled = false;
    RunBtn.IsEnabled = false;
    Progress.IsIndeterminate = false;
    Progress.Value = 0;

    Task.Run(() => {
      var sw = Stopwatch.StartNew();
      Exception? err = null;
      var origSize = new FileInfo(path).Length;
      long wiped = 0;
      // Total span of unused bytes (free space + cluster tips). May be -1 if
      // the format can't expose its extent map separately from the wipe.
      var totalUnused = -1L;

      try {
        Dispatcher.BeginInvoke(() => Progress.Value = 20);

        if (ops is IWipeEmpty wiper) {
          using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
          // Compute total unused upfront when the same descriptor also exposes
          // an extent/layout map. The wiper itself only reports bytes it had
          // to overwrite — on a mostly-empty image those two figures diverge
          // dramatically and the user perceives the smaller one as "the image
          // is barely empty", which is wrong.
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
          throw new NotSupportedException($"Format {formatStr} does not support wipe-empty.");
        }

        Dispatcher.BeginInvoke(() => Progress.Value = 80);
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();

      Dispatcher.Invoke(() => {
        Progress.Value = 100;
        WipeEmptyBtn.IsEnabled = true;
        ShrinkBtn.IsEnabled = ShrinkBtn.IsEnabled; // restore original state on next LoadImage
        RunBtn.IsEnabled = true;
        if (err != null) {
          Append($"FAILED ({sw.ElapsedMilliseconds} ms): {err.GetType().Name}: {err.Message}");
        } else {
          var writtenPct = origSize > 0 ? 100.0 * wiped / origSize : 0;
          Append($"OK ({sw.ElapsedMilliseconds} ms)");
          if (totalUnused >= 0) {
            var unusedPct = origSize > 0 ? 100.0 * totalUnused / origSize : 0;
            var alreadyZero = Math.Max(0, totalUnused - wiped);
            Append($"  Unused space: {FormatSize(totalUnused)} ({unusedPct:F1}% of image)");
            Append($"  Newly zeroed: {FormatSize(wiped)} ({wiped:N0} bytes); {FormatSize(alreadyZero)} was already zero");
          } else {
            Append($"  Wiped {FormatSize(wiped)} ({wiped:N0} bytes, {writtenPct:F1}% of image)");
          }
        }
        Append("");

        // Refresh the block chart to show the cleaned layout.
        PreviewBlockMap(path, ops);
      });
    });
  }

  private void OnClose(object sender, RoutedEventArgs e) => Close();

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  // ── Probe machinery ─────────────────────────────────────────────────
  // We can't ask a descriptor "do you support this mode?" without invoking
  // it. So we feed it a stream that throws ProbeAbortException on the first
  // Read/Seek — if the descriptor got that far, it accepted the mode; if it
  // threw NotSupportedException before any I/O, the mode is unsupported.

  private sealed class ProbeAbortException : Exception { }
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
}
