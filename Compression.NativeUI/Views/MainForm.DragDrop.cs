using System.Drawing;
using Compression.NativeUI.Editing;
using Compression.NativeUI.Navigation;
using Place = Compression.NativeUI.Navigation.Location;
using Compression.NativeUI.ViewModels;
using Hawkynt.NativeForms;

namespace Compression.NativeUI.Views;

/// <summary>What a drag started in the shell carries: the picked-up entries and where they are.</summary>
internal sealed record ShellDragData(IReadOnlyList<TransferItem> Items);

/// <summary>
/// Dragging between the shell's own panes: rows of the list onto a folder in the tree, onto a
/// folder row, or into another folder's list. Files dropped from the desktop go the same way.
/// </summary>
internal sealed partial class MainForm {
  private void WireDragAndDrop() {
    this._entries.ItemDrag += (_, _) => {
      var items = this._model.SelectionAsTransferItems();
      if (items.Count > 0)
        this._entries.DoDragDrop(new ShellDragData(items), DragDropEffects.Copy | DragDropEffects.Move);
    };

    this._entries.AllowDrop = true;
    this._entries.DragEnter += (_, e) => this.Answer(e, this.ListDropTarget(e));
    this._entries.DragOver += (_, e) => this.Answer(e, this.ListDropTarget(e));
    this._entries.DragDrop += (_, e) => this.Drop(e, this.ListDropTarget(e));

    this._tree.AllowDrop = true;
    this._tree.DragEnter += (_, e) => this.Answer(e, this.TreeDropTarget(e));
    this._tree.DragOver += (_, e) => this.Answer(e, this.TreeDropTarget(e));
    this._tree.DragDrop += (_, e) => this.Drop(e, this.TreeDropTarget(e));
  }

  /// <summary>
  /// Where a drop on the list lands: in the folder row under the pointer, or else in the folder
  /// the list is showing. The <c>..</c> row takes nothing — it is navigation, not a folder.
  /// </summary>
  private Location? ListDropTarget(DragEventArgs e) {
    if (this._model.CurrentLocation is not { } here) return null;

    var point = ToClient(this._entries, e);
    if (this._entries.GetItemAt(point.X, point.Y)?.Tag is not ArchiveEntryViewModel row) return here;
    if (row.IsParentEntry) return null;
    if (!row.IsDirectory) return here;

    return here.IsInArchive
      ? Place.InArchive(here.HostPath, here.ArchiveFolder + row.Name + "/")
      : Place.Folder(Path.Combine(here.HostPath, row.Name));
  }

  private Location? TreeDropTarget(DragEventArgs e) {
    var point = ToClient(this._tree, e);
    return this._tree.GetNodeAt(point.X, point.Y)?.Tag as Location;
  }

  /// <summary>Drag coordinates arrive in screen space; hit tests want the control's own.</summary>
  private static Point ToClient(Control control, DragEventArgs e) {
    var origin = control.PointToScreen(Point.Empty);
    return new(e.X - origin.X, e.Y - origin.Y);
  }

  /// <summary>
  /// The entries a drag carries: the shell's own, or files dropped from outside — whichever of
  /// those still exist.
  /// </summary>
  private static IReadOnlyList<TransferItem>? ItemsOf(object data) => data switch {
    ShellDragData shell => shell.Items,
    string[] paths => [
      .. paths
        .Where(p => File.Exists(p) || Directory.Exists(p))
        .Select(Path.TrimEndingDirectorySeparator)
        .Where(p => Path.GetDirectoryName(p) is not null)
        .Select(p => new TransferItem(Place.Folder(Path.GetDirectoryName(p)!), Path.GetFileName(p), Directory.Exists(p))),
    ],
    _ => null,
  };

  /// <summary>
  /// The effect a drop would have here. A drag within one volume or one archive moves, anything
  /// else copies; files from outside always copy — the other application still expects them. A
  /// drop that would change nothing, or that the transfer refuses, is not offered.
  /// </summary>
  private DragDropEffects EffectFor(object data, Location? target) {
    if (target is null || ItemsOf(data) is not { Count: > 0 } items) return DragDropEffects.None;
    if (items.All(i => i.From == target)) return DragDropEffects.None;

    var move = data is ShellDragData && Transfer.MovesByDefault(items, target);
    return this._model.WhyNotTransfer(items, target, move) is null
      ? move ? DragDropEffects.Move : DragDropEffects.Copy
      : DragDropEffects.None;
  }

  private void Answer(DragEventArgs e, Location? target) => e.Effect = this.EffectFor(e.Data, target) & e.AllowedEffect;

  private void Drop(DragEventArgs e, Location? target) {
    if (target is null || ItemsOf(e.Data) is not { Count: > 0 } items) return;

    var effect = this.EffectFor(e.Data, target) & e.AllowedEffect;
    if (effect == DragDropEffects.None) return;

    _ = this._model.TransferAsync(items, target, move: effect == DragDropEffects.Move);
  }
}
