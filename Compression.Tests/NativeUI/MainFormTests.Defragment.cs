using System;
using System.IO;
using System.Linq;
using Compression.Core.Layout;
using Compression.Lib;
using Compression.NativeUI;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Navigation;
using Compression.NativeUI.ViewModels;
using Compression.NativeUI.Views;
using Hawkynt.NativeForms;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>The Defragment tab, driven through the assembled shell against real images.</summary>
public sealed partial class MainFormTests {
  private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

  /// <summary>A scratch folder holding the maintenance fixtures, removed afterwards.</summary>
  private static void WithImages(Action<string> body) {
    var root = MaintenanceFixtures.Scratch();
    UserSettings.PathOverride = Path.Combine(root, "settings.json");
    FormatRegistration.EnsureInitialized();
    try {
      body(root);
    } finally {
      UserSettings.PathOverride = null;
      try { Directory.Delete(root, recursive: true); } catch { }
    }
  }

  private static RibbonContextualTabGroup DiskTools(MainForm shell) => Field<RibbonContextualTabGroup>(shell, "_diskTools");
  private static RibbonTab DefragTab(MainForm shell) => Field<RibbonTab>(shell, "_defragTab");
  private static DefragmentView DefragView(MainForm shell) => Field<DefragmentView>(shell, "_defragView");
  private static T Item<T>(MainForm shell, string text) where T : RibbonItem
    => DefragTab(shell).Groups.SelectMany(g => g.Items.Cast<ToolStripItem>()).OfType<T>().Single(i => i.Text == text);
  private static RibbonToggleButton Toggle(MainForm shell, string text) => Item<RibbonToggleButton>(shell, text);

  /// <summary>Opens <paramref name="image"/>, selects the Defragment tab and waits for the first map.</summary>
  private static void OpenOnDefragmentTab(MainForm shell, string image) {
    shell.OpenArchive(image);
    RibbonItemNamed(shell, "Maintenance").PerformClick();
    Assert.That(Field<Ribbon>(shell, "_ribbon").SelectedTab, Is.SameAs(DefragTab(shell)), "Maintenance opens the Defragment tab");
    Settle(shell);
  }

  /// <summary>Waits for whatever the tab started — an operation, then the analysis it triggers.</summary>
  private static void Settle(MainForm shell) {
    Assert.That(shell.MaintenanceTask?.Wait(Patience) ?? true, Is.True, "the operation finished");
    Assert.That(shell.AnalysisTask?.Wait(Patience) ?? true, Is.True, "the analysis finished");
  }

  // ── the tab comes and goes with a target ────────────────────────────────────────────────────

  [Test]
  public void GivenNoImage_WhenTheShellStarts_ThenTheDiskToolsTabIsHiddenAndMaintenanceIsDisabled() {
    WithImages(_ => WithShell(shell => {
      Assert.That(DiskTools(shell).Visible, Is.False);
      Assert.That(RibbonItemNamed(shell, "Maintenance").Enabled, Is.False, "nothing to maintain");
    }));
  }

  [Test]
  public void GivenAFatImage_WhenOpenedAndThenLeftForAFolder_ThenTheTabAppearsAndGoes() {
    WithImages(root => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");

      shell.OpenArchive(MaintenanceFixtures.FragmentedFat(root));
      Assert.That(DiskTools(shell).Visible, Is.True, "an open FAT image can be maintained");

      model.NavigateTo(Location.Folder(Path.Combine(root)));
      model.SelectedEntries.Clear();
      CommandManager.InvalidateRequerySuggested();
      Assert.That(DiskTools(shell).Visible, Is.False, "a folder with nothing selected has nothing to maintain");
    }));
  }

  [Test]
  public void GivenAPartitionedDisk_WhenOpened_ThenNoMaintenanceIsOffered() {
    WithImages(root => WithShell(shell => {
      shell.OpenArchive(MaintenanceFixtures.PartitionedDisk(root));

      Assert.That(Field<MainViewModel>(shell, "_model").Format, Is.EqualTo("PartitionedDisk"));
      Assert.That(DiskTools(shell).Visible, Is.False, "a partitioned disk is read-only as a whole");
      Assert.That(RibbonItemNamed(shell, "Maintenance").Enabled, Is.False);
    }));
  }

  // ── the client area ─────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheBrowser_WhenTheDefragmentTabIsEnteredAndLeft_ThenTheBlockViewTakesThePanesPlaceAndTheBrowserReturnsAsItWas() {
    WithImages(root => WithShell(shell => {
      var split = Field<SplitContainer>(shell, "_split");
      var ribbon = Field<Ribbon>(shell, "_ribbon");
      var model = Field<MainViewModel>(shell, "_model");
      shell.OpenArchive(MaintenanceFixtures.FragmentedFat(root));
      split.Panel1Collapsed = true;
      var (distance, location, bounds) = (split.SplitterDistance, model.CurrentLocation, split.Bounds);

      OpenOnDefragmentTab(shell, model.ArchivePath);
      var view = DefragView(shell);
      Assert.Multiple(() => {
        Assert.That(split.Visible, Is.False, "the browser gives up the client area");
        Assert.That(view.Visible, Is.True);
        Assert.That(view.Bounds, Is.EqualTo(bounds), "the block view gets the whole client area the panes had");
        Assert.That(view.Map.BlockMap, Is.Not.Null.And.Not.Empty, "the map is drawn on entry");
      });

      ribbon.SelectedIndex = 1;
      Assert.Multiple(() => {
        Assert.That(view.Visible, Is.False);
        Assert.That(split.Visible, Is.True);
        Assert.That(split.Panel1Collapsed, Is.True, "a folded navigation pane stays folded");
        Assert.That(split.SplitterDistance, Is.EqualTo(distance));
        Assert.That(model.CurrentLocation, Is.EqualTo(location), "the browser is where it was left");
      });
    }));
  }

  // ── what is offered ─────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFatImage_WhenOnTheTab_ThenDefragmentIsOfferedAndOptimizeIsNotAndEachSaysWhy() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));

      Assert.Multiple(() => {
        Assert.That(Toggle(shell, "Defragment").Enabled, Is.True);
        Assert.That(Toggle(shell, "Defragment").Checked, Is.True, "the first supported operation is preselected");
        Assert.That(Toggle(shell, "Shrink").Enabled, Is.True);
        Assert.That(Toggle(shell, "Clear").Enabled, Is.True);
        Assert.That(Toggle(shell, "Optimize").Enabled, Is.False);
        Assert.That(Toggle(shell, "Optimize").ToolTipText, Does.Contain("no optimizer"));
        Assert.That(Toggle(shell, "Sort Entries").Enabled, Is.False);
        Assert.That(Toggle(shell, "Sort Entries").ToolTipText, Does.Contain("sorting directory entries"));
        Assert.That(Item<RibbonButton>(shell, "Start").Enabled, Is.True);
      });
    }));
  }

  [Test]
  public void GivenAZip_WhenOnTheTab_ThenDefragmentIsNotOfferedButOptimizeAndPurgeAre() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.Zip(root));

      Assert.Multiple(() => {
        Assert.That(Toggle(shell, "Defragment").Enabled, Is.False);
        Assert.That(Toggle(shell, "Optimize").Enabled, Is.True);
        Assert.That(Toggle(shell, "Optimize").Checked, Is.True);
        Assert.That(Toggle(shell, "Purge").Enabled, Is.True);
        Assert.That(Toggle(shell, "Consolidate").Enabled, Is.False, "no defrag mode applies to an archive");
      });
    }));
  }

  [Test]
  public void GivenDefragment_WhenAnotherOperationIsPicked_ThenTheDefragModesAndLayoutOptionsGreyOut() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      var interleave = Field<RibbonHostItem>(shell, "_interleaveItem");
      Assert.That((Toggle(shell, "Consolidate").Enabled, interleave.Enabled), Is.EqualTo((true, true)));

      Toggle(shell, "Shrink").PerformClick();

      Assert.Multiple(() => {
        Assert.That(Toggle(shell, "Shrink").Checked, Is.True);
        Assert.That(Toggle(shell, "Defragment").Checked, Is.False, "operations are a radio group");
        foreach (var mode in new[] { "Consolidate", "Defrag", "Re-order", "Carve Hole" }) {
          Assert.That(Toggle(shell, mode).Enabled, Is.False, mode);
          Assert.That(Toggle(shell, mode).ToolTipText, Does.Contain("Applies to Defragment"), mode);
        }
        Assert.That(interleave.Enabled, Is.False);
      });

      Toggle(shell, "Shrink").PerformClick();
      Assert.That(Toggle(shell, "Shrink").Checked, Is.True, "clicking the picked operation keeps it picked");
    }));
  }

  [Test]
  public void GivenCarveHole_WhenPickedAndUnpicked_ThenItsSizeAndOffsetAppearOnlyWhilePicked() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      var size = Field<RibbonHostItem>(shell, "_holeSizeItem");
      var at = Field<RibbonHostItem>(shell, "_holeAtItem");
      Assert.That((size.Visible, at.Visible), Is.EqualTo((false, false)));

      Toggle(shell, "Carve Hole").PerformClick();
      Assert.That((size.Visible, at.Visible), Is.EqualTo((true, true)));

      Toggle(shell, "Re-order").PerformClick();
      Assert.That((size.Visible, at.Visible), Is.EqualTo((false, false)));
    }));
  }

  [TestCase("0", false)]
  [TestCase("1", true)]
  [TestCase("256", true)]
  [TestCase("257", false)]
  [TestCase("two", false)]
  public void GivenAnInterleave_WhenTyped_ThenStartFollowsWhetherItIsInRange(string stride, bool startable) {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));

      Field<TextBox>(shell, "_interleaveBox").Text = stride;

      var start = Item<RibbonButton>(shell, "Start");
      Assert.That(start.Enabled, Is.EqualTo(startable));
      if (!startable) Assert.That(start.ToolTipText, Does.Contain("Interleave"), "Start says what blocks it");
    }));
  }

  [Test]
  public void GivenTheViewToggles_WhenEachIsClicked_ThenTheBlockMapSwitchesAndTheOthersRelease() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      var map = DefragView(shell).Map;

      foreach (var (text, view) in new[] { ("Circle", BlockMapView.CircularPlatter), ("3D Stack", BlockMapView.CylinderStack), ("Blocks", BlockMapView.LinearBlocks) }) {
        Toggle(shell, text).PerformClick();
        Assert.That(map.ViewMode, Is.EqualTo(view), text);
        Assert.That(new[] { "Blocks", "Circle", "3D Stack" }.Count(t => Toggle(shell, t).Checked), Is.EqualTo(1), text);
      }

      Toggle(shell, "Files").PerformClick();
      Assert.That(DefragView(shell).FilesPanelVisible, Is.False);
    }));
  }

  /// <summary>
  /// The view is hidden while the ribbon is built, so asking a child whether it is visible answers
  /// no. Syncing the toggles from that answer once switched the colour key off for good.
  /// </summary>
  [Test]
  public void GivenTheDefragmentTab_WhenEntered_ThenTheColourKeyIsShownAndItsToggleHidesIt() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      var legend = Field<Control>(DefragView(shell), "_legend");

      Assert.That((Toggle(shell, "Legend").Checked, legend.Visible), Is.EqualTo((true, true)));

      Toggle(shell, "Legend").PerformClick();
      Assert.That((Toggle(shell, "Legend").Checked, legend.Visible), Is.EqualTo((false, false)));
    }));
  }

  [Test]
  public void GivenAContextMenuVerb_WhenRun_ThenTheDefragmentTabOpensWithThatOperationPicked() {
    WithImages(root => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      shell.OpenArchive(MaintenanceFixtures.FragmentedFat(root));

      model.ShrinkEntryCommand.Execute(null);
      Settle(shell);

      Assert.That(Field<Ribbon>(shell, "_ribbon").SelectedTab, Is.SameAs(DefragTab(shell)));
      Assert.That(Toggle(shell, "Shrink").Checked, Is.True);
    }));
  }

  // ── running ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFragmentedFat_WhenStartIsClicked_ThenItIsDefragmentedInPlaceWithSizeAndContentsUnchanged() {
    WithImages(root => WithShell(shell => {
      var image = MaintenanceFixtures.FragmentedFat(root);
      var size = new FileInfo(image).Length;
      OpenOnDefragmentTab(shell, image);
      Assume.That(DefragView(shell).StatusText, Does.Contain("fragmented file"));

      Item<RibbonButton>(shell, "Start").PerformClick();
      Settle(shell);

      var model = Field<MainViewModel>(shell, "_model");
      Assert.Multiple(() => {
        Assert.That(model.StatusText, Does.StartWith("Defragment finished"), model.StatusText);
        Assert.That(new FileInfo(image).Length, Is.EqualTo(size), "total size unchanged");
        var after = MaintenanceFixtures.ReadFatFiles(image);
        foreach (var (name, data) in MaintenanceFixtures.FatFiles)
          Assert.That(after[name], Is.EqualTo(data), $"{name} byte-identical");
        Assert.That(DefragView(shell).StatusText, Does.Contain("no fragmentation"), "the map is read again afterwards");
        Assert.That(DiskTools(shell).Visible, Is.True, "the tab stays with its image after the reload");
      });
    }));
  }

  [Test]
  public void GivenARequestTheFormatRefuses_WhenStarted_ThenItIsReportedAndTheImageIsByteIdentical() {
    WithImages(root => WithShell(shell => {
      var image = MaintenanceFixtures.FragmentedFat(root);
      var before = File.ReadAllBytes(image);
      OpenOnDefragmentTab(shell, image);

      Toggle(shell, "Carve Hole").PerformClick();
      Field<TextBox>(shell, "_holeSizeBox").Text = "1g";
      Item<RibbonButton>(shell, "Start").PerformClick();
      Settle(shell);

      Assert.Multiple(() => {
        Assert.That(Field<MainViewModel>(shell, "_model").StatusText, Does.Contain("refused").And.Contain("unchanged"));
        Assert.That(File.ReadAllBytes(image), Is.EqualTo(before));
      });
    }));
  }
}
