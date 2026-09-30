using Compression.NativeUI.Theming;
using Compression.NativeUI.ViewModels;
using Hawkynt.NativeForms;
using Color = System.Drawing.Color;

namespace Compression.NativeUI.Views;

/// <summary>
/// The command surface: an Office-style ribbon in place of a menu bar and toolbar. Every command the
/// shell has lives here once — the Quick Access Toolbar for navigation, File for archives as files,
/// Home for everyday file work, View for panes and layout, Tools for the specialist windows, and an
/// Archive Tools tab that appears only while the shell is inside an archive.
/// </summary>
/// <remarks>
/// Shortcut keys are registered on the ribbon items and dispatched form-wide. Keys that belong to the
/// list — F2, Enter, the clipboard chords — are only named in the tooltips: registered here they
/// would also fire while a name or an address is being typed.
/// </remarks>
internal sealed partial class MainForm {
  private const int LargeIcon = 32;
  private const int SmallIcon = 16;

  private readonly Ribbon _ribbon = new();
  private readonly RibbonContextualTabGroup _archiveTools = new("Archive Tools", Color.FromArgb(0xE0, 0x9A, 0x2C)) { Visible = false };
  private RibbonToggleButton _navigationPaneToggle = null!;
  private RibbonToggleButton _previewPaneToggle = null!;
  private RibbonToggleButton _detailsToggle = null!;
  private RibbonToggleButton _thumbnailsToggle = null!;

  private void BuildRibbon() {
    this._ribbon.QuickAccessItems.Add(this.RibbonCommand("Back", IconKeys.Back, this._model.BackCommand, RibbonItemSize.Small, Keys.Alt | Keys.Left, "Back (Alt+Left)"));
    this._ribbon.QuickAccessItems.Add(this.RibbonCommand("Forward", IconKeys.Forward, this._model.ForwardCommand, RibbonItemSize.Small, Keys.Alt | Keys.Right, "Forward (Alt+Right)"));
    this._ribbon.QuickAccessItems.Add(this.RibbonCommand("Up", IconKeys.NavigateUp, this._model.NavigateUpCommand, RibbonItemSize.Small, Keys.Back, "Up (Backspace)"));
    this._ribbon.QuickAccessItems.Add(this.RibbonAction("Refresh", IconKeys.Refresh, this.RefreshView, RibbonItemSize.Small, Keys.F5, "Refresh (F5)"));

    this._ribbon.Tabs.AddRange(this.FileTab(), this.HomeTab(), this.ViewTab(), this.ToolsTab());

    var archive = this.ArchiveTab();
    this._ribbon.Tabs.Add(archive);
    this._archiveTools.Add(archive);
    this._ribbon.ContextualTabGroups.Add(this._archiveTools);

    this._ribbon.SelectedIndex = 1; // Home, as in Explorer
    this._ribbon.PreferredHeightChanged += (_, _) => this.LayoutChildren();

    // The contextual tab follows the shell in and out of archives.
    this._model.LocationChanged += (_, _) => this.UpdateArchiveTools();
    this.UpdateArchiveTools();
  }

  private void UpdateArchiveTools() {
    var inside = this._model.HasArchive && !this._model.IsBrowsingOsFolder;
    this._archiveTools.Visible = inside;
    if (!inside && this._ribbon.SelectedTab is { } tab && this._archiveTools.Tabs.Contains(tab))
      this._ribbon.SelectedIndex = 1;
  }

  // ── tabs ────────────────────────────────────────────────────────────────────────────────────

  private RibbonTab FileTab() {
    var tab = new RibbonTab("File");
    tab.Groups.AddRange(
      Group("Archive",
        this.RibbonCommand("Open", IconKeys.Open, this._model.OpenCommand, RibbonItemSize.Large, Keys.Control | Keys.O, "Open an archive or image (Ctrl+O)"),
        this.RibbonCommand("Create", IconKeys.Create, this._model.CreateCommand, RibbonItemSize.Large, Keys.Control | Keys.N, "Create an archive (Ctrl+N)"),
        this.RibbonAction("Convert", IconKeys.Create, () => _ = this.ConvertArchiveAsync(), RibbonItemSize.Small, Keys.None, "Convert an archive to another format")),
      Group("Application",
        this.RibbonCommand("File Associations", IconKeys.Properties, this._model.FileAssociationsCommand, RibbonItemSize.Small),
        this.RibbonAction("About", IconKeys.About, () => new AboutWindow().ShowDialog(this), RibbonItemSize.Small),
        this.RibbonAction("Exit", IconKeys.Exit, this.Close, RibbonItemSize.Small)));
    return tab;
  }

  private RibbonTab HomeTab() {
    var tab = new RibbonTab("Home");
    tab.Groups.AddRange(
      Group("Clipboard",
        this.RibbonCommand("Paste", IconKeys.Paste, this._model.PasteCommand, RibbonItemSize.Large, Keys.None, "Paste (Ctrl+V)"),
        this.RibbonCommand("Cut", IconKeys.Cut, this._model.CutCommand, RibbonItemSize.Small, Keys.None, "Cut (Ctrl+X)"),
        this.RibbonCommand("Copy", IconKeys.Copy, this._model.CopyCommand, RibbonItemSize.Small, Keys.None, "Copy (Ctrl+C)")),
      Group("Organize",
        this.RibbonCommand("New Folder", IconKeys.Folder, this._model.NewFolderCommand, RibbonItemSize.Large, Keys.Control | Keys.Shift | Keys.N, "New folder (Ctrl+Shift+N)"),
        this.RibbonCommand("Rename", IconKeys.Rename, this._model.RenameCommand, RibbonItemSize.Small, Keys.None, "Rename (F2)"),
        this.RibbonCommand("Delete", IconKeys.Remove, this._model.DeleteSelectedCommand, RibbonItemSize.Small, Keys.Delete, "Delete (Del)")),
      Group("Open",
        this.RibbonCommand("Properties", IconKeys.Properties, this._model.PropertiesCommand, RibbonItemSize.Large, Keys.Alt | Keys.Enter, "Properties (Alt+Enter)"),
        this.RibbonCommand("View as Text", IconKeys.ViewText, this._model.ViewAsTextCommand, RibbonItemSize.Small, Keys.None, "View as text (Enter)"),
        this.RibbonCommand("View as Hex", IconKeys.ViewHex, this._model.ViewAsHexCommand, RibbonItemSize.Small),
        this.RibbonCommand("Preview as Image", IconKeys.ViewImage, this._model.ViewAsImageCommand, RibbonItemSize.Small)),
      Group("Select",
        this.RibbonAction("Select All", IconKeys.SelectAll, this.SelectAllEntries, RibbonItemSize.Small, Keys.Control | Keys.A, "Select all (Ctrl+A)"),
        this.RibbonAction("Select None", IconKeys.SelectNone, this.SelectNoEntries, RibbonItemSize.Small, Keys.None, "Clear the selection"),
        this.RibbonAction("Invert Selection", IconKeys.SelectInvert, this.InvertEntrySelection, RibbonItemSize.Small, Keys.None, "Invert the selection")));
    return tab;
  }

  private RibbonTab ViewTab() {
    this._navigationPaneToggle = this.RibbonToggle("Navigation Pane", IconKeys.Folder, RibbonItemSize.Large, Keys.None, "Show the folder tree", @checked: true,
      on => this._split.Panel1Collapsed = !on);
    this._previewPaneToggle = this.RibbonToggle("Preview Pane", IconKeys.Preview, RibbonItemSize.Large, Keys.Alt | Keys.P, "Show the preview pane (Alt+P)", @checked: true,
      on => this.PreviewPaneVisible = on);
    this._detailsToggle = this.RibbonToggle("Details", IconKeys.ViewText, RibbonItemSize.Small, Keys.Control | Keys.Shift | Keys.D6, "Details (Ctrl+Shift+6)", @checked: true,
      _ => this.ChooseLayout(thumbnails: false));
    this._thumbnailsToggle = this.RibbonToggle("Thumbnails", IconKeys.ViewImage, RibbonItemSize.Small, Keys.Control | Keys.Shift | Keys.D2, "Thumbnails (Ctrl+Shift+2)", @checked: false,
      _ => this.ChooseLayout(thumbnails: true));

    var tab = new RibbonTab("View");
    tab.Groups.AddRange(
      Group("Panes", this._navigationPaneToggle, this._previewPaneToggle),
      Group("Layout", this._detailsToggle, this._thumbnailsToggle));
    return tab;
  }

  private RibbonTab ToolsTab() {
    var tab = new RibbonTab("Tools");
    tab.Groups.AddRange(
      Group("Analysis",
        this.RibbonCommand("Analyze File", IconKeys.Analyze, this._model.AnalyzeFileCommand, RibbonItemSize.Large, Keys.None, "Analyze any binary file"),
        this.RibbonCommand("Analyze Entry", IconKeys.Analyze, this._model.AnalyzeCommand, RibbonItemSize.Small, Keys.None, "Analyze the selected entry"),
        this.RibbonAction("Reverse Engineer", IconKeys.Analyze, () => new ReverseEngineerWindow().Show(), RibbonItemSize.Small, Keys.None, "Work out an unknown format")),
      Group("Disks and Images",
        this.RibbonAction("Maintenance", IconKeys.Defragment, this.OpenMaintenance, RibbonItemSize.Large, Keys.None, "Optimize, shrink, defragment, purge or wipe an image"),
        this.RibbonAction("Partitions", IconKeys.Create, this.OpenPartitionEditor, RibbonItemSize.Small, Keys.None, "Edit MBR and GPT partition tables"),
        this.RibbonAction("Mount", IconKeys.Open, this.OpenMountWindow, RibbonItemSize.Small, Keys.None, "Mount a filesystem image")),
      Group("Performance",
        this.RibbonCommand("Benchmark", IconKeys.Test, this._model.BenchmarkCommand, RibbonItemSize.Large, Keys.None, "Benchmark every building block")));
    return tab;
  }

  private RibbonTab ArchiveTab() {
    var tab = new RibbonTab("Archive");
    tab.Groups.AddRange(
      Group("Extract",
        this.RibbonCommand("Extract All", IconKeys.Extract, this._model.ExtractAllCommand, RibbonItemSize.Large, Keys.Control | Keys.E, "Extract everything (Ctrl+E)"),
        this.RibbonCommand("Extract Selected", IconKeys.ExtractSelected, this._model.ExtractSelectedCommand, RibbonItemSize.Small)),
      Group("Edit",
        this.RibbonCommand("Add Files", IconKeys.Add, this._model.AddFilesCommand, RibbonItemSize.Large, Keys.None, "Add files to the archive"),
        this.RibbonCommand("Test", IconKeys.Test, this._model.TestCommand, RibbonItemSize.Small, Keys.Control | Keys.T, "Test integrity (Ctrl+T)")),
      Group("Maintenance",
        this.RibbonCommand("Compact", IconKeys.Defragment, this._model.CompactEntryCommand, RibbonItemSize.Large, Keys.None, "Defragment, optimize and shrink in one pass"),
        this.RibbonCommand("Defragment", IconKeys.Defragment, this._model.DefragmentEntryCommand, RibbonItemSize.Small),
        this.RibbonCommand("Optimize", IconKeys.Analyze, this._model.OptimizeEntryCommand, RibbonItemSize.Small),
        this.RibbonCommand("Shrink", IconKeys.Defragment, this._model.ShrinkEntryCommand, RibbonItemSize.Small),
        this.RibbonCommand("Purge", IconKeys.Remove, this._model.PurgeEntryCommand, RibbonItemSize.Small),
        this.RibbonCommand("Wipe Free Space", IconKeys.Remove, this._model.WipeEntryCommand, RibbonItemSize.Small),
        this.RibbonCommand("Scramble", IconKeys.Defragment, this._model.ScrambleEntryCommand, RibbonItemSize.Small),
        this.RibbonCommand("Reconfigure", IconKeys.Properties, this._model.ReconfigureEntryCommand, RibbonItemSize.Small)));
    return tab;
  }

  // ── actions the ribbon adds ─────────────────────────────────────────────────────────────────

  private bool _choosingLayout;

  /// <summary>
  /// Details and Thumbnails behave as a pair of radio buttons: clicking either one picks it, and
  /// clicking the one already picked keeps it rather than flipping to the other.
  /// </summary>
  private void ChooseLayout(bool thumbnails) {
    if (this._choosingLayout) return;

    this._choosingLayout = true;
    try {
      if (this.ThumbnailsShown != thumbnails) this.ShowThumbnails(thumbnails);
      this._detailsToggle.Checked = !thumbnails;
      this._thumbnailsToggle.Checked = thumbnails;
    } finally {
      this._choosingLayout = false;
    }
  }

  /// <summary>Rereads what is shown — the folder or archive, and the tree's view of it.</summary>
  private void RefreshView() {
    this._model.RefreshCommand.Execute(null);
    if (this._tree.SelectedNode is { } node) this.ReloadChildren(node);
  }

  private void SelectAllEntries() => this.SetSelection(_ => true);
  private void SelectNoEntries() => this.SetSelection(_ => false);
  private void InvertEntrySelection() => this.SetSelection(item => !item.Selected);

  private void SetSelection(Func<ListViewItem, bool> selected) {
    foreach (var item in this._entries.Items)
      if (item.Tag is ArchiveEntryViewModel { IsParentEntry: false })
        item.Selected = selected(item);
      else
        item.Selected = false;
  }

  // ── item factories ──────────────────────────────────────────────────────────────────────────

  private static RibbonGroup Group(string text, params RibbonItem[] items) {
    var group = new RibbonGroup(text);
    group.Items.AddRange(items);
    return group;
  }

  /// <summary>A button bound to a view-model command: it runs the command and greys out when it cannot.</summary>
  private RibbonButton RibbonCommand(string text, string iconKey, ICommand command, RibbonItemSize size,
                              Keys shortcut = Keys.None, string? tip = null) {
    var button = this.RibbonAction(text, iconKey, () => {
      if (command.CanExecute(null)) command.Execute(null);
    }, size, shortcut, tip);
    command.CanExecuteChanged += (_, _) => button.Enabled = command.CanExecute(null);
    button.Enabled = command.CanExecute(null);
    return button;
  }

  private RibbonButton RibbonAction(string text, string iconKey, Action action, RibbonItemSize size,
                              Keys shortcut = Keys.None, string? tip = null) {
    var button = new RibbonButton(text, size) {
      Image = Images.Icon(iconKey, size == RibbonItemSize.Large ? LargeIcon : SmallIcon),
      ShortcutKeys = shortcut,
      ToolTipText = tip ?? text,
    };
    button.Click += (_, _) => action();
    return button;
  }

  private RibbonToggleButton RibbonToggle(string text, string iconKey, RibbonItemSize size, Keys shortcut, string tip, bool @checked,
                                    Action<bool> changed) {
    var toggle = new RibbonToggleButton(text, size) {
      Image = Images.Icon(iconKey, size == RibbonItemSize.Large ? LargeIcon : SmallIcon),
      ShortcutKeys = shortcut,
      ToolTipText = tip,
      Checked = @checked,
    };
    toggle.CheckedChanged += (_, _) => changed(toggle.Checked);
    return toggle;
  }
}
