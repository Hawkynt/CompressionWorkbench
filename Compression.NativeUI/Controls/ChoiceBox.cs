using Hawkynt.NativeForms;

namespace Compression.NativeUI.Controls;

/// <summary>
/// A drop-down list drawn by the toolkit rather than the platform: the face shows the picked item
/// and a click opens the choices as a menu.
/// </summary>
/// <remarks>
/// It stands in for <see cref="ComboBox"/> where the space is fixed and small — a ribbon row. GTK
/// draws a native combo box at its natural height whatever bounds it is given, so in a ribbon row
/// the bottom of its text is cut off; an owner-drawn face keeps exactly the bounds it is laid out to.
/// </remarks>
internal sealed class ChoiceBox : DropDownButton {
  private readonly List<string> _items = [];
  private int _selectedIndex = -1;

  /// <summary>Raised after <see cref="SelectedIndex"/> changes, whether by a click or by code.</summary>
  public event EventHandler? SelectedIndexChanged;

  public IReadOnlyList<string> Items => this._items;

  /// <summary>The picked item's index, or -1 for none. Out-of-range values are clamped.</summary>
  public int SelectedIndex {
    get => this._selectedIndex;
    set {
      var index = this._items.Count == 0 ? -1 : Math.Clamp(value, 0, this._items.Count - 1);
      if (index == this._selectedIndex) return;

      this._selectedIndex = index;
      this.Text = index < 0 ? "" : this._items[index];
      for (var i = 0; i < this.DropDownItems.Count; ++i)
        if (this.DropDownItems[i] is ToolStripMenuItem item) item.Checked = i == index;
      this.SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }
  }

  /// <summary>Replaces the choices and picks <paramref name="selectedIndex"/>.</summary>
  public void SetItems(IEnumerable<string> items, int selectedIndex = 0) {
    this._items.Clear();
    this._items.AddRange(items);
    this.DropDownItems.Clear();
    for (var i = 0; i < this._items.Count; ++i) {
      var index = i;
      var item = new ToolStripMenuItem(this._items[i]);
      item.Click += (_, _) => this.SelectedIndex = index;
      this.DropDownItems.Add(item);
    }

    // Forced through the setter even when the index is unchanged, so the face shows the new text.
    this._selectedIndex = -2;
    this.SelectedIndex = selectedIndex;
  }
}
