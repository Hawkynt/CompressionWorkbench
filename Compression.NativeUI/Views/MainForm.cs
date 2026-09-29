using System.Drawing;
using Compression.Lib;
using Compression.Mounting;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.Theming;
using Compression.NativeUI.ViewModels;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// The shell: menu, toolbar, breadcrumb trail, entry list and status bar over
/// <see cref="MainViewModel"/>. Browses archives and the host filesystem alike, and is the entry
/// point to every other window.
/// </summary>
internal sealed partial class MainForm : Form {
  private const int MenuHeight = 26;
  private const int ToolbarHeight = 30;
  private const int BreadcrumbHeight = 26;
  private const int StatusHeight = 24;

  private readonly MainViewModel _model = new();

  private readonly MenuStrip _menu = new();
  private readonly ToolStrip _toolbar = new();
  private readonly Panel _breadcrumbBar = new();
  private readonly Label _formatLabel = new() { ForeColor = Color.FromArgb(0x44, 0x66, 0xAA) };
  private readonly Breadcrumb _breadcrumb = new() { TrimOnClick = true };
  private readonly KeyedListView _entries = new() {
    View = ListViewView.Details,
    FullRowSelect = true,
    MultiSelect = true,
    Dock = DockStyle.Fill,
  };

  // Folder tree on the left, contents on the right. The tree keeps its width when the window is
  // resized, as a navigation pane does; the contents take the rest.
  private readonly SplitContainer _split = new() {
    FixedPanel = FixedPanel.Panel1,
    SplitterDistance = 220,
    Panel1MinSize = 120,
    Panel2MinSize = 200,
  };
  private readonly KeyedTreeView _tree = new() {
    Dock = DockStyle.Fill,
    ShowLines = true,
    ShowRootLines = true,
    ShowPlusMinus = true,
  };
  private readonly FolderSource _folders;

  // Contents on the left, the preview pane on the right; the pane keeps its width, as the tree does.
  private readonly SplitContainer _contentSplit = new() {
    Dock = DockStyle.Fill,
    FixedPanel = FixedPanel.Panel2,
    Panel1MinSize = 200,
    Panel2MinSize = 120,
  };
  private readonly PreviewPane _preview = new() { Dock = DockStyle.Fill };

  // Bumped for every selection change, so a slow read for an entry the user has already moved past
  // does not overwrite the preview of the one they moved to.
  private int _previewGeneration;
  private bool _previewSized;
  private const int PreviewPaneWidth = 280;
  private const int TreePaneWidth = 230;

  // Set while the tree is being moved to match a navigation that happened elsewhere, so selecting
  // the node there does not navigate a second time.
  private bool _syncingTree;
  private readonly DropOverlay _dropOverlay = new() { Visible = false };
  private readonly StatusStrip _status = new();
  private readonly ToolStripStatusLabel _statusText = new();
  private readonly ToolStripProgressBarItem _progress = new();
  private readonly ContextMenuStrip _entryMenu = new();
  private readonly ImageList _icons = IconSet.CreateImageList(16);

  private readonly IFilesystemMountBackend[] _mountBackends;
  private readonly IMountLauncher? _mountLauncher;

  private string? _sortColumn;
  private bool _sortDescending;

  public MainForm(IEnumerable<IFilesystemMountBackend> mountBackends, IMountLauncher? mountLauncher = null) {
    this._mountBackends = mountBackends.ToArray();
    this._mountLauncher = mountLauncher;

    // Only the archive the user is actually in appears as a folder in the tree. A nested archive is
    // a temporary extraction whose host path means nothing to them, so it is not grafted on anywhere.
    this._folders = new(
      folder => this._model.ArchiveSubfolders(folder),
      () => this._model.HasArchive && !this._model.IsNestedArchive ? this._model.ArchivePath : null);

    this.Text = this._model.Title;
    // Wide enough for three panes — tree, list and preview — with the list columns readable.
    this.ClientSize = new(1180, 680);
    this.MinimumSize = new(700, 420);
    this.StartPosition = FormStartPosition.CenterScreen;
    this.AllowDrop = true;

    this._model.Owner = this;
    this._model.UiMarshaller = action => this.BeginInvoke(action);
    this._model.PropertyChanged += (_, _) => this.SyncFromModel();
    this._model.Entries.CollectionChanged += (_, _) => this.RefreshEntries();
    this._model.Breadcrumbs.CollectionChanged += (_, _) => this.RefreshBreadcrumbs();

    this.BuildMenu();
    this.BuildToolbar();
    this.BuildBreadcrumbBar();
    this.BuildEntryList();
    this.BuildNavigationTree();
    this.BuildStatusBar();

    this._split.Panel1.Controls.Add(this._tree);
    this._split.Panel2.Controls.Add(this._contentSplit);
    this._contentSplit.Panel1.Controls.Add(this._entries);
    this._contentSplit.Panel2.Controls.Add(this._preview);

    // Added in one place, in z-order: the drop overlay sits above the panes it covers.
    this.Controls.AddRange(this._menu, this._toolbar, this._breadcrumbBar, this._split, this._dropOverlay, this._status);

    this.DragOver += this.OnDragOver;
    this.DragLeave += (_, _) => this.SetDropOverlay(visible: false, "");
    this.DragDrop += this.OnDrop;

    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
    this.SyncFromModel();
  }

  /// <summary>Opens <paramref name="path"/> as an archive.</summary>
  public void OpenArchive(string path) => this._model.Open(path);

  /// <summary>Starts in filesystem-browser mode at the folder last used.</summary>
  public void StartInOsBrowserAtLastFolder() => this._model.StartInOsBrowserAtLastFolder();

  // ── Construction ────────────────────────────────────────────────────────────────────────────

  private void BuildMenu() {
    this._menu.Items.AddRange([
      Menu("&File", [
        Item("&Open...", IconKeys.Open, Keys.Control | Keys.O, this._model.OpenCommand),
        Item("&Create...", IconKeys.Create, Keys.Control | Keys.N, this._model.CreateCommand),
        Item("&Add Files...", IconKeys.Add, Keys.None, this._model.AddFilesCommand),
        new ToolStripSeparator(),
        Item("Anal&yze File...", IconKeys.Analyze, Keys.None, this._model.AnalyzeFileCommand),
        new ToolStripSeparator(),
        Action("E&xit", IconKeys.Exit, this.Close),
      ]),
      Menu("&Actions", [
        Item("Extract &All...", IconKeys.Extract, Keys.Control | Keys.E, this._model.ExtractAllCommand),
        Item("Extract &Selected...", IconKeys.ExtractSelected, Keys.None, this._model.ExtractSelectedCommand),
        Item("&Test Integrity", IconKeys.Test, Keys.Control | Keys.T, this._model.TestCommand),
        new ToolStripSeparator(),
        Item("&Back", IconKeys.Back, Keys.Alt | Keys.Left, this._model.BackCommand),
        Item("&Forward", IconKeys.Forward, Keys.Alt | Keys.Right, this._model.ForwardCommand),
        Item("Go &Up", IconKeys.NavigateUp, Keys.Back, this._model.NavigateUpCommand),
        Item("&Delete", IconKeys.Remove, Keys.Delete, this._model.DeleteSelectedCommand),
        // F2 belongs to the list, which starts editing the focused name itself; shown, not claimed.
        Item("Rena&me", IconKeys.Rename, Keys.None, this._model.RenameCommand, "F2"),
        // The clipboard keys are shown, not claimed, for the same reason: registered here they would
        // also fire while the address bar or a name is being edited, and paste files into a text box.
        Item("Cu&t", IconKeys.Cut, Keys.None, this._model.CutCommand, "Ctrl+X"),
        Item("&Copy", IconKeys.Copy, Keys.None, this._model.CopyCommand, "Ctrl+C"),
        Item("&Paste", IconKeys.Paste, Keys.None, this._model.PasteCommand, "Ctrl+V"),
        Item("New &Folder", IconKeys.Folder, Keys.Control | Keys.Shift | Keys.N, this._model.NewFolderCommand),
        new ToolStripSeparator(),
        Item("&View as Text", IconKeys.ViewText, Keys.None, this._model.ViewAsTextCommand, "Enter"),
        Item("View as &Hex", IconKeys.ViewHex, Keys.None, this._model.ViewAsHexCommand),
        new ToolStripSeparator(),
        Item("&Properties", IconKeys.Properties, Keys.Alt | Keys.Enter, this._model.PropertiesCommand),
        Item("A&nalyze Entry...", IconKeys.Analyze, Keys.None, this._model.AnalyzeCommand),
      ]),
      Menu("&View", [
        this.PreviewPaneToggle(),
        new ToolStripSeparator(),
        .. this.ViewModeItems(),
      ]),
      Menu("&Tools", [
        Item("&Benchmark...", IconKeys.Test, Keys.None, this._model.BenchmarkCommand),
        new ToolStripSeparator(),
        Action("&Reverse Engineer Format...", IconKeys.Analyze, () => new ReverseEngineerWindow().Show()),
        new ToolStripSeparator(),
        Action("&Maintenance (Optimize / Shrink / Defragment / Purge / Wipe)...", IconKeys.Defragment, this.OpenMaintenance),
        Action("Convert &Archive...", IconKeys.Create, () => _ = this.ConvertArchiveAsync()),
        Action("&Partition Editor...", IconKeys.Create, this.OpenPartitionEditor),
        Action("&Mount Image...", IconKeys.Open, this.OpenMountWindow),
        new ToolStripSeparator(),
        Item("&File Associations...", IconKeys.Properties, Keys.None, this._model.FileAssociationsCommand),
      ]),
      Menu("&Help", [
        Action("&About", IconKeys.About, () => new AboutWindow().ShowDialog(this)),
      ]),
    ]);
  }

  private static ToolStripMenuItem Menu(string text, ToolStripItem[] children) {
    var item = new ToolStripMenuItem(text);
    item.DropDownItems.AddRange(children);
    return item;
  }

  /// <summary>
  /// A menu item bound to a command. <paramref name="displayShortcut"/> only labels the item — it
  /// is for keys another control already owns, where registering a second global handler would take
  /// the key away from the control that should act on it.
  /// </summary>
  private ToolStripMenuItem Item(
    string text, string iconKey, Keys shortcut, ICommand command, string? displayShortcut = null) {
    var item = new ToolStripMenuItem(text) { Image = Images.Icon(iconKey) };
    if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
    if (displayShortcut is not null) item.ShortcutKeyDisplayString = displayShortcut;
    item.Click += (_, _) => {
      if (command.CanExecute(null)) command.Execute(null);
    };
    command.CanExecuteChanged += (_, _) => item.Enabled = command.CanExecute(null);
    item.Enabled = command.CanExecute(null);
    return item;
  }

  /// <summary>Details and Thumbnails, on the keys Explorer uses for them.</summary>
  private ToolStripMenuItem[] ViewModeItems() {
    var details = new ToolStripMenuItem("&Details") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.D6, Checked = true };
    var thumbnails = new ToolStripMenuItem("&Thumbnails") {
      Image = Images.Icon(IconKeys.ViewImage),
      ShortcutKeys = Keys.Control | Keys.Shift | Keys.D2,
    };

    details.Click += (_, _) => Choose(thumbnailsOn: false);
    thumbnails.Click += (_, _) => Choose(thumbnailsOn: true);
    return [details, thumbnails];

    void Choose(bool thumbnailsOn) {
      this.ShowThumbnails(thumbnailsOn);
      details.Checked = !thumbnailsOn;
      thumbnails.Checked = thumbnailsOn;
    }
  }

  private ToolStripMenuItem PreviewPaneToggle() {
    var item = new ToolStripMenuItem("&Preview Pane") {
      Image = Images.Icon(IconKeys.Preview),
      ShortcutKeys = Keys.Alt | Keys.P,
      Checked = true,
    };
    item.Click += (_, _) => {
      this.PreviewPaneVisible = !this.PreviewPaneVisible;
      item.Checked = this.PreviewPaneVisible;
    };
    return item;
  }

  private static ToolStripMenuItem Action(string text, string iconKey, Action action) {
    var item = new ToolStripMenuItem(text) { Image = Images.Icon(iconKey) };
    item.Click += (_, _) => action();
    return item;
  }

  private void BuildToolbar() {
    this._toolbar.Items.AddRange([
      Button("Open", IconKeys.Open, "Open archive (Ctrl+O)", this._model.OpenCommand),
      Button("Create", IconKeys.Create, "Create archive (Ctrl+N)", this._model.CreateCommand),
      new ToolStripSeparator(),
      Button("Extract", IconKeys.Extract, "Extract all (Ctrl+E)", this._model.ExtractAllCommand),
      Button("Add", IconKeys.Add, "Add files to archive", this._model.AddFilesCommand),
      Button("Test", IconKeys.Test, "Test integrity (Ctrl+T)", this._model.TestCommand),
      new ToolStripSeparator(),
      Button("Back", IconKeys.Back, "Back (Alt+Left)", this._model.BackCommand),
      Button("Forward", IconKeys.Forward, "Forward (Alt+Right)", this._model.ForwardCommand),
      Button("Up", IconKeys.NavigateUp, "Go up (Backspace)", this._model.NavigateUpCommand),
      new ToolStripSeparator(),
      Button("Analyze", IconKeys.Analyze, "Analyze binary file", this._model.AnalyzeFileCommand),
    ]);

    static ToolStripButton Button(string text, string iconKey, string tip, ICommand command) {
      var button = new ToolStripButton(text) { Image = Images.Icon(iconKey), ToolTipText = tip };
      button.Click += (_, _) => {
        if (command.CanExecute(null)) command.Execute(null);
      };
      command.CanExecuteChanged += (_, _) => button.Enabled = command.CanExecute(null);
      button.Enabled = command.CanExecute(null);
      return button;
    }
  }

  private void BuildNavigationTree() {
    this._tree.ImageList = this._icons;
    foreach (var root in this._folders.Roots())
      this._tree.Nodes.Add(this.MakeTreeNode(root));

    this._tree.AfterSelect += (_, e) => {
      if (this._syncingTree || e.Node?.Tag is not Location target) return;
      this._model.NavigateTo(target);
    };

    this._model.LocationChanged += (_, where) => this.RevealInTree(where);
  }

  private TreeNode MakeTreeNode(FolderNode folder) {
    var isArchive = folder.Location is { IsInArchive: true, ArchiveFolder: "" };
    var node = new TreeNode(folder.Label) {
      Tag = folder.Location,
      ImageKey = isArchive ? IconKeys.Open : IconKeys.Folder,
      SelectedImageKey = isArchive ? IconKeys.Open : IconKeys.Folder,
    };
    node.SetChildLoader(n => this._folders.Children((Location)n.Tag!).Select(this.MakeTreeNode));
    return node;
  }

  /// <summary>
  /// Moves the tree to <paramref name="where"/>, however the shell got there — a double-click, a
  /// crumb, Back, a typed address — expanding each folder on the way down.
  /// </summary>
  private void RevealInTree(Location where) {
    if (this._model.IsNestedArchive) return;

    var chain = ChainFromRoot(where);
    var node = this._tree.Nodes.Cast<TreeNode>()
      .Where(n => n.Tag is Location root && chain.Contains(root))
      .MaxBy(n => chain.IndexOf((Location)n.Tag!));
    if (node is null) return;

    foreach (var step in chain.Skip(chain.IndexOf((Location)node.Tag!) + 1)) {
      node.Expand();
      var next = FindChild(node, step);

      // A folder listed before the archive was opened, or before a folder appeared on disk, is out
      // of date: reload it once rather than stopping short.
      if (next is null) {
        this.ReloadChildren(node);
        next = FindChild(node, step);
      }

      // Still missing means the listing leaves it out on purpose — a hidden folder, typically. The
      // user went there anyway, so it is shown, as Explorer shows a hidden folder you are inside.
      if (next is null) {
        next = this.MakeTreeNode(new(LabelOf(step), step));
        node.Nodes.Add(next);
      }

      node = next;
    }

    this._syncingTree = true;
    try {
      this._tree.SelectedNode = node;
      node.EnsureVisible();
    } finally {
      this._syncingTree = false;
    }

    static TreeNode? FindChild(TreeNode parent, Location step)
      => parent.Nodes.Cast<TreeNode>().FirstOrDefault(c => c.Tag is Location l && l == step);

    static string LabelOf(Location place) => place.ArchiveFolder switch {
      null or "" => Path.GetFileName(Path.TrimEndingDirectorySeparator(place.HostPath)),
      var folder => folder.TrimEnd('/')[(folder.TrimEnd('/').LastIndexOf('/') + 1)..],
    };
  }

  private void ReloadChildren(TreeNode node) {
    node.Nodes.Clear();
    foreach (var child in this._folders.Children((Location)node.Tag!))
      node.Nodes.Add(this.MakeTreeNode(child));
  }

  /// <summary>Every place from a filesystem root down to <paramref name="where"/>.</summary>
  private static List<Location> ChainFromRoot(Location where) {
    var hostFolder = where.IsInArchive ? Path.GetDirectoryName(where.HostPath) ?? "" : where.HostPath;
    var chain = HostPathSegments.Split(hostFolder).Select(s => Navigation.Location.Folder(s.Path)).ToList();
    if (!where.IsInArchive) return chain;

    chain.Add(Navigation.Location.InArchive(where.HostPath, ""));
    var accumulated = "";
    foreach (var part in (where.ArchiveFolder ?? "").TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      accumulated += part + "/";
      chain.Add(Navigation.Location.InArchive(where.HostPath, accumulated));
    }

    return chain;
  }

  private void BuildBreadcrumbBar() {
    this._formatLabel.Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold);
    this._breadcrumb.ItemClicked += (_, e) => {
      if (e.Item.Tag is Location target) this._model.NavigateToBreadcrumbCommand.Execute(target);
      else if (e.Item.Tag is string folderPath) this._model.NavigateToBreadcrumbCommand.Execute(folderPath);
    };

    // An address bar as well as a trail: click the empty space, type or paste a path — one that runs
    // into an archive included — and Enter goes there. The edit field opens on the real path rather
    // than on the captions joined together, which would mix the host's separator with the archive's.
    this._breadcrumb.Editable = true;
    this._breadcrumb.PathComposer = () => this._model.CurrentLocation?.ToString() ?? "";
    this._breadcrumb.PathEntered += (_, e) => this._model.NavigateToAddress(e.Path);
    this._breadcrumb.AutoCompleteSource = this._folders.Complete;

    // Each crumb's chevron lists the folders beneath it, so a sibling is one click away.
    this._breadcrumb.SubItemsProvider = item => [
      .. (item?.Tag is Location parent ? this._folders.Children(parent) : this._folders.Roots())
        .Select(folder => new BreadcrumbItem(folder.Label) { Tag = folder.Location }),
    ];
    this._breadcrumb.SubItemSelected += (_, e) => {
      if (e.Item.Tag is Location target) this._model.NavigateTo(target);
    };

    this._breadcrumbBar.BackColor = DefaultTheme.Instance.ControlBackground;
    this._breadcrumbBar.Controls.AddRange(this._formatLabel, this._breadcrumb);
  }

  private void BuildEntryList() {
    this._entries.SmallImageList = this._icons;
    // The icon sits in the name column, as in every file manager: the column an item's own text
    // occupies is the one label editing edits, so a separate icon column made renaming edit nothing.
    this._entries.Columns.AddRange([
      new ColumnHeader("Name", 250),
      new ColumnHeader("Original", 90) { TextAlign = ContentAlignment.MiddleRight },
      new ColumnHeader("Compressed", 90) { TextAlign = ContentAlignment.MiddleRight },
      new ColumnHeader("Ratio", 60) { TextAlign = ContentAlignment.MiddleRight },
      new ColumnHeader("Method", 80),
      new ColumnHeader("Modified", 130),
    ]);

    this._entries.SelectedIndexChanged += (_, _) => {
      this._model.SelectedEntries.Clear();
      foreach (var item in this._entries.SelectedItems)
        if (item.Tag is ArchiveEntryViewModel entry) this._model.SelectedEntries.Add(entry);

      CommandManager.InvalidateRequerySuggested();
      this.RefreshPreview();
    };

    this._entries.LabelEdit = true;
    this._entries.BeforeLabelEdit += (_, e) => {
      if (this.EntryAt(e.Item) is not { } entry || !this._model.CanRename(entry)) e.CancelEdit = true;
    };
    this._entries.AfterLabelEdit += (_, e) => {
      // A null label is an edit the user abandoned with Escape.
      if (e.Label is not null && this.EntryAt(e.Item) is { } entry) this._model.Rename(entry, e.Label);

      // The list is rebuilt from the model either way, so the row never takes the typed text as is:
      // a refused name must not stay on screen, and an accepted one is already there, re-sorted.
      e.CancelEdit = true;
    };
    this._model.RenameRequested += (_, entry) => {
      for (var i = 0; i < this._entries.Items.Count; ++i)
        if (ReferenceEquals(this._entries.Items[i].Tag, entry)) {
          this._entries.BeginEdit(i);
          return;
        }
    };

    this._entries.KeyDown += (_, e) => this.OnClipboardKey(e);
    this._tree.KeyDown += (_, e) => this.OnClipboardKey(e);

    this._entries.ItemActivate += (_, _) => this.ActivateSelectedEntry();
    this._entries.MouseDoubleClick += (_, _) => this.ActivateSelectedEntry();
    this._entries.ColumnClick += (_, e) => this.SortBy(e.Column);
    this._entries.ContextMenuStrip = this._entryMenu;

    this.BuildEntryContextMenu();
  }

  private void BuildEntryContextMenu() {
    var maintenance = new ToolStripMenuItem("&Maintenance") { Image = Images.Icon(IconKeys.Defragment) };
    maintenance.DropDownItems.AddRange([
      Tip(this.Item("&Compact (defrag + optimize + shrink)...", IconKeys.Defragment, Keys.None, this._model.CompactEntryCommand),
        "One pass to the smallest valid container holding the same contents. Tick 'Minimal geometry' in the window for the bare-minimum (non-standard) rebuild."),
      Tip(this.Item("&Reconfigure (geometry/options)...", IconKeys.Properties, Keys.None, this._model.ReconfigureEntryCommand),
        "Change an existing image's geometry/options after creation (e.g. FAT cluster size or root entries, NTFS MFT record size). Contents preserved byte-for-byte."),
      new ToolStripSeparator(),
      Tip(this.Item("&Optimize...", IconKeys.Analyze, Keys.None, this._model.OptimizeEntryCommand),
        "Find + apply the best layout / re-encode payload. Size preserved where possible."),
      Tip(this.Item("&Shrink...", IconKeys.Defragment, Keys.None, this._model.ShrinkEntryCommand),
        "Keep the parameter set; minimise the stored footprint."),
      Tip(this.Item("&Defragment...", IconKeys.Defragment, Keys.None, this._model.DefragmentEntryCommand),
        "Re-order entries/extents so files are contiguous. Size preserved."),
      new ToolStripSeparator(),
      Tip(this.Item("&Purge...", IconKeys.Remove, Keys.None, this._model.PurgeEntryCommand),
        "Erase ALL live data, leaving a valid empty container."),
      Tip(this.Item("&Wipe Empty...", IconKeys.Remove, Keys.None, this._model.WipeEntryCommand),
        "Overwrite only unused space (free clusters, slack, deleted entries). Live data untouched."),
      Tip(this.Item("Scra&mble...", IconKeys.Defragment, Keys.None, this._model.ScrambleEntryCommand),
        "Scatter every block of every file across the volume — fragmentation on purpose, so Defragment has something real to work against. Content preserved; asks first."),
    ]);

    this._entryMenu.Items.AddRange([
      this.Item("View as &Text", IconKeys.ViewText, Keys.None, this._model.ViewAsTextCommand),
      this.Item("View as &Hex", IconKeys.ViewHex, Keys.None, this._model.ViewAsHexCommand),
      this.Item("Preview as &Image", IconKeys.ViewImage, Keys.None, this._model.ViewAsImageCommand),
      new ToolStripSeparator(),
      this.Item("&Extract Selected...", IconKeys.ExtractSelected, Keys.None, this._model.ExtractSelectedCommand),
      this.Item("Extract &All...", IconKeys.Extract, Keys.None, this._model.ExtractAllCommand),
      this.Item("&Delete", IconKeys.Remove, Keys.None, this._model.DeleteSelectedCommand, "Del"),
      this.Item("Rena&me", IconKeys.Rename, Keys.None, this._model.RenameCommand, "F2"),
      new ToolStripSeparator(),
      this.Item("Cu&t", IconKeys.Cut, Keys.None, this._model.CutCommand, "Ctrl+X"),
      this.Item("&Copy", IconKeys.Copy, Keys.None, this._model.CopyCommand, "Ctrl+C"),
      this.Item("&Paste", IconKeys.Paste, Keys.None, this._model.PasteCommand, "Ctrl+V"),
      new ToolStripSeparator(),
      this.Item("&Add Files...", IconKeys.Add, Keys.None, this._model.AddFilesCommand),
      new ToolStripSeparator(),
      maintenance,
      Action("Con&vert Archive...", IconKeys.Create, () => _ = this.ConvertArchiveAsync()),
      this.Item("A&nalyze...", IconKeys.Analyze, Keys.None, this._model.AnalyzeCommand),
      this.Item("&Properties", IconKeys.Properties, Keys.None, this._model.PropertiesCommand, "Alt+Enter"),
    ]);

    ToolStripMenuItem Tip(ToolStripMenuItem item, string tip) {
      item.ToolTipText = tip;
      return item;
    }
  }

  private void BuildStatusBar() {
    this._progress.Visible = false;
    this._status.Items.AddRange([this._statusText, this._progress]);
  }

  private void LayoutChildren() {
    var width = this.ClientSize.Width;

    this._menu.Bounds = new(0, 0, width, MenuHeight);
    this._toolbar.Bounds = new(0, MenuHeight, width, ToolbarHeight);

    var y = MenuHeight + ToolbarHeight;
    var breadcrumbVisible = this._breadcrumbBar.Visible;
    if (breadcrumbVisible) {
      this._breadcrumbBar.Bounds = new(0, y, width, BreadcrumbHeight);
      this._formatLabel.Bounds = new(4, 3, 90, 20);
      this._breadcrumb.Bounds = new(98, 2, Math.Max(50, width - 102), 22);
      y += BreadcrumbHeight;
    }

    var paneHeight = Math.Max(60, this.ClientSize.Height - y - StatusHeight);
    this._split.Bounds = new(0, y, width, paneHeight);

    // The tree and the preview open at fixed widths, once; from then on the list takes whatever the
    // window gains, and a width the user dragged to is theirs. Set before the split has a size, a
    // distance is clamped to the minimum, which is why this waits for the first real layout.
    if (!this._previewSized && width > 0) {
      this._previewSized = true;
      this._split.SplitterDistance = TreePaneWidth;
      var contentWidth = width - this._split.SplitterDistance - this._split.SplitterWidth;
      this._contentSplit.SplitterDistance = Math.Max(this._contentSplit.Panel1MinSize, contentWidth - PreviewPaneWidth);
    }
    this._dropOverlay.Bounds = this._split.Bounds;
    this._status.Bounds = new(0, this.ClientSize.Height - StatusHeight, width, StatusHeight);
  }

  // ── View-model synchronisation ──────────────────────────────────────────────────────────────

  private void SyncFromModel() {
    this.Text = this._model.Title;
    this._statusText.Text = this._model.StatusText;
    this._progress.Visible = this._model.IsBusy;
    this._progress.Value = (int)Math.Clamp(this._model.Progress, 0, 100);
    this._formatLabel.Text = this._model.Format;

    var showBreadcrumbs = this._model.HasArchive || this._model.IsBrowsingOsFolder;
    if (this._breadcrumbBar.Visible != showBreadcrumbs) {
      this._breadcrumbBar.Visible = showBreadcrumbs;
      this.LayoutChildren();
    }

    CommandManager.InvalidateRequerySuggested();
  }

  private void RefreshBreadcrumbs() {
    this._breadcrumb.Items.Clear();
    foreach (var segment in this._model.Breadcrumbs)
      this._breadcrumb.Items.Add(new BreadcrumbItem(segment.Label) { Tag = (object?)segment.Target ?? segment.FolderPath });
  }

  private void RefreshEntries() {
    this._entries.Items.Clear();

    foreach (var entry in this.SortedEntries())
      this._entries.Items.Add(new ListViewItem(entry.Name, [
        entry.OriginalSizeText,
        entry.CompressedSizeText,
        entry.RatioText,
        entry.MethodText,
        entry.LastModifiedText,
      ]) {
        Tag = entry,
        ImageKey = this.ImageKeyFor(entry),
      });

    this.StartThumbnails();
  }

  /// <summary>
  /// Applies the current column sort. The parent entry always leads, whatever the sort — it is
  /// navigation, not content.
  /// </summary>
  private IEnumerable<ArchiveEntryViewModel> SortedEntries() {
    var entries = this._model.Entries.AsEnumerable();
    if (this._sortColumn is null) return entries;

    Func<ArchiveEntryViewModel, IComparable?> key = this._sortColumn switch {
      "Name" => e => e.Name,
      "Original" => e => e.OriginalSize,
      "Compressed" => e => e.CompressedSize,
      "Ratio" => e => e.OriginalSize > 0 ? (double)e.CompressedSize / e.OriginalSize : -1,
      "Method" => e => e.MethodText,
      "Modified" => e => e.LastModified,
      _ => e => e.Name,
    };

    var parents = entries.Where(e => e.IsParentEntry);
    var rest = entries.Where(e => !e.IsParentEntry);
    rest = this._sortDescending ? rest.OrderByDescending(key) : rest.OrderBy(key);
    return parents.Concat(rest);
  }

  private void SortBy(int columnIndex) {
    if (columnIndex < 0 || columnIndex >= this._entries.Columns.Count) return;

    var header = this._entries.Columns[columnIndex].Text;
    if (this._sortColumn == header) this._sortDescending = !this._sortDescending;
    else {
      this._sortColumn = header;
      this._sortDescending = false;
    }

    this.RefreshEntries();
  }

  /// <summary>Whether the preview pane is shown.</summary>
  internal bool PreviewPaneVisible {
    get => !this._contentSplit.Panel2Collapsed;
    set {
      this._contentSplit.Panel2Collapsed = !value;
      this.RefreshPreview();
    }
  }

  /// <summary>
  /// Shows the single selected entry in the preview pane. The bytes are read off the UI thread; a
  /// result that arrives after the selection has moved on is dropped.
  /// </summary>
  private void RefreshPreview() {
    var generation = ++this._previewGeneration;
    if (!this.PreviewPaneVisible) return;

    var selected = this._model.SelectedEntries.Where(e => !e.IsParentEntry).Take(2).ToList();
    if (selected.Count != 1) {
      this._preview.ShowCaption(selected.Count == 0 ? PreviewPane.NothingSelected : $"{this._model.SelectedEntries.Count(e => !e.IsParentEntry)} items selected");
      return;
    }

    var entry = selected[0];
    this._preview.ShowCaption(entry.Name);
    if (entry.IsDirectory) {
      this._preview.ShowCaption(this._model.ReadForPreview(entry).Caption);
      return;
    }

    Task.Run(() => this._model.ReadForPreview(entry)).ContinueWith(read => this.BeginInvoke(() => {
      if (generation != this._previewGeneration || read.IsFaulted) return;

      var (data, caption) = read.Result;
      if (data is null) this._preview.ShowCaption(caption);
      else this._preview.ShowContent(caption, data);
    }), TaskScheduler.Default);
  }

  /// <summary>Ctrl+C, Ctrl+X and Ctrl+V where files are shown — the list and the tree, not a text box.</summary>
  private void OnClipboardKey(KeyEventArgs e) {
    if (!e.Control || e.Alt || e.Shift) return;

    var command = e.KeyCode switch {
      Keys.C => this._model.CopyCommand,
      Keys.X => this._model.CutCommand,
      Keys.V => this._model.PasteCommand,
      _ => null,
    };
    if (command is null) return;

    if (command.CanExecute(null)) command.Execute(null);
    e.Handled = true;
  }

  private ArchiveEntryViewModel? EntryAt(int index)
    => index >= 0 && index < this._entries.Items.Count ? this._entries.Items[index].Tag as ArchiveEntryViewModel : null;

  private void ActivateSelectedEntry() {
    if (this._entries.SelectedItem?.Tag is not ArchiveEntryViewModel entry) return;

    if (entry.IsParentEntry || entry.IsDirectory) {
      this._model.NavigateIntoCommand.Execute(entry);
      return;
    }

    // In filesystem-browser mode a double-click matches File → Open: NavigateInto detects the
    // format and either opens it as an archive or falls back to a byte preview. Going straight to
    // the preview would read a multi-megabyte file into memory for nothing.
    if (this._model.IsBrowsingOsFolder) {
      this._model.NavigateIntoCommand.Execute(entry);
      return;
    }

    this._model.ViewSelectedAs(hex: false);
  }

  // ── Drag and drop ───────────────────────────────────────────────────────────────────────────

  private void OnDragOver(object? sender, DragEventArgs e) {
    if (e.Data is not string[] { Length: > 0 } files) {
      e.Effect = DragDropEffects.None;
      return;
    }

    e.Effect = DragDropEffects.Copy;

    if (!this._model.HasArchive) {
      this.SetDropOverlay(visible: true, "Drop archive to open");
      return;
    }

    var (allowed, message) = this._model.EvaluateDropAgainstCurrentArchive(files);
    e.Effect = allowed ? DragDropEffects.Copy : DragDropEffects.None;
    this.SetDropOverlay(visible: true, message ?? (allowed ? "Drop to add files to archive" : "This archive doesn't accept those inputs"));
  }

  private void OnDrop(object? sender, DragEventArgs e) {
    this.SetDropOverlay(visible: false, "");
    if (e.Data is string[] { Length: > 0 } files) this._model.HandleFileDrop(files);
  }

  private void SetDropOverlay(bool visible, string message) {
    this._dropOverlay.Message = message;
    this._dropOverlay.Visible = visible;
    this._dropOverlay.Invalidate();
  }

  // ── Window commands ─────────────────────────────────────────────────────────────────────────

  private void OpenMaintenance() {
    var preselected = this._model.HasArchive ? this._model.ArchivePath : null;
    var window = preselected is not null ? new DefragmentWindow(preselected) : new DefragmentWindow();

    // Re-list whenever the maintenance window mutates the archive we have open.
    window.ArchiveMutated += path => {
      if (this._model.HasArchive && string.Equals(path, this._model.ArchivePath, StringComparison.OrdinalIgnoreCase))
        this._model.Open(this._model.ArchivePath);
    };

    window.Show();
  }

  private void OpenPartitionEditor() {
    var preselected = this._model.HasArchive ? this._model.ArchivePath : null;
    if (preselected is not null && File.Exists(preselected)) {
      new PartitionsWindow(preselected).Show();
      return;
    }

    var dialog = new OpenFileDialog {
      Title = "Open disk image / virtual disk",
      Filter = "Disk images & virtual disks|*.img;*.iso;*.bin;*.vhd;*.vhdx;*.vmdk;*.qcow2;*.qcow;*.vdi|All files|*.*",
    };

    // Cancelling still opens the window, so the in-window Open button remains reachable.
    var window = dialog.ShowDialog() == DialogResult.OK
      ? new PartitionsWindow(dialog.FileName)
      : new PartitionsWindow();
    window.Show();
  }

  private void OpenMountWindow() => new MountWindow(this._mountBackends, this._mountLauncher).Show();

  /// <summary>
  /// Converts the selected file — or the open archive — to another format. The registry warm-up
  /// started at launch may still be running, so it is awaited rather than blocked on.
  /// </summary>
  private async Task ConvertArchiveAsync() {
    if (!FormatRegistration.IsReady) {
      this.Cursor = Cursors.Wait;
      try {
        await FormatRegistration.EnsureInitializedAsync();
      } finally {
        this.Cursor = Cursors.Default;
      }
    }

    // Prefer a single real file picked in the browser, so Convert is reachable without opening the
    // archive first; otherwise fall back to whatever is open.
    string? sourcePath = null;
    if (this._model.IsBrowsingOsFolder
        && this._model.SelectedEntries is [{ IsParentEntry: false, IsDirectory: false } selected]
        && File.Exists(selected.Path))
      sourcePath = selected.Path;
    else if (!string.IsNullOrEmpty(this._model.ArchivePath) && File.Exists(this._model.ArchivePath))
      sourcePath = this._model.ArchivePath;

    if (sourcePath is null) {
      MessageBox.Show(this,
        "Select a file in the explorer or open an archive/image first, then use Convert Archive.",
        "Convert Archive", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }

    this.RunConvertArchiveDialog(sourcePath);
  }

  private void RunConvertArchiveDialog(string sourcePath) {
    // Every creatable descriptor is offered, sorted by name. Filesystem descriptors share the
    // archive category with real archives, so a category prefix would mislead rather than help.
    var creatable = FormatRegistry.All
      .Where(d => d is IArchiveCreatable)
      .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (creatable.Count == 0) {
      MessageBox.Show(this, "No creatable formats are registered.", "Convert Archive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }

    var parts = new List<string>();
    foreach (var d in creatable) {
      var extensions = d.Extensions.Count > 0
        ? string.Join(";", d.Extensions.Select(x => "*" + x))
        : "*" + d.DefaultExtension;
      parts.Add($"{d.DisplayName} ({extensions})|{extensions}");
    }
    parts.Add("All files (*.*)|*.*");

    var save = new SaveFileDialog {
      Title = "Convert archive to...",
      Filter = string.Join("|", parts),
      FileName = Path.GetFileNameWithoutExtension(sourcePath) + "_converted",
    };
    if (save.ShowDialog() != DialogResult.OK) return;

    // FilterIndex is one-based, and the final slot is "All files", which falls back to
    // extension-based detection.
    var index = save.FilterIndex - 1;
    var targetFormatId = index >= 0 && index < creatable.Count ? creatable[index].Id : null;

    // A target that publishes tunable knobs asks for them before the conversion starts. Cancelling
    // that dialog aborts silently, exactly as cancelling the save dialog does.
    FormatCreateOptions? createOptions = null;
    if (!string.IsNullOrEmpty(targetFormatId)
        && FormatRegistry.GetById(targetFormatId) is { } target
        && target is IFormatOptionsSchema { OptionsSchema: { Count: > 0 } schema }) {
      var options = new TargetOptionsDialog(schema, target.DisplayName);
      if (options.ShowDialog(this) != DialogResult.OK) return;
      createOptions = new() { FormatSpecific = options.Result };
    }

    try {
      var warnings = ArchiveOperations.ConvertArchive(sourcePath, save.FileName, targetFormatId, createOptions);
      var message = $"Conversion complete.\nOutput: {save.FileName}";
      if (warnings.Count > 0) message += "\n\nWarnings:\n" + string.Join("\n", warnings);
      MessageBox.Show(this, message, "Convert Archive", MessageBoxButtons.OK, MessageBoxIcon.Information);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Conversion failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  /// <summary>Dims the entry list and names what a drop would do.</summary>
  private sealed class DropOverlay : OwnerDrawnControl {
    public string Message { get; set; } = "";

    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.FillRectangle(Color.FromArgb(0x80, 0, 0, 0), new(0, 0, this.Width, this.Height));

      var icon = Images.Icon(IconKeys.Extract, 48);
      g.DrawImage(icon, new(this.Width / 2 - 24, this.Height / 2 - 40, 48, 48));

      var font = DefaultTheme.Instance.DefaultFont;
      g.DrawText(this.Message, new(font.Family, 13f, FontStyle.Regular), Color.White,
        new(0, this.Height / 2 + 16, this.Width, 24), ContentAlignment.MiddleCenter);
    }
  }
}
