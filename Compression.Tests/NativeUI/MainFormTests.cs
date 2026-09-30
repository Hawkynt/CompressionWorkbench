using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Compression.Lib;
using Compression.Mounting;
using Compression.NativeUI;
using Compression.NativeUI.Editing;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.ViewModels;
using Compression.NativeUI.Views;
using Hawkynt.NativeForms;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// Drives the shell against a headless backend. These cover the things that only go wrong once the
/// form is actually assembled and running — a control added to the tree twice, an accelerator
/// registered on top of a key a control needs, a context menu that closes itself the moment the
/// selection changes underneath it.
/// </summary>
/// <remarks>
/// The backend registry is process-wide state, so these run on their own.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class MainFormTests {
  private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

  /// <summary>
  /// Builds the shell and runs <paramref name="body"/> inside a message loop, because a form cannot
  /// be shown and a popup cannot be opened outside one.
  /// </summary>
  private static void WithShell(Action<MainForm> body) {
    HeadlessBackend.Install();

    Exception? failure = null;
    var ran = false;
    var shell = new MainForm([]);

    HeadlessBackend.WhileRunning = () => {
      ran = true;
      try {
        body(shell);
      } catch (Exception ex) {
        failure = ex;
      }
    };

    try {
      Application.Run(shell);
    } finally {
      HeadlessBackend.WhileRunning = null;
    }

    Assert.That(ran, Is.True, "the message loop never ran, so nothing was driven");
    if (failure is not null) throw failure;
  }

  private static T Field<T>(object target, string name) where T : class
    => target.GetType().GetField(name, Private)?.GetValue(target) as T
       ?? throw new InvalidOperationException($"{name} not found on {target.GetType().Name}");

  private static IEnumerable<Control> Descendants(Control root) {
    foreach (Control child in root.Controls) {
      yield return child;
      foreach (var grandchild in Descendants(child)) yield return grandchild;
    }
  }

  /// <summary>Every item of the shell's ribbon: the Quick Access Toolbar and every tab, contextual ones included.</summary>
  private static IEnumerable<RibbonItem> RibbonItems(MainForm shell) {
    var ribbon = Field<Ribbon>(shell, "_ribbon");
    foreach (var item in ribbon.QuickAccessItems) yield return item;
    foreach (var tab in ribbon.Tabs)
      foreach (var group in tab.Groups)
        foreach (var item in group.Items)
          if (item is RibbonItem ribbonItem) yield return ribbonItem;
  }

  private static RibbonItem RibbonItemNamed(MainForm shell, string text) => RibbonItems(shell).Single(i => i.Text == text);

  private static IEnumerable<ToolStripMenuItem> MenuItems(ToolStripItem item) {
    if (item is not ToolStripMenuItem menuItem) yield break;

    yield return menuItem;
    foreach (ToolStripItem child in menuItem.DropDownItems)
      foreach (var descendant in MenuItems(child))
        yield return descendant;
  }

  // ── layout ──────────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheShell_WhenItIsBuilt_ThenNoControlIsAddedToTheFormTwice() {
    WithShell(shell => {
      var duplicated = shell.Controls
        .Cast<Control>()
        .GroupBy(c => c)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key.GetType().Name)
        .ToList();

      Assert.That(duplicated, Is.Empty,
        "a control in the tree twice is laid out and painted twice");
    });
  }

  [Test]
  public void GivenTheShell_WhenItIsBuilt_ThenEveryExpectedRegionIsPresentExactlyOnce() {
    WithShell(shell => {
      foreach (var name in new[] { "_ribbon", "_breadcrumbBar", "_split", "_status", "_dropOverlay" }) {
        var control = Field<Control>(shell, name);
        Assert.That(shell.Controls.Cast<Control>().Count(c => ReferenceEquals(c, control)), Is.EqualTo(1),
          $"{name} should appear exactly once in the form's controls");
      }

      // The two panes live inside the split, once each, not on the form.
      foreach (var name in new[] { "_tree", "_entries" }) {
        var control = Field<Control>(shell, name);
        Assert.That(Descendants(shell).Count(c => ReferenceEquals(c, control)), Is.EqualTo(1),
          $"{name} should appear exactly once below the form");
      }
    });
  }

  // ── accelerators ────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheRibbon_WhenShortcutsAreCollected_ThenNoKeyIsClaimedTwice() {
    WithShell(shell => {
      var claims = RibbonItems(shell)
        .Where(i => i.ShortcutKeys != Keys.None)
        .GroupBy(i => i.ShortcutKeys)
        .Where(g => g.Count() > 1)
        .Select(g => $"{g.Key}: {string.Join(" & ", g.Select(i => i.Text))}")
        .ToList();

      Assert.That(claims, Is.Empty, "two items firing on one key means one of them never fires");
    });
  }

  /// <summary>
  /// Enter activates the selected entry in the list. It is shown beside "View as Text" as a hint,
  /// which is display text and must not also be registered as an accelerator — doing so takes the
  /// key away from the list, where it does the thing the user expects.
  /// </summary>
  [Test]
  public void GivenViewAsText_WhenTheRibbonIsBuilt_ThenEnterIsShownButNotRegistered() {
    WithShell(shell => {
      var item = RibbonItemNamed(shell, "View as Text");

      Assert.Multiple(() => {
        Assert.That(item.ShortcutKeys, Is.EqualTo(Keys.None), "Enter belongs to the entry list");
        Assert.That(item.ToolTipText, Does.Contain("Enter"), "but it is still advertised");
      });
    });
  }

  [TestCase("Up", Keys.Back)]
  [TestCase("Back", Keys.Alt | Keys.Left)]
  [TestCase("Forward", Keys.Alt | Keys.Right)]
  [TestCase("Refresh", Keys.F5)]
  [TestCase("Delete", Keys.Delete)]
  [TestCase("Open", Keys.Control | Keys.O)]
  [TestCase("Create", Keys.Control | Keys.N)]
  [TestCase("Extract All", Keys.Control | Keys.E)]
  [TestCase("Test", Keys.Control | Keys.T)]
  [TestCase("Properties", Keys.Alt | Keys.Enter)]
  [TestCase("New Folder", Keys.Control | Keys.Shift | Keys.N)]
  [TestCase("Select All", Keys.Control | Keys.A)]
  [TestCase("Preview Pane", Keys.Alt | Keys.P)]
  public void GivenACommand_WhenTheRibbonIsBuilt_ThenItCarriesItsShortcut(string label, Keys expected) {
    WithShell(shell => {
      Assert.That(RibbonItemNamed(shell, label).ShortcutKeys, Is.EqualTo(expected));
    });
  }

  /// <summary>
  /// The same command appears in both the menu bar and the entry context menu. Only one of the two
  /// may own the key, or the accelerator is registered twice for one action.
  /// </summary>
  [Test]
  public void GivenTheContextMenu_WhenItRepeatsAMenuBarAction_ThenItOnlyAdvertisesTheKey() {
    WithShell(shell => {
      var duplicated = Field<ContextMenuStrip>(shell, "_entryMenu").Items
        .Cast<ToolStripItem>()
        .SelectMany(MenuItems)
        .Where(i => i.ShortcutKeys != Keys.None)
        .Select(i => $"{i.Text} -> {i.ShortcutKeys}")
        .ToList();

      Assert.That(duplicated, Is.Empty, "the ribbon owns these keys; the context menu shows them");
    });
  }

  // ── the context menu, while the shell churns underneath it ──────────────────────────────────

  [Test]
  public void GivenAnOpenContextMenu_WhenTheShellRequeriesItsCommands_ThenItStaysOpen() {
    WithShell(shell => {
      var (menu, popup, closedCount) = OpenEntryMenu(shell);
      var items = menu.Items.Cast<ToolStripItem>().ToList();

      // Every item assigns Enabled from its command's CanExecuteChanged, so a requery writes to all
      // of them. Selecting a different entry triggers exactly this, with the menu still up.
      CommandManager.InvalidateRequerySuggested();
      AssertStillOpen(menu, popup, closedCount, "a command requery");

      shell.GetType().GetMethod("SyncFromModel", Private)!.Invoke(shell, null);
      AssertStillOpen(menu, popup, closedCount, "a model sync");

      for (var i = 0; i < 20; ++i) CommandManager.InvalidateRequerySuggested();
      AssertStillOpen(menu, popup, closedCount, "twenty rapid requeries");

      foreach (var item in items) item.Enabled = !item.Enabled;
      foreach (var item in items) item.Enabled = !item.Enabled;
      AssertStillOpen(menu, popup, closedCount, "every item's Enabled being written");

      menu.Close();
    });
  }

  [Test]
  public void GivenTheEntryList_WhenItIsRightClicked_ThenTheEntryMenuOpens() {
    WithShell(shell => {
      var (menu, popup, _) = OpenEntryMenu(shell);

      Assert.Multiple(() => {
        Assert.That(menu.IsOpen, Is.True);
        Assert.That(popup.ShowCount, Is.EqualTo(1), "the popup should be shown once");
        Assert.That(popup.LastSize.Width, Is.GreaterThan(0), "and sized to its content");
        Assert.That(popup.LastSize.Height, Is.GreaterThan(0));
      });

      menu.Close();
    });
  }

  [Test]
  public void GivenAnOpenContextMenu_WhenTheUserClicksAway_ThenItCloses() {
    WithShell(shell => {
      var (menu, popup, _) = OpenEntryMenu(shell);

      // The platform asks whether the press belongs to the popup, then reports the dismissal.
      popup.OutsidePress?.Invoke(new(4000, 4000));
      if (menu.IsOpen) popup.RaiseDismissed();

      Assert.That(menu.IsOpen, Is.False, "a click outside has to take the menu down");
    });
  }

  [Test]
  public void GivenAnOpenContextMenu_WhenARowIsClicked_ThenTheItemDrawnThereIsTheOneActivated() {
    WithShell(shell => {
      var menu = Field<ContextMenuStrip>(shell, "_entryMenu");
      var original = menu.Items.Cast<ToolStripItem>().ToList();

      // The shell's own items open windows when activated, so hit-testing is measured against inert
      // stand-ins and the real menu is put back afterwards.
      var fired = new List<int>();
      var probes = Enumerable.Range(0, 5).Select(i => {
        var item = new ToolStripMenuItem($"probe {i}");
        item.Click += (_, _) => fired.Add(i);
        return item;
      }).ToArray();

      menu.Items.Clear();
      menu.Items.AddRange(probes);

      try {
        var peer = PeerOf(Field<Control>(shell, "_entries"));
        peer.RaiseContextMenuRequested(new(40, 30));
        Assert.That(menu.IsOpen, Is.True, "the menu should reopen with the stand-ins");

        var height = RecordingPopup.All[^1].LastSize.Height;
        var activated = new List<int>();

        for (var y = 0; y < height; y += 2) {
          if (!menu.IsOpen) peer.RaiseContextMenuRequested(new(40, 30));

          var popup = RecordingPopup.All[^1];
          fired.Clear();
          popup.RaiseMouseMove(20, y);
          popup.RaiseClick(20, y);
          if (fired.Count > 0 && (activated.Count == 0 || activated[^1] != fired[0])) activated.Add(fired[0]);
        }

        Assert.That(activated, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }),
          "clicking down the popup should activate its items top to bottom, each exactly once");
      } finally {
        if (menu.IsOpen) menu.Close();
        menu.Items.Clear();
        menu.Items.AddRange([.. original]);
      }
    });
  }

  /// <summary>
  /// With nothing selected most entry commands cannot run, and their items are disabled. Clicking
  /// one anyway must do nothing rather than execute against no selection.
  /// </summary>
  [Test]
  public void GivenADisabledMenuItem_WhenItIsClicked_ThenNothingHappens() {
    WithShell(shell => {
      var menu = Field<ContextMenuStrip>(shell, "_entryMenu");
      var disabled = menu.Items.Cast<ToolStripItem>().FirstOrDefault(i => !i.Enabled);

      Assert.That(disabled, Is.Not.Null, "with no selection, some entry commands should be unavailable");
      Assert.DoesNotThrow(() => disabled!.PerformClick());
    });
  }

  // ── navigation pane ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// A scratch folder holding a sub-folder and a zip with folders inside it, with the settings file
  /// redirected there so opening the archive does not touch the real profile.
  /// </summary>
  private static void WithScratch(Action<string, string> body) {
    var root = Path.Combine(Path.GetTempPath(), "cwb-tree-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "sub"));
    UserSettings.PathOverride = Path.Combine(root, "settings.json");
    FormatRegistration.EnsureInitialized();

    var zip = Path.Combine(root, "bundle.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) {
      archive.CreateEntry("docs/guide/intro.txt");
      archive.CreateEntry("top.txt");
    }

    try {
      body(root, zip);
    } finally {
      UserSettings.PathOverride = null;
      try { Directory.Delete(root, recursive: true); } catch { }
    }
  }

  private static Location? SelectedPlace(MainForm shell)
    => Field<TreeView>(shell, "_tree").SelectedNode?.Tag as Location;

  [Test]
  public void GivenTheShell_WhenItIsBuilt_ThenTheTreeStartsAtTheFilesystemRoots() {
    WithShell(shell => {
      var roots = Field<TreeView>(shell, "_tree").Nodes.Cast<TreeNode>().ToList();

      Assert.That(roots, Is.Not.Empty);
      Assert.That(roots.All(n => n.Tag is Location { IsInArchive: false }), Is.True,
        "every root is a host folder the shell can go to");
      Assert.That(roots.Any(n => n.Tag is Location l && Directory.Exists(l.HostPath) && Path.GetPathRoot(l.HostPath) == l.HostPath), Is.True,
        "at least one root is a drive or /");
    });
  }

  [Test]
  public void GivenAHostFolder_WhenTheShellGoesThere_ThenTheTreeFollowsToThatFolder() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      var sub = Path.Combine(root, "sub");

      model.NavigateTo(Location.Folder(sub));

      Assert.That(SelectedPlace(shell), Is.EqualTo(Location.Folder(sub)),
        "the tree expands down to wherever the shell went, however it got there");
    }));
  }

  [Test]
  public void GivenAHiddenFolder_WhenTheShellGoesThere_ThenTheTreeShowsItAnyway() {
    WithScratch((root, _) => WithShell(shell => {
      var hidden = Path.Combine(root, ".cache", "inner");
      Directory.CreateDirectory(hidden);

      Field<MainViewModel>(shell, "_model").NavigateTo(Location.Folder(hidden));

      Assert.That(SelectedPlace(shell), Is.EqualTo(Location.Folder(hidden)),
        "the listing leaves hidden folders out, but not the one the user is standing in");
    }));
  }

  [Test]
  public void GivenAFolderInsideAnArchive_WhenTheShellGoesThere_ThenTheTreeShowsTheArchiveAsAFolder() {
    WithScratch((root, zip) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      var target = Location.InArchive(zip, "docs/guide/");

      model.NavigateTo(target);

      Assert.That(SelectedPlace(shell), Is.EqualTo(target));
      var archiveNode = Field<TreeView>(shell, "_tree").SelectedNode!.Parent!.Parent!;
      Assert.That(archiveNode.Text, Is.EqualTo("bundle.zip"));
    }));
  }

  [Test]
  public void GivenATreeNode_WhenTheUserSelectsIt_ThenTheShellGoesThere() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(root));
      var tree = Field<TreeView>(shell, "_tree");
      var here = tree.SelectedNode!;
      here.Expand();
      var sub = here.Nodes.Cast<TreeNode>().Single(n => n.Text == "sub");

      tree.SelectedNode = sub;

      Assert.That(model.CurrentLocation, Is.EqualTo(Location.Folder(Path.Combine(root, "sub"))));
      Assert.That(model.BackCommand.CanExecute(null), Is.True, "a tree click is a visit like any other");
    }));
  }

  [Test]
  public void GivenTheShellFollowedByTheTree_WhenItArrives_ThenTheArrivalIsRecordedOnce() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(root));
      model.NavigateTo(Location.Folder(Path.Combine(root, "sub")));

      model.BackCommand.Execute(null);

      Assert.That(model.CurrentLocation, Is.EqualTo(Location.Folder(root)),
        "the tree moving to match must not itself count as a second navigation");

      Assert.That(model.ForwardCommand.CanExecute(null), Is.True);
    }));
  }

  // ── rename ──────────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheEntryList_WhenBuilt_ThenTheEditableTextOfEachRowIsItsName() {
    WithScratch((root, _) => WithShell(shell => {
      Field<MainViewModel>(shell, "_model").NavigateTo(Location.Folder(root));
      var list = Field<ListView>(shell, "_entries");

      Assert.Multiple(() => {
        Assert.That(list.LabelEdit, Is.True);
        Assert.That(list.Columns[0].Text, Is.EqualTo("Name"), "label editing edits the first column");
        Assert.That(list.Items.Cast<ListViewItem>().Select(i => i.Text), Does.Contain("sub"));
      });
    }));
  }

  [Test]
  public void GivenTheRenameCommands_WhenBuilt_ThenF2IsShownButLeftToTheList() {
    WithShell(shell => {
      var ribbon = RibbonItemNamed(shell, "Rename");
      var menu = Field<ContextMenuStrip>(shell, "_entryMenu").Items.Cast<ToolStripItem>().SelectMany(MenuItems).Single(i => i.Text == "Rena&me");

      Assert.That((ribbon.ShortcutKeys, menu.ShortcutKeys), Is.EqualTo((Keys.None, Keys.None)),
        "the list starts editing on F2 itself; a shortcut would take the key away from it");
      Assert.That(ribbon.ToolTipText, Does.Contain("F2"));
      Assert.That(menu.ShortcutKeyDisplayString, Is.EqualTo("F2"));
    });
  }

  [Test]
  public void GivenASelectedEntry_WhenRenameIsInvoked_ThenTheListStartsEditingThatRow() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(root));
      var list = Field<ListView>(shell, "_entries");
      var row = list.Items.Cast<ListViewItem>().First(i => i.Text == "sub");
      model.SelectedEntries.Add((ArchiveEntryViewModel)row.Tag!);

      model.RenameCommand.Execute(null);

      var editor = list.Controls.OfType<TextBox>().SingleOrDefault();
      Assert.That(editor, Is.Not.Null.And.Property(nameof(Control.Visible)).True);
      Assert.That(editor!.Text, Is.EqualTo("sub"));
    }));
  }

  [Test]
  public void GivenAFolderOnDisk_WhenANewFolderIsMade_ThenTheListOpensItsNameForEditing() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(root));

      model.NewFolderCommand.Execute(null);

      var editor = Field<ListView>(shell, "_entries").Controls.OfType<TextBox>().SingleOrDefault();
      Assert.That(editor?.Visible, Is.True);
      Assert.That(editor!.Text, Is.EqualTo("New folder"));
    }));
  }

  // ── preview pane ────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenThePreviewPane_WhenToggledFromTheRibbon_ThenItHidesAndReturns() {
    WithShell(shell => {
      var toggle = (RibbonToggleButton)RibbonItemNamed(shell, "Preview Pane");
      Assert.That(shell.PreviewPaneVisible, Is.True, "a file manager opens with its preview showing");

      toggle.PerformClick();
      Assert.That((shell.PreviewPaneVisible, toggle.Checked), Is.EqualTo((false, false)));

      toggle.PerformClick();
      Assert.That((shell.PreviewPaneVisible, toggle.Checked), Is.EqualTo((true, true)));
    });
  }

  [Test]
  public void GivenAFolderSelected_WhenThePaneRefreshes_ThenItSaysFolderWithoutReadingAnything() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(root));
      var list = Field<ListView>(shell, "_entries");

      list.Items.Cast<ListViewItem>().First(i => i.Text == "sub").Selected = true;

      var pane = Field<Compression.NativeUI.Controls.PreviewPane>(shell, "_preview");
      Assert.That(pane.Caption, Does.StartWith("sub").And.EndWith("Folder"));
    }));
  }

  [Test]
  public void GivenTheViewTab_WhenThumbnailsAreChosen_ThenTheListShowsLargeIconsAndDetailsBringsTheColumnsBack() {
    WithScratch((root, _) => WithShell(shell => {
      Field<MainViewModel>(shell, "_model").NavigateTo(Location.Folder(root));
      var details = (RibbonToggleButton)RibbonItemNamed(shell, "Details");
      var thumbnails = (RibbonToggleButton)RibbonItemNamed(shell, "Thumbnails");
      var list = Field<ListView>(shell, "_entries");

      thumbnails.PerformClick();
      Assert.Multiple(() => {
        Assert.That(list.View, Is.EqualTo(ListViewView.LargeIcon));
        Assert.That(list.LargeImageList, Is.Not.Null);
        Assert.That((details.Checked, thumbnails.Checked), Is.EqualTo((false, true)));
        Assert.That(list.Items.Cast<ListViewItem>().Single(i => i.Text == "sub").ImageKey, Is.EqualTo("Folder"),
          "an entry without a picture keeps its icon, at the large size");
      });

      details.PerformClick();
      Assert.That(list.View, Is.EqualTo(ListViewView.Details));
      Assert.That((details.Checked, thumbnails.Checked), Is.EqualTo((true, false)));

      details.PerformClick();
      Assert.That((list.View, details.Checked, thumbnails.Checked), Is.EqualTo((ListViewView.Details, true, false)),
        "clicking the layout already chosen keeps it, as a radio button does");
    }));
  }

  // ── ribbon ──────────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheShell_WhenBuilt_ThenEveryCommandOfTheOldMenuBarIsOnTheRibbon() {
    WithShell(shell => {
      var labels = RibbonItems(shell).Select(i => i.Text).ToHashSet();
      string[] expected = [
        "Open", "Create", "Convert", "File Associations", "About", "Exit",
        "Paste", "Cut", "Copy", "New Folder", "Rename", "Delete", "Properties", "View as Text", "View as Hex",
        "Navigation Pane", "Preview Pane", "Details", "Thumbnails",
        "Analyze File", "Analyze Entry", "Reverse Engineer", "Maintenance", "Partitions", "Mount", "Benchmark",
        "Extract All", "Extract Selected", "Add Files", "Test",
        "Back", "Forward", "Up", "Refresh",
      ];

      Assert.That(expected.Where(e => !labels.Contains(e)), Is.Empty, "a command the menu bar had must not be lost");
    });
  }

  [Test]
  public void GivenTheArchiveToolsTab_WhenTheShellMovesInAndOutOfAnArchive_ThenItAppearsOnlyInside() {
    WithScratch((root, zip) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      var tools = Field<RibbonContextualTabGroup>(shell, "_archiveTools");

      model.NavigateTo(Location.Folder(root));
      Assert.That(tools.Visible, Is.False, "a folder on disk is not an archive");

      model.NavigateTo(Location.InArchive(zip, "docs/"));
      Assert.That(tools.Visible, Is.True);

      model.NavigateTo(Location.Folder(root));
      Assert.That(tools.Visible, Is.False);
    }));
  }

  [Test]
  public void GivenTheNavigationPaneToggle_WhenSwitchedOff_ThenTheTreeFoldsAwayAndReturns() {
    WithShell(shell => {
      var toggle = (RibbonToggleButton)RibbonItemNamed(shell, "Navigation Pane");
      var split = Field<SplitContainer>(shell, "_split");

      toggle.PerformClick();
      Assert.That(split.Panel1Collapsed, Is.True);

      toggle.PerformClick();
      Assert.That(split.Panel1Collapsed, Is.False);
    });
  }

  [Test]
  public void GivenAFolder_WhenSelectAllNoneAndInvertAreUsed_ThenTheSelectionFollowsButTheParentRowIsNeverPicked() {
    WithScratch((root, _) => WithShell(shell => {
      Field<MainViewModel>(shell, "_model").NavigateTo(Location.Folder(root));
      var list = Field<ListView>(shell, "_entries");
      var selectable = list.Items.Cast<ListViewItem>().Count(i => i.Text != "..");

      RibbonItemNamed(shell, "Select All").PerformClick();
      Assert.That(list.SelectedItems.Count(), Is.EqualTo(selectable));
      Assert.That(list.Items.Cast<ListViewItem>().Single(i => i.Text == "..").Selected, Is.False);

      RibbonItemNamed(shell, "Invert Selection").PerformClick();
      Assert.That(list.SelectedItems.Count(), Is.Zero);

      RibbonItemNamed(shell, "Invert Selection").PerformClick();
      RibbonItemNamed(shell, "Select None").PerformClick();
      Assert.That(list.SelectedItems.Count(), Is.Zero);
    }));
  }

  [Test]
  public void GivenAFileAddedBehindTheShellsBack_WhenRefreshIsPressed_ThenItAppears() {
    WithScratch((root, _) => WithShell(shell => {
      Field<MainViewModel>(shell, "_model").NavigateTo(Location.Folder(root));
      File.WriteAllText(Path.Combine(root, "late.txt"), "late");

      RibbonItemNamed(shell, "Refresh").PerformClick();

      Assert.That(Field<ListView>(shell, "_entries").Items.Cast<ListViewItem>().Select(i => i.Text), Does.Contain("late.txt"));
    }));
  }

  [Test]
  public void GivenTheListHasFocus_WhenF5IsPressed_ThenTheRibbonsRefreshRunsWithNoMenuBarInvolved() {
    WithScratch((root, _) => WithShell(shell => {
      Field<MainViewModel>(shell, "_model").NavigateTo(Location.Folder(root));
      var list = Field<ListView>(shell, "_entries");
      list.Focus();
      File.WriteAllText(Path.Combine(root, "late.txt"), "late");

      PeerOf(list).RaiseKeyDown(Keys.F5);

      Assert.That(list.Items.Cast<ListViewItem>().Select(i => i.Text), Does.Contain("late.txt"),
        "the key reaches the ribbon through the form's shortcut chain");
    }));
  }

  // ── views stay fresh ────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAnExpandedTreeNode_WhenANewFolderIsMadeThere_ThenTheTreeShowsIt() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(root));
      var tree = Field<TreeView>(shell, "_tree");
      var node = tree.SelectedNode!;
      node.Expand();
      Assume.That(node.Nodes.Count, Is.GreaterThan(0), "the scratch folder has a sub-folder, so the node has children to reread");

      model.CreateNewFolder();

      Assert.That(node.Nodes.Cast<TreeNode>().Select(n => n.Text), Does.Contain("New folder"));
    }));
  }

  [Test]
  public void GivenTheTreeHasFocus_WhenCtrlCIsPressed_ThenTheTreesFolderIsOnTheClipboardNotTheListsSelection() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(Path.Combine(root, "sub")));

      PeerOf(Field<Control>(shell, "_tree")).RaiseKeyDown(Keys.C, KeyModifiers.Control);

      var clipboard = (IReadOnlyList<TransferItem>)typeof(MainViewModel).GetField("_clipboardItems", Private)!.GetValue(model)!;
      Assert.That(clipboard, Is.EqualTo(new[] { new TransferItem(Location.Folder(root), "sub", true) }));
    }));
  }

  // ── drag and drop ───────────────────────────────────────────────────────────────────────────

  private static DragDropEffects EffectOf(MainForm shell, object data, Location? target)
    => (DragDropEffects)typeof(MainForm).GetMethod("EffectFor", Private)!.Invoke(shell, [data, target])!;

  [Test]
  public void GivenEntriesDraggedWithinTheDisk_WhenOverAnotherFolder_ThenTheyWouldMove() {
    WithScratch((root, _) => WithShell(shell => {
      var items = new[] { new TransferItem(Location.Folder(root), "bundle.zip", false) };

      Assert.That(EffectOf(shell, new ShellDragData(items), Location.Folder(Path.Combine(root, "sub"))), Is.EqualTo(DragDropEffects.Move));
    }));
  }

  [Test]
  public void GivenEntriesDraggedOverTheFolderTheyAreIn_WhenAsked_ThenNothingIsOffered() {
    WithScratch((root, _) => WithShell(shell => {
      var items = new[] { new TransferItem(Location.Folder(root), "bundle.zip", false) };

      Assert.That(EffectOf(shell, new ShellDragData(items), Location.Folder(root)), Is.EqualTo(DragDropEffects.None));
    }));
  }

  [Test]
  public void GivenFilesFromOutside_WhenOverAFolder_ThenTheyWouldBeCopiedNotMoved() {
    WithScratch((root, zip) => WithShell(shell => {
      Assert.That(EffectOf(shell, new[] { zip }, Location.Folder(Path.Combine(root, "sub"))), Is.EqualTo(DragDropEffects.Copy),
        "the application they came from still expects them");
      Assert.That(EffectOf(shell, new[] { Path.Combine(root, "missing.txt") }, Location.Folder(Path.Combine(root, "sub"))), Is.EqualTo(DragDropEffects.None));
    }));
  }

  [Test]
  public void GivenAFolderDraggedIntoItself_WhenAsked_ThenNothingIsOffered() {
    WithScratch((root, _) => WithShell(shell => {
      Directory.CreateDirectory(Path.Combine(root, "sub", "inner"));
      var items = new[] { new TransferItem(Location.Folder(root), "sub", true) };

      Assert.That(EffectOf(shell, new ShellDragData(items), Location.Folder(Path.Combine(root, "sub", "inner"))), Is.EqualTo(DragDropEffects.None));
    }));
  }

  [Test]
  public void GivenTheShellsOwnFileListDrag_WhenOverAnotherFolderOfTheSameArchive_ThenItStillMoves() {
    WithScratch((root, zip) => WithShell(shell => {
      var items = new[] { new TransferItem(Location.InArchive(zip, ""), "top.txt", false) };
      var files = new[] { Path.Combine(root, "sub") };   // stands in for the extracted copies
      typeof(MainForm).GetField("_outgoingDrag", Private)!.SetValue(shell, ((object Payload, System.Collections.Generic.IReadOnlyList<TransferItem> Items)?)(files, items));

      Assert.That(EffectOf(shell, files, Location.InArchive(zip, "docs/")), Is.EqualTo(DragDropEffects.Move),
        "inside the window the extracted files still mean the entries they came from");
      Assert.That(EffectOf(shell, new[] { Path.Combine(root, "sub") }, Location.InArchive(zip, "docs/")), Is.EqualTo(DragDropEffects.Copy),
        "an equal list from somewhere else is not the shell's own drag");
    }));
  }

  [Test]
  public void GivenFilesWithNoPathFromAnotherApplication_WhenOverAFolder_ThenTheyWouldBeCopiedIn() {
    WithScratch((root, zip) => WithShell(shell => {
      var attachment = new[] { new VirtualFile("invoice.pdf", () => new MemoryStream()) };

      Assert.Multiple(() => {
        Assert.That(EffectOf(shell, attachment, Location.Folder(Path.Combine(root, "sub"))), Is.EqualTo(DragDropEffects.Copy));
        Assert.That(EffectOf(shell, attachment, Location.InArchive(zip, "docs/")), Is.EqualTo(DragDropEffects.Copy));
        Assert.That(EffectOf(shell, attachment, Location.Folder(Path.Combine(root, "gone"))), Is.EqualTo(DragDropEffects.None));
      });
    }));
  }

  [Test]
  public void GivenTheShell_WhenBuilt_ThenTheListAndTheTreeTakeDrops() {
    WithShell(shell => {
      Assert.That(Field<Control>(shell, "_entries").AllowDrop, Is.True);
      Assert.That(Field<Control>(shell, "_tree").AllowDrop, Is.True);
    });
  }

  // ── clipboard ───────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenASelectedEntry_WhenCtrlCIsPressedInTheList_ThenItIsOnTheClipboard() {
    WithScratch((root, _) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.Folder(root));
      model.SelectedEntries.Add(model.Entries.First(e => e.Name == "sub"));

      PeerOf(Field<Control>(shell, "_entries")).RaiseKeyDown(Keys.C, KeyModifiers.Control);

      Assert.That(model.HasClipboard, Is.True);
    }));
  }

  [Test]
  public void GivenTheClipboardCommands_WhenTheRibbonIsBuilt_ThenTheirKeysAreShownButLeftToTheFileViews() {
    WithShell(shell => {
      var items = RibbonItems(shell).Where(i => i.Text is "Cut" or "Copy" or "Paste").ToList();

      Assert.That(items, Has.Count.EqualTo(3));
      Assert.That(items.Select(i => i.ToolTipText), Is.EquivalentTo(new[] { "Cut (Ctrl+X)", "Copy (Ctrl+C)", "Paste (Ctrl+V)" }));
      Assert.That(items.All(i => i.ShortcutKeys == Keys.None), Is.True,
        "registered on the ribbon, Ctrl+V would paste files while a name is being typed");
    });
  }

  [Test]
  public void GivenTheAddressBar_WhenOpenedForEditing_ThenItHoldsTheRealPath() {
    WithScratch((_, zip) => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      model.NavigateTo(Location.InArchive(zip, "docs/"));
      var bar = Field<Breadcrumb>(shell, "_breadcrumb");

      Assert.That(bar.Editable, Is.True);
      Assert.That(bar.PathComposer!(), Is.EqualTo(zip + Path.DirectorySeparatorChar + "docs/"),
        "the archive's own separator is kept, so the typed path resolves back to the same place");
    }));
  }

  private static (ContextMenuStrip Menu, RecordingPopup Popup, int ClosedCount) OpenEntryMenu(MainForm shell) {
    var menu = Field<ContextMenuStrip>(shell, "_entryMenu");
    var closed = 0;
    menu.Closed += (_, _) => ++closed;

    PeerOf(Field<Control>(shell, "_entries")).RaiseContextMenuRequested(new(40, 30));
    Assert.That(menu.IsOpen, Is.True, "a right-click on the entry list should open its menu");

    return (menu, RecordingPopup.All[^1], closed);
  }

  private static void AssertStillOpen(ContextMenuStrip menu, RecordingPopup popup, int closedBefore, string what) {
    Assert.Multiple(() => {
      Assert.That(menu.IsOpen, Is.True, $"the menu closed itself on {what}");
      Assert.That(popup.HideCount, Is.Zero, $"the popup was hidden on {what}");
      Assert.That(popup.Shown, Is.True, $"the popup stopped being shown on {what}");
    });
  }

  /// <summary>
  /// Finds the platform peer behind a control, which is where the platform's own events come in.
  /// The field holding it is not part of any contract, so the search is by value, not declared type.
  /// </summary>
  private static RecordingControlPeer PeerOf(Control control) {
    for (var type = control.GetType(); type is not null; type = type.BaseType)
      foreach (var field in type.GetFields(Private))
        if (field.GetValue(control) is RecordingControlPeer peer)
          return peer;

    throw new InvalidOperationException($"{control.GetType().Name} has no peer");
  }
}
