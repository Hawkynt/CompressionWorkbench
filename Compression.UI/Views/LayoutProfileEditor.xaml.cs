using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Compression.Lib.Layout;
using Compression.Registry;
using Compression.Registry.Layout;
// Explicit WPF aliases — UseWindowsForms=true means bare type names collide
// with WinForms types.
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using ListView = System.Windows.Controls.ListView;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Canvas = System.Windows.Controls.Canvas;

namespace Compression.UI.Views;

/// <summary>
/// Modal editor for <see cref="LayoutTemplate"/> profiles. Combines a
/// left-rail profile list (built-ins + user profiles) with a right-pane
/// editor that mutates the selected template. Validation surfaces inline
/// next to the offending field (red border + error label) so the user
/// catches mistakes before the planner does.
///
/// <para>State lives in <see cref="LayoutProfileEditorState"/> so the
/// dirty-tracking + can-save logic is unit-testable without a Window.
/// This code-behind translates between that state and the WPF controls.</para>
/// </summary>
public partial class LayoutProfileEditor : Window {

  private readonly LayoutProfileEditorState _state = new();
  private readonly ObservableCollection<ProfileListItem> _profiles = [];
  private readonly ObservableCollection<ZoneRow> _zoneRows = [];
  private readonly ObservableCollection<SortRow> _sortRows = [];
  private LayoutProfileEditorState.EditableZone? _selectedZone;
  private bool _suppressEvents;

  public LayoutProfileEditor() {
    InitializeComponent();
    ProfilesList.ItemsSource = this._profiles;
    ZonesList.ItemsSource = this._zoneRows;
    SortList.ItemsSource = this._sortRows;
    RefreshProfilesList(initialSelectFirst: true);
  }

  /// <summary>
  /// Reloads the left-rail list from <see cref="LayoutProfileStore"/>.
  /// Called after saves and deletes so the list reflects on-disk state.
  /// </summary>
  private void RefreshProfilesList(bool initialSelectFirst) {
    var previousPath = (ProfilesList.SelectedItem as ProfileListItem)?.Entry.FilePath;
    this._profiles.Clear();
    foreach (var entry in LayoutProfileStore.List())
      this._profiles.Add(new ProfileListItem(entry));
    if (this._profiles.Count == 0) {
      ClearEditor();
      return;
    }
    // Try to restore the previous selection by path; fall back to the first.
    if (previousPath != null) {
      for (var i = 0; i < this._profiles.Count; i++) {
        if (string.Equals(this._profiles[i].Entry.FilePath, previousPath, StringComparison.OrdinalIgnoreCase)) {
          ProfilesList.SelectedIndex = i;
          return;
        }
      }
    }
    if (initialSelectFirst) ProfilesList.SelectedIndex = 0;
  }

  private void OnProfileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    if (ProfilesList.SelectedItem is not ProfileListItem item) {
      ClearEditor();
      return;
    }

    if (this._state.IsDirty && !PromptDiscardChanges()) {
      // Restore previous selection without re-firing this handler.
      this._suppressEvents = true;
      try {
        // Find the item that matches the current state's source entry.
        if (this._state.SourceEntry != null) {
          for (var i = 0; i < this._profiles.Count; i++) {
            if (string.Equals(this._profiles[i].Entry.FilePath, this._state.SourceEntry.FilePath, StringComparison.OrdinalIgnoreCase)) {
              ProfilesList.SelectedIndex = i;
              break;
            }
          }
        } else {
          ProfilesList.SelectedIndex = -1;
        }
      } finally { this._suppressEvents = false; }
      return;
    }

    try {
      var template = LayoutProfileStore.Load(item.Entry);
      LoadStateIntoUi(template, item.Entry);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Failed to load profile '{item.Entry.Name}':\n{ex.Message}",
        "Profile load error", MessageBoxButton.OK, MessageBoxImage.Error);
      ClearEditor();
    }
  }

  private void ClearEditor() {
    this._suppressEvents = true;
    try {
      NameBox.Text = "";
      MetadataZoneCombo.SelectedIndex = 0;
      LeftoverCombo.SelectedIndex = 0;
      this._zoneRows.Clear();
      this._sortRows.Clear();
      this._selectedZone = null;
      ZoneDetailGrid.IsEnabled = false;
      ZoneNameBox.Text = "";
      ZoneRangeBox.Text = "";
      ZoneFilterBox.Text = "";
      ClearZoneErrors();
      UpdatePreviewBar(null);
    } finally { this._suppressEvents = false; }
    UpdateButtonStates();
    UpdateStatus(null);
  }

  /// <summary>
  /// Hydrates the WPF controls from <paramref name="template"/> and parks
  /// the editor in a clean state ready for the user to edit. Suppresses
  /// change events while loading so the dirty flag doesn't latch on the
  /// initial fill.
  /// </summary>
  private void LoadStateIntoUi(LayoutTemplate template, LayoutProfileEntry? entry) {
    this._state.LoadFrom(template, entry);
    this._suppressEvents = true;
    try {
      NameBox.Text = this._state.Name;
      SelectComboByTag(MetadataZoneCombo, this._state.MetadataZone.ToString());
      SelectComboByTag(LeftoverCombo, this._state.LeftoverStrategy);
      this._zoneRows.Clear();
      foreach (var z in this._state.Zones)
        this._zoneRows.Add(new ZoneRow(z));
      if (this._zoneRows.Count > 0) ZonesList.SelectedIndex = 0;
      else ShowZone(null);
    } finally { this._suppressEvents = false; }
    UpdateButtonStates();
    UpdateStatus(null);
  }

  // ── Header field handlers ───────────────────────────────────────────────

  private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
    if (this._suppressEvents) return;
    this._state.Name = NameBox.Text;
    UpdateButtonStates();
  }

  private void OnHeaderChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    if (sender == MetadataZoneCombo) {
      var tag = (MetadataZoneCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Unchanged";
      this._state.MetadataZone = Enum.TryParse<MetadataZone>(tag, out var mz) ? mz : MetadataZone.Unchanged;
    } else if (sender == LeftoverCombo) {
      var tag = (LeftoverCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fill_gaps";
      this._state.LeftoverStrategy = tag;
    }
    UpdateButtonStates();
  }

  // ── Zone list handlers ──────────────────────────────────────────────────

  private void OnZoneSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    var row = ZonesList.SelectedItem as ZoneRow;
    ShowZone(row?.Source);
    UpdateButtonStates();
  }

  private void ShowZone(LayoutProfileEditorState.EditableZone? zone) {
    this._selectedZone = zone;
    this._suppressEvents = true;
    try {
      this._sortRows.Clear();
      if (zone == null) {
        ZoneDetailGrid.IsEnabled = false;
        ZoneNameBox.Text = "";
        ZoneRangeBox.Text = "";
        ZoneFilterBox.Text = "";
        ClearZoneErrors();
        UpdatePreviewBar(null);
        return;
      }
      ZoneDetailGrid.IsEnabled = true;
      ZoneNameBox.Text = zone.Name;
      ZoneRangeBox.Text = zone.Range;
      ZoneFilterBox.Text = zone.Filter;
      foreach (var s in zone.SortBy)
        this._sortRows.Add(SortRow.From(s));
      ClearZoneErrors();
      // Validate without marking dirty on initial display.
      ApplyRangeValidation(zone.Range, markDirty: false);
      ApplyFilterValidation(zone.Filter, markDirty: false);
    } finally { this._suppressEvents = false; }
  }

  private void OnAddZone(object sender, RoutedEventArgs e) {
    var zone = new LayoutProfileEditorState.EditableZone {
      Name = $"zone{this._state.Zones.Count + 1}",
      Range = "0%-100%",
    };
    this._state.Zones.Add(zone);
    this._zoneRows.Add(new ZoneRow(zone));
    this._state.MarkDirty();
    ZonesList.SelectedItem = this._zoneRows[^1];
    UpdateButtonStates();
  }

  private void OnRemoveZone(object sender, RoutedEventArgs e) {
    var idx = ZonesList.SelectedIndex;
    if (idx < 0 || idx >= this._zoneRows.Count) return;
    this._state.Zones.RemoveAt(idx);
    this._zoneRows.RemoveAt(idx);
    this._state.MarkDirty();
    if (this._zoneRows.Count > 0)
      ZonesList.SelectedIndex = Math.Min(idx, this._zoneRows.Count - 1);
    else ShowZone(null);
    UpdateButtonStates();
  }

  private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelectedZone(-1);
  private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelectedZone(+1);

  private void MoveSelectedZone(int delta) {
    var idx = ZonesList.SelectedIndex;
    var target = idx + delta;
    if (idx < 0 || target < 0 || target >= this._zoneRows.Count) return;
    (this._state.Zones[idx], this._state.Zones[target]) = (this._state.Zones[target], this._state.Zones[idx]);
    (this._zoneRows[idx], this._zoneRows[target]) = (this._zoneRows[target], this._zoneRows[idx]);
    this._state.MarkDirty();
    ZonesList.SelectedIndex = target;
    UpdateButtonStates();
  }

  // ── Zone detail handlers ────────────────────────────────────────────────

  private void OnZoneNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
    if (this._suppressEvents || this._selectedZone == null) return;
    this._selectedZone.Name = ZoneNameBox.Text;
    RefreshSelectedZoneRow();
    this._state.MarkDirty();
    UpdateButtonStates();
  }

  private void OnZoneRangeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
    if (this._suppressEvents || this._selectedZone == null) return;
    this._selectedZone.Range = ZoneRangeBox.Text;
    ApplyRangeValidation(ZoneRangeBox.Text, markDirty: true);
    RefreshSelectedZoneRow();
    UpdateButtonStates();
  }

  private void OnZoneFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
    if (this._suppressEvents || this._selectedZone == null) return;
    this._selectedZone.Filter = ZoneFilterBox.Text;
    ApplyFilterValidation(ZoneFilterBox.Text, markDirty: true);
    RefreshSelectedZoneRow();
    UpdateButtonStates();
  }

  private void OnValidateRange(object sender, RoutedEventArgs e) {
    var msg = LayoutProfileEditorState.ValidateRange(ZoneRangeBox.Text);
    SetRangeError(msg);
    UpdatePreviewBar(msg == null ? ZoneRangeBox.Text : null);
    if (msg == null)
      MessageBox.Show(this, "Range OK.", "Validate", MessageBoxButton.OK, MessageBoxImage.Information);
  }

  private void OnValidateFilter(object sender, RoutedEventArgs e) {
    var msg = LayoutProfileEditorState.ValidateFilter(ZoneFilterBox.Text);
    SetFilterError(msg);
    if (msg == null)
      MessageBox.Show(this, "Filter OK.", "Validate", MessageBoxButton.OK, MessageBoxImage.Information);
  }

  private void ApplyRangeValidation(string range, bool markDirty) {
    var msg = LayoutProfileEditorState.ValidateRange(range);
    SetRangeError(msg);
    UpdatePreviewBar(msg == null ? range : null);
    if (markDirty) this._state.MarkDirty();
  }

  private void ApplyFilterValidation(string filter, bool markDirty) {
    var msg = LayoutProfileEditorState.ValidateFilter(filter);
    SetFilterError(msg);
    if (markDirty) this._state.MarkDirty();
  }

  private void SetRangeError(string? msg) {
    ZoneRangeError.Text = msg ?? "";
    ZoneRangeBorder.BorderBrush = msg == null ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30));
  }

  private void SetFilterError(string? msg) {
    ZoneFilterError.Text = msg ?? "";
    ZoneFilterBorder.BorderBrush = msg == null ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30));
  }

  private void ClearZoneErrors() {
    SetRangeError(null);
    SetFilterError(null);
  }

  /// <summary>
  /// Lays a blue band across the 0..100% preview track that visualises
  /// where the selected zone falls. Falls back to a fraction of an
  /// assumed 1 GiB image for absolute ranges so the bar is still useful.
  /// </summary>
  private void UpdatePreviewBar(string? rangeText) {
    if (ZoneRangePreview == null || ZoneRangePreviewBar == null) return;
    if (rangeText == null) {
      ZoneRangePreviewBar.Width = 0;
      Canvas.SetLeft(ZoneRangePreviewBar, 0);
      return;
    }
    try {
      var spec = RangeSpec.Parse(rangeText);
      // Assume a notional 1 GiB image so we can render absolute byte ranges too.
      const long notional = 1024L * 1024L * 1024L;
      var (start, end) = spec.Resolve(notional);
      var width = ZoneRangePreview.ActualWidth > 0 ? ZoneRangePreview.ActualWidth : 600;
      var x0 = width * start / notional;
      var x1 = width * end / notional;
      Canvas.SetLeft(ZoneRangePreviewBar, x0);
      ZoneRangePreviewBar.Width = Math.Max(0, x1 - x0);
    } catch {
      ZoneRangePreviewBar.Width = 0;
    }
  }

  private void RefreshSelectedZoneRow() {
    var idx = ZonesList.SelectedIndex;
    if (idx < 0 || idx >= this._zoneRows.Count || this._selectedZone == null) return;
    this._zoneRows[idx] = new ZoneRow(this._selectedZone);
    ZonesList.SelectedIndex = idx;
  }

  // ── Sort row handlers ───────────────────────────────────────────────────

  private void OnSortSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    UpdateButtonStates();
  }

  private void OnAddSort(object sender, RoutedEventArgs e) {
    if (this._selectedZone == null) return;
    var key = new LayoutProfileEditorState.EditableSortKey();
    this._selectedZone.SortBy.Add(key);
    this._sortRows.Add(SortRow.From(key));
    this._state.MarkDirty();
    RefreshSelectedZoneRow();
    UpdateButtonStates();
  }

  private void OnRemoveSort(object sender, RoutedEventArgs e) {
    if (this._selectedZone == null) return;
    var idx = SortList.SelectedIndex;
    if (idx < 0 || idx >= this._sortRows.Count) return;
    this._selectedZone.SortBy.RemoveAt(idx);
    this._sortRows.RemoveAt(idx);
    this._state.MarkDirty();
    RefreshSelectedZoneRow();
    UpdateButtonStates();
  }

  private void OnSortRowChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
    if (this._suppressEvents || this._selectedZone == null) return;
    // Sync underlying model from observable rows.
    this._selectedZone.SortBy.Clear();
    foreach (var row in this._sortRows)
      this._selectedZone.SortBy.Add(new LayoutProfileEditorState.EditableSortKey {
        Field = row.Field,
        Direction = row.Direction,
      });
    this._state.MarkDirty();
    RefreshSelectedZoneRow();
    UpdateButtonStates();
  }

  // ── Save / new / delete handlers ────────────────────────────────────────

  private void OnNew(object sender, RoutedEventArgs e) {
    if (this._state.IsDirty && !PromptDiscardChanges()) return;
    var template = new LayoutTemplate {
      Name = "New profile",
      MetadataZone = MetadataZone.Unchanged,
      LeftoverStrategyText = "fill_gaps",
      Zones = [],
    };
    this._suppressEvents = true;
    try { ProfilesList.SelectedIndex = -1; } finally { this._suppressEvents = false; }
    LoadStateIntoUi(template, null);
  }

  private void OnSave(object sender, RoutedEventArgs e) {
    if (this._state.SourceEntry == null || this._state.SourceEntry.Origin == ProfileOrigin.Builtin) {
      OnSaveAs(sender, e);
      return;
    }
    if (!TryBuildTemplate(out var template)) return;
    try {
      var fileName = Path.GetFileName(this._state.SourceEntry.FilePath);
      var entry = LayoutProfileStore.Save(template, fileName);
      this._state.LoadFrom(template, entry);
      UpdateStatus($"Saved {Path.GetFileName(entry.FilePath)}.");
      RefreshProfilesList(initialSelectFirst: false);
      UpdateButtonStates();
    } catch (Exception ex) {
      MessageBox.Show(this, $"Save failed: {ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OnSaveAs(object sender, RoutedEventArgs e) {
    if (!TryBuildTemplate(out var template)) return;
    var suggested = LayoutProfileStore.SuggestFileName(template.Name);
    var dlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Save layout profile",
      Filter = "Layout profile JSON|*.json|All files|*.*",
      InitialDirectory = SafeDir(LayoutProfileStore.UserDirectory),
      FileName = suggested,
      DefaultExt = ".json",
      AddExtension = true,
    };
    if (dlg.ShowDialog(this) != true) return;
    try {
      // The store handles directory creation + extension normalisation.
      var entry = LayoutProfileStore.Save(template, Path.GetFileName(dlg.FileName));
      this._state.LoadFrom(template, entry);
      UpdateStatus($"Saved {Path.GetFileName(entry.FilePath)}.");
      RefreshProfilesList(initialSelectFirst: false);
      // Reselect the freshly-saved profile.
      for (var i = 0; i < this._profiles.Count; i++) {
        if (string.Equals(this._profiles[i].Entry.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase)) {
          ProfilesList.SelectedIndex = i;
          break;
        }
      }
      UpdateButtonStates();
    } catch (Exception ex) {
      MessageBox.Show(this, $"Save failed: {ex.Message}", "Save As", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private static string SafeDir(string path) {
    try { Directory.CreateDirectory(path); } catch { /* fall through */ }
    return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
  }

  private bool TryBuildTemplate(out LayoutTemplate template) {
    template = null!;
    try {
      template = this._state.Build();
      return true;
    } catch (FormatException ex) {
      MessageBox.Show(this, ex.Message, "Profile validation",
        MessageBoxButton.OK, MessageBoxImage.Warning);
      return false;
    }
  }

  private void OnDelete(object sender, RoutedEventArgs e) {
    if (ProfilesList.SelectedItem is not ProfileListItem item) return;
    if (item.Entry.Origin == ProfileOrigin.Builtin) {
      MessageBox.Show(this, "Built-in profiles are read-only.", "Delete",
        MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    var answer = MessageBox.Show(this, $"Delete profile '{item.Entry.Name}'?", "Confirm delete",
      MessageBoxButton.YesNo, MessageBoxImage.Question);
    if (answer != MessageBoxResult.Yes) return;
    try {
      LayoutProfileStore.Delete(item.Entry);
      // Clear the editor if the deleted profile was selected.
      if (this._state.SourceEntry != null
          && string.Equals(this._state.SourceEntry.FilePath, item.Entry.FilePath, StringComparison.OrdinalIgnoreCase))
        ClearEditor();
      RefreshProfilesList(initialSelectFirst: true);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Delete failed: {ex.Message}", "Delete",
        MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OnClose(object sender, RoutedEventArgs e) => Close();

  protected override void OnClosing(CancelEventArgs e) {
    base.OnClosing(e);
    if (e.Cancel) return;
    if (this._state.IsDirty && !PromptDiscardChanges())
      e.Cancel = true;
  }

  /// <summary>
  /// Asks the user whether to discard unsaved changes. Returns true when
  /// the caller may proceed (user chose Discard) and false when they should
  /// abort (user chose Cancel). The Save button is also offered for
  /// convenience; choosing Save persists then returns true.
  /// </summary>
  private bool PromptDiscardChanges() {
    var result = MessageBox.Show(this,
      "Profile has unsaved changes. Save before continuing?",
      "Unsaved changes",
      MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
    switch (result) {
      case MessageBoxResult.Yes:
        OnSave(this, new RoutedEventArgs());
        return !this._state.IsDirty; // success only if save actually clean-flagged us
      case MessageBoxResult.No:
        this._state.MarkClean();
        return true;
      default:
        return false;
    }
  }

  private void UpdateButtonStates() {
    var hasZone = this._selectedZone != null;
    var zoneIdx = ZonesList.SelectedIndex;
    RemoveZoneBtn.IsEnabled = zoneIdx >= 0;
    MoveUpBtn.IsEnabled = zoneIdx > 0;
    MoveDownBtn.IsEnabled = zoneIdx >= 0 && zoneIdx < this._zoneRows.Count - 1;
    AddSortBtn.IsEnabled = hasZone;
    RemoveSortBtn.IsEnabled = hasZone && SortList.SelectedIndex >= 0;

    var entry = this._state.SourceEntry;
    var canSave = entry == null || entry.Origin == ProfileOrigin.User;
    SaveBtn.IsEnabled = canSave && this._state.IsDirty;
    if (entry?.Origin == ProfileOrigin.Builtin) {
      SaveBtn.ToolTip = "Built-in profiles are read-only — use Save As to make a copy.";
    } else {
      SaveBtn.ToolTip = this._state.IsDirty ? "Save changes to the current profile." : "Nothing to save.";
    }
    DeleteBtn.IsEnabled = entry?.Origin == ProfileOrigin.User;
  }

  private void UpdateStatus(string? msg) {
    StatusLbl.Text = msg ?? (this._state.IsDirty ? "Modified." : "");
  }

  private static void SelectComboByTag(ComboBox combo, string tag) {
    for (var i = 0; i < combo.Items.Count; i++) {
      if (combo.Items[i] is ComboBoxItem item
          && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)) {
        combo.SelectedIndex = i;
        return;
      }
    }
    if (combo.Items.Count > 0) combo.SelectedIndex = 0;
  }

  // ── Row view-models for the list/grid controls ──────────────────────────

  /// <summary>Row binding for the left-rail profile list.</summary>
  private sealed class ProfileListItem(LayoutProfileEntry entry) {
    public LayoutProfileEntry Entry { get; } = entry;
    public string DisplayName => this.Entry.Name;
    public string BadgeText => this.Entry.Origin == ProfileOrigin.Builtin ? "Built-in" : "User";
    public Brush BadgeBrush => this.Entry.Origin == ProfileOrigin.Builtin
      ? new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70))
      : new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5));
  }

  /// <summary>Row binding for the zones list. Mirrors the source zone so
  /// updates to the underlying model show up after a list refresh.</summary>
  private sealed class ZoneRow(LayoutProfileEditorState.EditableZone src) {
    public LayoutProfileEditorState.EditableZone Source { get; } = src;
    public string Name => this.Source.Name;
    public string Range => this.Source.Range;
    public string Filter => this.Source.Filter;
    public string SortSummary => this.Source.SortBy.Count == 0
      ? "—"
      : string.Join(", ", this.Source.SortBy.Select(s => $"{FieldToText(s.Field)} {DirToText(s.Direction)}"));

    private static string FieldToText(DefragSortField f) => f switch {
      DefragSortField.Name => "name",
      DefragSortField.Path => "path",
      DefragSortField.Extension => "extension",
      DefragSortField.Size => "size",
      DefragSortField.LastModified => "lastModified",
      DefragSortField.LastAccessed => "lastAccessed",
      DefragSortField.Created => "created",
      DefragSortField.Attributes => "attributes",
      _ => f.ToString().ToLowerInvariant(),
    };
    private static string DirToText(SortDirection d)
      => d == SortDirection.Ascending ? "asc" : "desc";
  }

  /// <summary>
  /// Row binding for the sort-by list. We bind ComboBox.SelectedItem to
  /// string properties so the cell template doesn't need x:Static enum
  /// references (which add XAML compile complexity).
  /// </summary>
  public sealed class SortRow : INotifyPropertyChanged {
    private string _fieldText = "name";
    private string _directionText = "ascending";

    public IReadOnlyList<string> FieldOptions { get; } = ["name", "path", "extension", "size", "lastModified", "lastAccessed", "created", "attributes"];
    public IReadOnlyList<string> DirectionOptions { get; } = ["ascending", "descending"];

    public string FieldText {
      get => this._fieldText;
      set {
        if (this._fieldText == value) return;
        this._fieldText = value ?? "name";
        OnChanged(nameof(this.FieldText));
      }
    }
    public string DirectionText {
      get => this._directionText;
      set {
        if (this._directionText == value) return;
        this._directionText = value ?? "ascending";
        OnChanged(nameof(this.DirectionText));
      }
    }

    public DefragSortField Field => this._fieldText switch {
      "path" => DefragSortField.Path,
      "extension" => DefragSortField.Extension,
      "size" => DefragSortField.Size,
      "lastModified" => DefragSortField.LastModified,
      "lastAccessed" => DefragSortField.LastAccessed,
      "created" => DefragSortField.Created,
      "attributes" => DefragSortField.Attributes,
      _ => DefragSortField.Name,
    };
    public SortDirection Direction
      => this._directionText == "descending" ? SortDirection.Descending : SortDirection.Ascending;

    public static SortRow From(LayoutProfileEditorState.EditableSortKey k) => new() {
      _fieldText = k.Field switch {
        DefragSortField.Path => "path",
        DefragSortField.Extension => "extension",
        DefragSortField.Size => "size",
        DefragSortField.LastModified => "lastModified",
        DefragSortField.LastAccessed => "lastAccessed",
        DefragSortField.Created => "created",
        DefragSortField.Attributes => "attributes",
        _ => "name",
      },
      _directionText = k.Direction == SortDirection.Descending ? "descending" : "ascending",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
  }
}
