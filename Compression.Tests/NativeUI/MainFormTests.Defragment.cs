using System;
using System.IO;
using System.Linq;
using Compression.Core.Layout;
using Compression.Lib;
using Compression.Registry;
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

  // ── the target ──────────────────────────────────────────────────────────────────────────────

  private static void Select(MainViewModel model, string name) {
    model.SelectedEntries.Clear();
    model.SelectedEntries.Add(model.Entries.First(e => e.Name == name));
    CommandManager.InvalidateRequerySuggested();
  }

  private static RibbonComboBox TargetCombo(MainForm shell) => Field<RibbonComboBox>(shell, "_targetCombo");

  /// <summary>
  /// A plain file named <c>f19.bin</c> inside a FAT volume is not a BIN/CUE image. Its name used to
  /// make it the target — "0 regions", an empty files panel, nearly everything disabled — instead of
  /// the volume the user had open.
  /// </summary>
  [Test]
  public void GivenAPlainBinFileSelectedInsideAFatVolume_WhenTheTabOpens_ThenTheVolumeIsTheTargetWithEveryFatOperation() {
    WithImages(root => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      shell.OpenArchive(MaintenanceFixtures.FatWithPlainBinFiles(root));
      Select(model, "f19.bin");

      Assert.That(model.SelectedMaintenanceTarget, Is.Null, "a .bin name with no container content proves nothing");

      RibbonItemNamed(shell, "Maintenance").PerformClick();
      Settle(shell);

      Assert.Multiple(() => {
        Assert.That(TargetCombo(shell).Items, Is.EqualTo(new[] { "Open volume (disk.img)" }));
        Assert.That(TargetCombo(shell).SelectedIndex, Is.EqualTo(0));
        Assert.That(DefragView(shell).TargetText, Does.StartWith("disk.img — ").And.Contain("FAT"));
        Assert.That(DefragView(shell).StatusText, Does.StartWith("Real on-disk layout"));
        foreach (var operation in new[] { "Defragment", "Shrink", "Clear", "Scramble" })
          Assert.That(Toggle(shell, operation).Enabled, Is.True, operation);
        Assert.That(Toggle(shell, "Sort Entries").Enabled, Is.True);
      });
    }));
  }

  [TestCase("fake.img")]
  [TestCase("notes.txt")]
  public void GivenAnEntryWhoseOnlyClaimIsItsName_WhenSelected_ThenItIsNotOfferedAsATarget(string name) {
    WithImages(root => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      shell.OpenArchive(MaintenanceFixtures.ZipWithNestedImage(root));
      Select(model, name);

      Assert.That(model.SelectedMaintenanceTarget, Is.Null);
      Assert.That(model.MaintenanceTargets().Select(t => t.Kind), Is.EqualTo(new[] { MaintenanceTargetKind.OpenVolume }));
    }));
  }

  [Test]
  public void GivenARealFatImageInsideAZip_WhenSelected_ThenTheTabOffersItAndPickingItMakesItTheTarget() {
    WithImages(root => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      shell.OpenArchive(MaintenanceFixtures.ZipWithNestedImage(root));
      Select(model, "inner.img");

      Assert.That(model.SelectedMaintenanceTarget?.FormatId, Is.EqualTo("Fat"), "its content is a FAT boot sector");

      RibbonItemNamed(shell, "Maintenance").PerformClick();
      Settle(shell);
      Assert.Multiple(() => {
        Assert.That(TargetCombo(shell).Items, Is.EqualTo(new[] { "Open volume (nested.zip)", "Selected: inner.img (Fat)" }));
        Assert.That(TargetCombo(shell).SelectedIndex, Is.EqualTo(0), "the open volume is the default");
        Assert.That(Toggle(shell, "Defragment").Enabled, Is.False, "the ZIP has no extents to move");
      });

      TargetCombo(shell).SelectedIndex = 1;
      Settle(shell);
      Assert.Multiple(() => {
        Assert.That(DefragView(shell).TargetText, Does.StartWith("inner.img — "));
        Assert.That(Toggle(shell, "Defragment").Enabled, Is.True, "the nested FAT image moves extents");
        Assert.That(Toggle(shell, "Sort Entries").Enabled, Is.True);
      });
    }));
  }

  [Test]
  public void GivenAChosenTarget_WhenTheSelectionChangesOnTheTab_ThenTheTargetStaysAsChosen() {
    WithImages(root => WithShell(shell => {
      var model = Field<MainViewModel>(shell, "_model");
      shell.OpenArchive(MaintenanceFixtures.ZipWithNestedImage(root));
      Select(model, "inner.img");
      model.DefragmentEntryCommand.Execute(null); // the context menu: aimed at the selection
      Settle(shell);
      Assume.That(DefragView(shell).TargetText, Does.StartWith("inner.img — "));
      Toggle(shell, "Sort Entries").PerformClick();

      Select(model, "notes.txt");
      Settle(shell);

      Assert.Multiple(() => {
        Assert.That(DefragView(shell).TargetText, Does.StartWith("inner.img — "), "a selection change never retargets silently");
        Assert.That(TargetCombo(shell).Items, Does.Contain("Selected: inner.img (Fat)"), "the chosen target stays listed");
        Assert.That(Toggle(shell, "Sort Entries").Checked, Is.True, "the configured operation is kept");
      });
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
  public void GivenAFatImage_WhenOnTheTab_ThenEachItemFollowsTheRegistryProfileAndSaysWhy() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));

      Assert.Multiple(() => {
        Assert.That(Toggle(shell, "Defragment").Enabled, Is.True);
        Assert.That(Toggle(shell, "Defragment").Checked, Is.True, "the first supported operation is preselected");
        Assert.That(Toggle(shell, "Shrink").Enabled, Is.True);
        Assert.That(Toggle(shell, "Clear").Enabled, Is.True);
        var profile = MaintenanceCapabilities.Describe("Fat")!;
        var optimizes = profile.Supports(MaintenanceCapability.Compress) || profile.Supports(MaintenanceCapability.Repack) || profile.Supports(MaintenanceCapability.Canonicalize);
        Assert.That(Toggle(shell, "Optimize").Enabled, Is.EqualTo(optimizes));
        Assert.That(Toggle(shell, "Optimize").ToolTipText, Does.StartWith("Optimize: "), "says why either way");
        Assert.That(Toggle(shell, "Sort Entries").Enabled, Is.True, "FAT sorts directory entries (#440)");
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
        Assert.That(Item<RibbonComboBox>(shell, "Method").Visible, Is.True, "Optimize offers its methods");
        Assert.That(Item<RibbonComboBox>(shell, "Method").Items, Is.Not.Empty);
        Assert.That(Toggle(shell, "Purge").Enabled, Is.True);
        Assert.That(Toggle(shell, "Consolidate").Enabled, Is.False, "no defrag mode applies to an archive");
      });
    }));
  }

  [Test]
  public void GivenDefragment_WhenAnotherOperationIsPicked_ThenTheDefragModesAndLayoutOptionsGreyOut() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      var interleave = Field<RibbonSpinner>(shell, "_interleaveSpinner");
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
      var fields = new RibbonFieldItem[] {
        Field<RibbonSpinner>(shell, "_holeSizeSpinner"), Field<RibbonComboBox>(shell, "_holePlacementCombo"), Field<RibbonSpinner>(shell, "_holeOffsetSpinner"),
      };
      Assert.That(fields.Select(f => f.Visible), Is.All.False);

      Toggle(shell, "Carve Hole").PerformClick();
      Assert.That(fields.Select(f => f.Visible), Is.All.True);

      Toggle(shell, "Re-order").PerformClick();
      Assert.That(fields.Select(f => f.Visible), Is.All.False);
    }));
  }

  [Test]
  public void GivenCarveHole_WhenTheHoleIsPlacedAtTheEndOrAtAnOffset_ThenTheOffsetFieldFollowsAndTheEngineGetsTheBytes() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      Toggle(shell, "Carve Hole").PerformClick();
      var placement = Field<RibbonComboBox>(shell, "_holePlacementCombo");
      var offset = Field<RibbonSpinner>(shell, "_holeOffsetSpinner");
      var presenter = Field<global::Compression.NativeUI.Maintenance.MaintenancePresenter>(shell, "_presenter");
      Field<RibbonSpinner>(shell, "_holeSizeSpinner").Value = 128;

      Assert.That((offset.Enabled, presenter.BuildDefragOptions().HoleAt), Is.EqualTo((false, -1L)), "End: no offset, auto placement");

      placement.SelectedIndex = 1;
      offset.Value = 256;
      var options = presenter.BuildDefragOptions();
      Assert.That((offset.Enabled, options.HoleSize, options.HoleAt), Is.EqualTo((true, 128L * 1024, 256L * 1024)));
    }));
  }

  /// <summary>
  /// The interleave is a spinner over 1–256, so a value outside the range cannot be entered: it is
  /// clamped, and Start stays available. The text-level boundaries are covered by the presenter tests.
  /// </summary>
  [TestCase(0, 1)]
  [TestCase(1, 1)]
  [TestCase(256, 256)]
  [TestCase(257, 256)]
  public void GivenTheInterleaveSpinner_WhenSet_ThenItClampsToOneTo256AndTheEngineGetsThatStride(int typed, int expected) {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      var spinner = Field<RibbonSpinner>(shell, "_interleaveSpinner");
      spinner.Value = 7; // away from the boundary, so every case is a change

      spinner.Value = typed;

      var presenter = Field<global::Compression.NativeUI.Maintenance.MaintenancePresenter>(shell, "_presenter");
      Assert.Multiple(() => {
        Assert.That(spinner.Value, Is.EqualTo((decimal)expected));
        Assert.That(presenter.BuildDefragOptions().InterleaveStride, Is.EqualTo(expected));
        Assert.That(Item<RibbonButton>(shell, "Start").Enabled, Is.True);
      });
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

  /// <summary>
  /// GTK draws a native combo box at its natural height whatever bounds it is handed, which cut the
  /// bottom off its text in a ribbon row. Every input on the tab is therefore a ribbon field — drawn
  /// by the ribbon and always one stacked row — and none is a hosted native control.
  /// </summary>
  [Test]
  public void GivenTheDefragmentTab_WhenItsInputsAreCollected_ThenEachIsAOneRowRibbonFieldAndNoneIsAHostedControl() {
    WithImages(root => WithShell(shell => {
      OpenOnDefragmentTab(shell, MaintenanceFixtures.FragmentedFat(root));
      var items = DefragTab(shell).Groups.SelectMany(g => g.Items.Cast<ToolStripItem>()).ToList();

      Assert.Multiple(() => {
        Assert.That(items.OfType<RibbonHostItem>(), Is.Empty, "a hosted native control ignores its row height on GTK");
        var fields = items.OfType<RibbonFieldItem>().ToList();
        Assert.That(fields.Select(f => f.Text), Is.SupersetOf(new[] { "Interleave", "Metadata", "Profile", "Hole KiB", "Hole at", "Offset KiB" }));
        Assert.That(fields.Select(f => f.ItemSize), Is.All.EqualTo(RibbonItemSize.Small), "one stacked row each");
      });
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
  public void GivenAnUnsortedFat_WhenSortEntriesIsStarted_ThenTheRootIsSortedInPlaceAndTheLayoutOptionsDoNotApply() {
    WithImages(root => WithShell(shell => {
      var image = MaintenanceFixtures.UnsortedFat(root);
      var size = new FileInfo(image).Length;
      OpenOnDefragmentTab(shell, image);

      Toggle(shell, "Sort Entries").PerformClick();
      Assert.Multiple(() => {
        Assert.That(Toggle(shell, "Sort Entries").Checked, Is.True);
        Assert.That(Field<RibbonSpinner>(shell, "_interleaveSpinner").Enabled, Is.False, "a sort moves no data, so interleave does not apply");
        Assert.That(Field<RibbonSpinner>(shell, "_interleaveSpinner").ToolTipText, Does.Contain("extent moves"));
      });

      Item<RibbonButton>(shell, "Start").PerformClick();
      Settle(shell);

      var order = MaintenanceFixtures.RootOrder(image);
      Assert.Multiple(() => {
        Assert.That(Field<MainViewModel>(shell, "_model").StatusText, Does.StartWith("Defragment finished"));
        Assert.That(order, Is.EqualTo(MaintenanceFixtures.NameOrder(order)));
        Assert.That(new FileInfo(image).Length, Is.EqualTo(size));
        var after = MaintenanceFixtures.ReadFatFiles(image);
        foreach (var (name, data) in MaintenanceFixtures.UnsortedFatFiles)
          Assert.That(after[name], Is.EqualTo(data), name);
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
      Field<RibbonSpinner>(shell, "_holeSizeSpinner").Value = 1024 * 1024; // 1 GiB on a 1.44 MB floppy
      Item<RibbonButton>(shell, "Start").PerformClick();
      Settle(shell);

      Assert.Multiple(() => {
        Assert.That(Field<MainViewModel>(shell, "_model").StatusText, Does.Contain("refused").And.Contain("unchanged"));
        Assert.That(File.ReadAllBytes(image), Is.EqualTo(before));
      });
    }));
  }
}
