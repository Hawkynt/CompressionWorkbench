using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Compression.Mounting;
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
      foreach (var name in new[] { "_menu", "_toolbar", "_breadcrumbBar", "_entries", "_status", "_dropOverlay" }) {
        var control = Field<Control>(shell, name);
        Assert.That(shell.Controls.Cast<Control>().Count(c => ReferenceEquals(c, control)), Is.EqualTo(1),
          $"{name} should appear exactly once in the form's controls");
      }
    });
  }

  // ── accelerators ────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenTheMenuBar_WhenAcceleratorsAreCollected_ThenNoKeyIsClaimedTwice() {
    WithShell(shell => {
      var claims = Field<MenuStrip>(shell, "_menu").Items
        .Cast<ToolStripItem>()
        .SelectMany(MenuItems)
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
  public void GivenViewAsText_WhenTheMenuIsBuilt_ThenEnterIsShownButNotRegistered() {
    WithShell(shell => {
      var item = Field<MenuStrip>(shell, "_menu").Items
        .Cast<ToolStripItem>()
        .SelectMany(MenuItems)
        .Single(i => i.Text.Replace("&", "") == "View as Text");

      Assert.Multiple(() => {
        Assert.That(item.ShortcutKeys, Is.EqualTo(Keys.None), "Enter belongs to the entry list");
        Assert.That(item.ShortcutKeyDisplayString, Is.EqualTo("Enter"), "but it is still advertised");
      });
    });
  }

  [TestCase("Go Up", Keys.Back)]
  [TestCase("Delete", Keys.Delete)]
  [TestCase("Open...", Keys.Control | Keys.O)]
  [TestCase("Create...", Keys.Control | Keys.N)]
  [TestCase("Extract All...", Keys.Control | Keys.E)]
  [TestCase("Test Integrity", Keys.Control | Keys.T)]
  [TestCase("Properties", Keys.Alt | Keys.Enter)]
  public void GivenAnAction_WhenTheMenuIsBuilt_ThenItCarriesItsAccelerator(string label, Keys expected) {
    WithShell(shell => {
      var item = Field<MenuStrip>(shell, "_menu").Items
        .Cast<ToolStripItem>()
        .SelectMany(MenuItems)
        .Single(i => i.Text.Replace("&", "") == label);

      Assert.That(item.ShortcutKeys, Is.EqualTo(expected));
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

      Assert.That(duplicated, Is.Empty, "the menu bar owns these keys; the context menu shows them");
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
