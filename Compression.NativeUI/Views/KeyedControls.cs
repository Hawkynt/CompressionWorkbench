using Hawkynt.NativeForms;

namespace Compression.NativeUI.Views;

/// <summary>A list that lets the shell see a key before the list acts on it.</summary>
/// <remarks>
/// NativeForms controls take keys through an overridable <c>OnKeyDown</c> and publish no event for
/// them. Shortcuts that only make sense where files are shown — the clipboard keys — belong here
/// rather than on the menu, where they would also fire in the middle of editing a name.
/// </remarks>
internal sealed class KeyedListView : ListView {
  public event EventHandler<KeyEventArgs>? KeyDown;

  protected override void OnKeyDown(KeyEventArgs e) {
    this.KeyDown?.Invoke(this, e);
    if (!e.Handled) base.OnKeyDown(e);
  }
}

/// <summary>A tree that lets the shell see a key before the tree acts on it; see <see cref="KeyedListView"/>.</summary>
internal sealed class KeyedTreeView : TreeView {
  public event EventHandler<KeyEventArgs>? KeyDown;

  protected override void OnKeyDown(KeyEventArgs e) {
    this.KeyDown?.Invoke(this, e);
    if (!e.Handled) base.OnKeyDown(e);
  }
}
