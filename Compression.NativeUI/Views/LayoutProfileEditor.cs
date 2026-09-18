using System.Drawing;
using Compression.Lib.Layout;
using Compression.Registry;
using Compression.Registry.Layout;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Editor for layout-profile templates: a profile list on the left, and on the right the header
/// fields, the ordered zone list and the selected zone's detail. Validation surfaces inline beside
/// the offending field so mistakes are caught before the planner sees them.
/// <para>
/// The dirty tracking and can-save rules live in <see cref="LayoutProfileEditorState"/>, which is
/// testable without a window; this class only maps that state onto controls.
/// </para>
/// </summary>
internal sealed class LayoutProfileEditor : Form {
  private const int LabelWidth = 130;
  private static readonly Color ErrorColor = Color.FromArgb(0xC0, 0x30, 0x30);

  private static readonly string[] FieldOptions =
    ["name", "path", "extension", "size", "lastModified", "lastAccessed", "created", "attributes"];
  private static readonly string[] DirectionOptions = ["ascending", "descending"];

  private static readonly string[] MetadataZoneOptions = ["Unchanged", "Front", "Back", "Middle", "BeforeContent"];
  private static readonly string[] LeftoverOptions = ["fill_gaps", "append_at_end"];

  private const string FieldReference = """
    Fields:
    name, path, extension, size, lastModified, lastAccessed, created, attributes
    Operators: = != < <= > >= contains matches in
    Logical: and, or, not (parentheses supported)
    Functions:
      quartile(0..1)  — p-th percentile across the file set (numeric/date fields)
      now() / today()  — current UTC time / midnight
      days(n) / hours(n) / minutes(n)  — durations (subtract from now() / today())
      date("yyyy-MM-dd")  — explicit date literal
    Examples:
      lastModified >= today() - days(30)
      size > 1MB and name matches '.*\.log$'
      extension in ('.exe', '.dll', '.sys')
    """;

  // Left rail.
  private readonly GroupBox _profilesGroup = new() { Text = "Profiles" };
  private readonly Button _new = new() { Text = "New" };
  private readonly Button _saveAsTop = new() { Text = "Save As…" };
  private readonly Button _delete = new() { Text = "Delete", Enabled = false };
  private readonly ListBox _profilesList = new();

  // Header fields.
  private readonly GroupBox _editorGroup = new() { Text = "Editor" };
  private readonly TextBox _name = new();
  private readonly ComboBox _metadataZone = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _leftover = new() { DropDownStyle = ComboBoxStyle.DropDownList };

  // Zone list.
  private readonly DataGridView _zonesList = new() { ReadOnly = true, ShowGridLines = true };
  private readonly Button _addZone = new() { Text = "+ Add" };
  private readonly Button _removeZone = new() { Text = "– Remove", Enabled = false };
  private readonly Button _moveUp = new() { Text = "↑ Up", Enabled = false };
  private readonly Button _moveDown = new() { Text = "↓ Down", Enabled = false };

  // Zone detail.
  private readonly Panel _zoneDetail = new() { AutoScroll = true, Enabled = false };
  private readonly TextBox _zoneName = new();
  private readonly TextBox _zoneRange = new();
  private readonly Button _validateRange = new() { Text = "Validate" };
  private readonly Label _zoneRangeError = new() { ForeColor = ErrorColor };
  private readonly RangePreviewBar _rangePreview = new();
  private readonly TextBox _zoneFilter = new() { Multiline = true, AcceptsReturn = true };
  private readonly Button _validateFilter = new() { Text = "Validate" };
  private readonly Label _zoneFilterError = new() { ForeColor = ErrorColor };
  private readonly Expander _fieldReference = new() { Text = "Show field reference" };
  private readonly TextBox _fieldReferenceText = new() { Multiline = true, ReadOnly = true, Text = FieldReference };
  private readonly Button _addSort = new() { Text = "+ Add", Enabled = false };
  private readonly Button _removeSort = new() { Text = "– Remove", Enabled = false };
  private readonly DataGridView _sortList = new() { ShowGridLines = true };

  // Footer.
  private readonly Label _status = new() { ForeColor = Color.DimGray };
  private readonly Button _save = new() { Text = "Save", Enabled = false };
  private readonly Button _saveAs = new() { Text = "Save As…" };
  private readonly Button _close = new() { Text = "Close" };
  private readonly ToolTip _toolTips = new();

  private readonly LayoutProfileEditorState _state = new();
  private readonly List<LayoutProfileEntry> _profileEntries = [];
  private readonly List<ZoneRow> _zoneRows = [];
  private readonly List<SortRow> _sortRows = [];

  private LayoutProfileEditorState.EditableZone? _selectedZone;
  private bool _suppressEvents;

  public LayoutProfileEditor() {
    this.Text = "Layout profiles";
    this.ClientSize = new(1000, 640);
    this.MinimumSize = new(780, 500);
    this.StartPosition = FormStartPosition.CenterParent;

    this.BuildLeftRail();
    this.BuildHeaderFields();
    this.BuildZoneList();
    this.BuildZoneDetail();
    this.BuildFooter();

    this.Controls.AddRange(this._profilesGroup, this._editorGroup, this._status, this._save, this._saveAs, this._close);
    this.FormClosing += this.OnFormClosing;
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();

    this.RefreshProfilesList(initialSelectFirst: true);
  }

  // ── Construction ────────────────────────────────────────────────────────────────────────────

  private void BuildLeftRail() {
    this._new.Click += (_, _) => this.OnNew();
    this._saveAsTop.Click += (_, _) => this.OnSaveAs();
    this._delete.Click += (_, _) => this.OnDelete();
    this._profilesList.SelectedIndexChanged += (_, _) => this.OnProfileSelectionChanged();

    this._profilesGroup.Controls.AddRange(this._new, this._saveAsTop, this._delete, this._profilesList);
    this.Controls.Add(this._profilesGroup);
  }

  private void BuildHeaderFields() {
    this._name.TextChanged += (_, _) => {
      if (this._suppressEvents) return;
      this._state.Name = this._name.Text;
      this.UpdateButtonStates();
    };

    this._metadataZone.Items.AddRange(MetadataZoneOptions);
    this._metadataZone.SelectedIndex = 0;
    this._toolTips.SetToolTip(this._metadataZone,
      "Where filesystem metadata (FAT, MFT, bitmaps, inode tables) and directory extents land during defragmentation.");
    this._metadataZone.SelectedIndexChanged += (_, _) => {
      if (this._suppressEvents) return;
      this._state.MetadataZone = Enum.TryParse<MetadataZone>(this._metadataZone.SelectedItem as string, out var mz) ? mz : MetadataZone.Unchanged;
      this.UpdateButtonStates();
    };

    this._leftover.Items.AddRange(LeftoverOptions);
    this._leftover.SelectedIndex = 0;
    this._toolTips.SetToolTip(this._leftover,
      "Where files matching no zone land. 'fill_gaps' = between zones, 'append_at_end' = after the last zone.");
    this._leftover.SelectedIndexChanged += (_, _) => {
      if (this._suppressEvents) return;
      this._state.LeftoverStrategy = this._leftover.SelectedItem as string ?? "fill_gaps";
      this.UpdateButtonStates();
    };

    this._editorGroup.Controls.AddRange(
      new Label { Text = "Name:" }, this._name,
      new Label { Text = "Metadata zone:" }, this._metadataZone,
      new Label { Text = "Leftover strategy:" }, this._leftover,
      new Label {
        Text = "Zones (placement order: first match wins)",
        Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold),
      });

    this.Controls.Add(this._editorGroup);
  }

  private void BuildZoneList() {
    this._zonesList.Columns.Add(new DataGridViewColumn("Name", static o => ((ZoneRow)o!).Name) { Width = 120 });
    this._zonesList.Columns.Add(new DataGridViewColumn("Range", static o => ((ZoneRow)o!).Range) { Width = 120 });
    this._zonesList.Columns.Add(new DataGridViewColumn("Filter", static o => ((ZoneRow)o!).Filter) { Width = 220 });
    this._zonesList.Columns.Add(new DataGridViewColumn("Sort", static o => ((ZoneRow)o!).SortSummary) { Width = 180 });
    this._zonesList.SelectionChanged += (_, _) => {
      if (this._suppressEvents) return;
      this.ShowZone((this._zonesList.SelectedItem as ZoneRow)?.Source);
      this.UpdateButtonStates();
    };

    this._addZone.Click += (_, _) => this.OnAddZone();
    this._removeZone.Click += (_, _) => this.OnRemoveZone();
    this._moveUp.Click += (_, _) => this.MoveSelectedZone(-1);
    this._moveDown.Click += (_, _) => this.MoveSelectedZone(+1);

    this._editorGroup.Controls.AddRange(this._zonesList, this._addZone, this._removeZone, this._moveUp, this._moveDown,
      new Label {
        Text = "Selected zone",
        Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold),
      });
  }

  private void BuildZoneDetail() {
    this._zoneName.TextChanged += (_, _) => {
      if (this._suppressEvents || this._selectedZone is null) return;
      this._selectedZone.Name = this._zoneName.Text;
      this.RefreshSelectedZoneRow();
      this._state.MarkDirty();
      this.UpdateButtonStates();
    };

    this._toolTips.SetToolTip(this._zoneRange, "Examples: 0%-5%, 10MB-50MB, [1024, 2048), 5%-, -50%");
    this._zoneRange.TextChanged += (_, _) => {
      if (this._suppressEvents || this._selectedZone is null) return;
      this._selectedZone.Range = this._zoneRange.Text;
      this.ApplyRangeValidation(this._zoneRange.Text, markDirty: true);
      this.RefreshSelectedZoneRow();
      this.UpdateButtonStates();
    };

    this._validateRange.Click += (_, _) => {
      var message = LayoutProfileEditorState.ValidateRange(this._zoneRange.Text);
      this.SetRangeError(message);
      this._rangePreview.SetRange(message is null ? this._zoneRange.Text : null);
      if (message is null) MessageBox.Show(this, "Range OK.", "Validate", MessageBoxButtons.OK, MessageBoxIcon.Information);
    };

    this._toolTips.SetToolTip(this._zoneFilter, "Predicate expression, e.g. lastModified >= quartile(0.75)");
    this._zoneFilter.TextChanged += (_, _) => {
      if (this._suppressEvents || this._selectedZone is null) return;
      this._selectedZone.Filter = this._zoneFilter.Text;
      this.ApplyFilterValidation(this._zoneFilter.Text, markDirty: true);
      this.RefreshSelectedZoneRow();
      this.UpdateButtonStates();
    };

    this._validateFilter.Click += (_, _) => {
      var message = LayoutProfileEditorState.ValidateFilter(this._zoneFilter.Text);
      this.SetFilterError(message);
      if (message is null) MessageBox.Show(this, "Filter OK.", "Validate", MessageBoxButtons.OK, MessageBoxIcon.Information);
    };

    this._fieldReference.Controls.Add(this._fieldReferenceText);
    this._fieldReference.ExpandedChanged += (_, _) => this.LayoutZoneDetail();

    this._addSort.Click += (_, _) => this.OnAddSort();
    this._removeSort.Click += (_, _) => this.OnRemoveSort();

    var fieldColumn = new DataGridViewColumn("Field", static o => ((SortRow)o!).FieldText) {
      Width = 180,
      Kind = DataGridViewColumnKind.ComboBox,
      ItemsSelector = static _ => FieldOptions,
      ValueSetter = (o, v) => {
        if (o is SortRow row) row.FieldText = v as string ?? "name";
        this.SyncSortRows();
      },
    };
    var directionColumn = new DataGridViewColumn("Direction", static o => ((SortRow)o!).DirectionText) {
      Width = 120,
      Kind = DataGridViewColumnKind.ComboBox,
      ItemsSelector = static _ => DirectionOptions,
      ValueSetter = (o, v) => {
        if (o is SortRow row) row.DirectionText = v as string ?? "ascending";
        this.SyncSortRows();
      },
    };
    this._sortList.Columns.Add(fieldColumn);
    this._sortList.Columns.Add(directionColumn);
    this._sortList.SelectionChanged += (_, _) => this.UpdateButtonStates();

    this._zoneDetail.Controls.AddRange(
      new Label { Text = "Name:" }, this._zoneName,
      new Label { Text = "Range:" }, this._zoneRange, this._validateRange, this._zoneRangeError, this._rangePreview,
      new Label { Text = "Filter:" }, this._zoneFilter, this._validateFilter, this._zoneFilterError,
      this._fieldReference,
      new Label { Text = "Sort by:" }, this._addSort, this._removeSort, this._sortList);

    this._editorGroup.Controls.Add(this._zoneDetail);
  }

  private void BuildFooter() {
    this._save.Click += (_, _) => this.OnSave();
    this._saveAs.Click += (_, _) => this.OnSaveAs();
    this._close.Click += (_, _) => this.Close();
    this.CancelButton = this._close;
  }

  // ── Layout ──────────────────────────────────────────────────────────────────────────────────

  private void LayoutChildren() {
    const int Gutter = 12;
    const int RailWidth = 260;
    var height = this.ClientSize.Height - 2 * Gutter - 40;

    this._profilesGroup.Bounds = new(Gutter, Gutter, RailWidth, height);
    this._new.Bounds = new(8, 22, 62, 26);
    this._saveAsTop.Bounds = new(74, 22, 80, 26);
    this._delete.Bounds = new(158, 22, 62, 26);
    this._profilesList.Bounds = new(8, 54, RailWidth - 16, Math.Max(40, height - 62));

    var editorX = Gutter + RailWidth + 8;
    var editorWidth = this.ClientSize.Width - editorX - Gutter;
    this._editorGroup.Bounds = new(editorX, Gutter, editorWidth, height);

    var inner = editorWidth - 16;
    var y = 24;
    LayoutField(this._editorGroup.Controls[0], this._name, y, inner);
    y += 28;
    LayoutField(this._editorGroup.Controls[2], this._metadataZone, y, inner);
    y += 28;
    LayoutField(this._editorGroup.Controls[4], this._leftover, y, inner);
    y += 34;

    this._editorGroup.Controls[6].Bounds = new(8, y, inner, 20); // zones heading
    y += 24;

    var zoneListHeight = Math.Max(80, (height - y - 40) / 3);
    this._zonesList.Bounds = new(8, y, Math.Max(100, inner - 80), zoneListHeight);
    var buttonX = 8 + inner - 74;
    this._addZone.Bounds = new(buttonX, y, 68, 24);
    this._removeZone.Bounds = new(buttonX, y + 28, 68, 24);
    this._moveUp.Bounds = new(buttonX, y + 56, 68, 24);
    this._moveDown.Bounds = new(buttonX, y + 84, 68, 24);
    y += zoneListHeight + 8;

    this._editorGroup.Controls[11].Bounds = new(8, y, inner, 20); // selected-zone heading
    y += 22;

    this._zoneDetail.Bounds = new(8, y, inner, Math.Max(80, height - y - 12));
    this.LayoutZoneDetail();

    var footerY = this.ClientSize.Height - Gutter - 28;
    this._close.Bounds = new(this.ClientSize.Width - Gutter - 80, footerY, 80, 28);
    this._saveAs.Bounds = new(this.ClientSize.Width - Gutter - 178, footerY, 90, 28);
    this._save.Bounds = new(this.ClientSize.Width - Gutter - 266, footerY, 80, 28);
    this._status.Bounds = new(Gutter, footerY + 4, this.ClientSize.Width - Gutter - 280, 20);

    static void LayoutField(Control caption, Control field, int top, int width) {
      caption.Bounds = new(8, top + 3, LabelWidth - 8, 20);
      field.Bounds = new(LabelWidth, top, Math.Max(80, width - LabelWidth), 24);
    }
  }

  private void LayoutZoneDetail() {
    var width = Math.Max(200, this._zoneDetail.Width - 20);
    var fieldWidth = Math.Max(80, width - LabelWidth - 90);
    var controls = this._zoneDetail.Controls;
    var y = 4;

    controls[0].Bounds = new(0, y + 3, LabelWidth, 20);
    this._zoneName.Bounds = new(LabelWidth, y, fieldWidth + 84, 24);
    y += 30;

    controls[2].Bounds = new(0, y + 3, LabelWidth, 20);
    this._zoneRange.Bounds = new(LabelWidth, y, fieldWidth, 24);
    this._validateRange.Bounds = new(LabelWidth + fieldWidth + 6, y, 80, 24);
    y += 28;

    this._zoneRangeError.Bounds = new(LabelWidth, y, width - LabelWidth, 16);
    y += 18;
    this._rangePreview.Bounds = new(LabelWidth, y, width - LabelWidth, 14);
    y += 20;

    controls[7].Bounds = new(0, y + 3, LabelWidth, 20);
    this._zoneFilter.Bounds = new(LabelWidth, y, fieldWidth, 48);
    this._validateFilter.Bounds = new(LabelWidth + fieldWidth + 6, y, 80, 24);
    y += 52;

    this._zoneFilterError.Bounds = new(LabelWidth, y, width - LabelWidth, 16);
    y += 20;

    var referenceHeight = this._fieldReference.Expanded ? 190 : 26;
    this._fieldReference.Bounds = new(LabelWidth, y, width - LabelWidth, referenceHeight);
    this._fieldReferenceText.Bounds = new(4, 26, Math.Max(60, width - LabelWidth - 12), referenceHeight - 32);
    y += referenceHeight + 8;

    controls[12].Bounds = new(0, y + 3, LabelWidth, 20);
    this._addSort.Bounds = new(LabelWidth, y, 68, 24);
    this._removeSort.Bounds = new(LabelWidth + 72, y, 80, 24);
    y += 30;

    this._sortList.Bounds = new(LabelWidth, y, width - LabelWidth, 110);
  }

  // ── Profile list ────────────────────────────────────────────────────────────────────────────

  private void RefreshProfilesList(bool initialSelectFirst) {
    var previousPath = this.SelectedEntry()?.FilePath;

    this._suppressEvents = true;
    try {
      this._profilesList.Items.Clear();
      this._profileEntries.Clear();
      foreach (var entry in LayoutProfileStore.List()) {
        this._profilesList.Items.Add($"{entry.Name}   [{(entry.Origin == ProfileOrigin.Builtin ? "Built-in" : "User")}]");
        this._profileEntries.Add(entry);
      }
    } finally {
      this._suppressEvents = false;
    }

    if (this._profileEntries.Count == 0) {
      this.ClearEditor();
      return;
    }

    if (previousPath is not null)
      for (var i = 0; i < this._profileEntries.Count; ++i)
        if (string.Equals(this._profileEntries[i].FilePath, previousPath, StringComparison.OrdinalIgnoreCase)) {
          this._profilesList.SelectedIndex = i;
          return;
        }

    if (initialSelectFirst) this._profilesList.SelectedIndex = 0;
  }

  private LayoutProfileEntry? SelectedEntry() {
    var index = this._profilesList.SelectedIndex;
    return index >= 0 && index < this._profileEntries.Count ? this._profileEntries[index] : null;
  }

  private void OnProfileSelectionChanged() {
    if (this._suppressEvents) return;
    if (this.SelectedEntry() is not { } entry) {
      this.ClearEditor();
      return;
    }

    if (this._state.IsDirty && !this.PromptDiscardChanges()) {
      // Restore the previous selection without re-firing this handler.
      this._suppressEvents = true;
      try {
        this._profilesList.SelectedIndex = this._state.SourceEntry is { } source
          ? this._profileEntries.FindIndex(e => string.Equals(e.FilePath, source.FilePath, StringComparison.OrdinalIgnoreCase))
          : -1;
      } finally {
        this._suppressEvents = false;
      }
      return;
    }

    try {
      this.LoadStateIntoUi(LayoutProfileStore.Load(entry), entry);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Failed to load profile '{entry.Name}':\n{ex.Message}",
        "Profile load error", MessageBoxButtons.OK, MessageBoxIcon.Error);
      this.ClearEditor();
    }
  }

  private void ClearEditor() {
    this._suppressEvents = true;
    try {
      this._name.Text = "";
      this._metadataZone.SelectedIndex = 0;
      this._leftover.SelectedIndex = 0;
      this._zoneRows.Clear();
      this._zonesList.DataSource = this._zoneRows;
      this._sortRows.Clear();
      this._sortList.DataSource = this._sortRows;
      this._selectedZone = null;
      this._zoneDetail.Enabled = false;
      this._zoneName.Text = "";
      this._zoneRange.Text = "";
      this._zoneFilter.Text = "";
      this.ClearZoneErrors();
      this._rangePreview.SetRange(null);
    } finally {
      this._suppressEvents = false;
    }

    this.UpdateButtonStates();
    this.UpdateStatus(null);
  }

  /// <summary>
  /// Hydrates the controls from <paramref name="template"/> and parks the editor clean. Change
  /// events are suppressed while loading so the dirty flag does not latch on the initial fill.
  /// </summary>
  private void LoadStateIntoUi(LayoutTemplate template, LayoutProfileEntry? entry) {
    this._state.LoadFrom(template, entry);

    this._suppressEvents = true;
    try {
      this._name.Text = this._state.Name;
      this._metadataZone.SelectedItem = this._state.MetadataZone.ToString();
      this._leftover.SelectedItem = this._state.LeftoverStrategy;

      this._zoneRows.Clear();
      foreach (var zone in this._state.Zones) this._zoneRows.Add(new(zone));
      this._zonesList.DataSource = this._zoneRows;
    } finally {
      this._suppressEvents = false;
    }

    if (this._zoneRows.Count > 0) this._zonesList.SelectedItem = this._zoneRows[0];
    else this.ShowZone(null);

    this.UpdateButtonStates();
    this.UpdateStatus(null);
  }

  // ── Zones ───────────────────────────────────────────────────────────────────────────────────

  private void ShowZone(LayoutProfileEditorState.EditableZone? zone) {
    this._selectedZone = zone;
    this._suppressEvents = true;

    try {
      this._sortRows.Clear();

      if (zone is null) {
        this._zoneDetail.Enabled = false;
        this._zoneName.Text = "";
        this._zoneRange.Text = "";
        this._zoneFilter.Text = "";
        this._sortList.DataSource = this._sortRows;
        this.ClearZoneErrors();
        this._rangePreview.SetRange(null);
        return;
      }

      this._zoneDetail.Enabled = true;
      this._zoneName.Text = zone.Name;
      this._zoneRange.Text = zone.Range;
      this._zoneFilter.Text = zone.Filter;

      foreach (var key in zone.SortBy) this._sortRows.Add(SortRow.From(key));
      this._sortList.DataSource = this._sortRows;

      this.ClearZoneErrors();
      // Validate without marking dirty: this is the initial display, not an edit.
      this.ApplyRangeValidation(zone.Range, markDirty: false);
      this.ApplyFilterValidation(zone.Filter, markDirty: false);
    } finally {
      this._suppressEvents = false;
    }
  }

  private void OnAddZone() {
    var zone = new LayoutProfileEditorState.EditableZone {
      Name = $"zone{this._state.Zones.Count + 1}",
      Range = "0%-100%",
    };

    this._state.Zones.Add(zone);
    this._zoneRows.Add(new(zone));
    this._zonesList.DataSource = this._zoneRows;
    this._state.MarkDirty();
    this._zonesList.SelectedItem = this._zoneRows[^1];
    this.UpdateButtonStates();
  }

  private void OnRemoveZone() {
    var index = this.SelectedZoneIndex();
    if (index < 0) return;

    this._state.Zones.RemoveAt(index);
    this._zoneRows.RemoveAt(index);
    this._zonesList.DataSource = this._zoneRows;
    this._state.MarkDirty();

    if (this._zoneRows.Count > 0) this._zonesList.SelectedItem = this._zoneRows[Math.Min(index, this._zoneRows.Count - 1)];
    else this.ShowZone(null);

    this.UpdateButtonStates();
  }

  private void MoveSelectedZone(int delta) {
    var index = this.SelectedZoneIndex();
    var target = index + delta;
    if (index < 0 || target < 0 || target >= this._zoneRows.Count) return;

    (this._state.Zones[index], this._state.Zones[target]) = (this._state.Zones[target], this._state.Zones[index]);
    (this._zoneRows[index], this._zoneRows[target]) = (this._zoneRows[target], this._zoneRows[index]);
    this._zonesList.DataSource = this._zoneRows;
    this._state.MarkDirty();
    this._zonesList.SelectedItem = this._zoneRows[target];
    this.UpdateButtonStates();
  }

  private int SelectedZoneIndex()
    => this._zonesList.SelectedItem is ZoneRow row ? this._zoneRows.IndexOf(row) : -1;

  private void RefreshSelectedZoneRow() {
    var index = this.SelectedZoneIndex();
    if (index < 0 || this._selectedZone is null) return;

    this._zoneRows[index] = new(this._selectedZone);
    this._zonesList.DataSource = this._zoneRows;
    this._zonesList.SelectedItem = this._zoneRows[index];
  }

  // ── Validation ──────────────────────────────────────────────────────────────────────────────

  private void ApplyRangeValidation(string range, bool markDirty) {
    var message = LayoutProfileEditorState.ValidateRange(range);
    this.SetRangeError(message);
    this._rangePreview.SetRange(message is null ? range : null);
    if (markDirty) this._state.MarkDirty();
  }

  private void ApplyFilterValidation(string filter, bool markDirty) {
    this.SetFilterError(LayoutProfileEditorState.ValidateFilter(filter));
    if (markDirty) this._state.MarkDirty();
  }

  private void SetRangeError(string? message) {
    this._zoneRangeError.Text = message ?? "";
    this._zoneRange.BackColor = message is null ? DefaultTheme.Instance.FieldBackground : Color.FromArgb(0xFF, 0xF0, 0xF0);
  }

  private void SetFilterError(string? message) {
    this._zoneFilterError.Text = message ?? "";
    this._zoneFilter.BackColor = message is null ? DefaultTheme.Instance.FieldBackground : Color.FromArgb(0xFF, 0xF0, 0xF0);
  }

  private void ClearZoneErrors() {
    this.SetRangeError(null);
    this.SetFilterError(null);
  }

  // ── Sort keys ───────────────────────────────────────────────────────────────────────────────

  private void OnAddSort() {
    if (this._selectedZone is null) return;

    var key = new LayoutProfileEditorState.EditableSortKey();
    this._selectedZone.SortBy.Add(key);
    this._sortRows.Add(SortRow.From(key));
    this._sortList.DataSource = this._sortRows;
    this._state.MarkDirty();
    this.RefreshSelectedZoneRow();
    this.UpdateButtonStates();
  }

  private void OnRemoveSort() {
    if (this._selectedZone is null) return;

    var index = this._sortList.SelectedItem is SortRow row ? this._sortRows.IndexOf(row) : -1;
    if (index < 0) return;

    this._selectedZone.SortBy.RemoveAt(index);
    this._sortRows.RemoveAt(index);
    this._sortList.DataSource = this._sortRows;
    this._state.MarkDirty();
    this.RefreshSelectedZoneRow();
    this.UpdateButtonStates();
  }

  private void SyncSortRows() {
    if (this._suppressEvents || this._selectedZone is null) return;

    this._selectedZone.SortBy.Clear();
    foreach (var row in this._sortRows)
      this._selectedZone.SortBy.Add(new() { Field = row.Field, Direction = row.Direction });

    this._state.MarkDirty();
    this.RefreshSelectedZoneRow();
    this.UpdateButtonStates();
  }

  // ── Save, new, delete ───────────────────────────────────────────────────────────────────────

  private void OnNew() {
    if (this._state.IsDirty && !this.PromptDiscardChanges()) return;

    this._suppressEvents = true;
    try {
      this._profilesList.SelectedIndex = -1;
    } finally {
      this._suppressEvents = false;
    }

    this.LoadStateIntoUi(new() {
      Name = "New profile",
      MetadataZone = MetadataZone.Unchanged,
      LeftoverStrategyText = "fill_gaps",
      Zones = [],
    }, null);
  }

  private void OnSave() {
    if (this._state.SourceEntry is not { Origin: ProfileOrigin.User } source) {
      this.OnSaveAs();
      return;
    }

    if (!this.TryBuildTemplate(out var template)) return;

    try {
      var entry = LayoutProfileStore.Save(template, Path.GetFileName(source.FilePath));
      this._state.LoadFrom(template, entry);
      this.UpdateStatus($"Saved {Path.GetFileName(entry.FilePath)}.");
      this.RefreshProfilesList(initialSelectFirst: false);
      this.UpdateButtonStates();
    } catch (Exception ex) {
      MessageBox.Show(this, $"Save failed: {ex.Message}", "Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  private void OnSaveAs() {
    if (!this.TryBuildTemplate(out var template)) return;

    var dialog = new SaveFileDialog {
      Title = "Save layout profile",
      Filter = "Layout profile JSON|*.json|All files|*.*",
      InitialDirectory = SafeDirectory(LayoutProfileStore.UserDirectory),
      FileName = LayoutProfileStore.SuggestFileName(template.Name),
    };
    if (dialog.ShowDialog() != DialogResult.OK) return;

    try {
      // The store handles directory creation and extension normalisation.
      var entry = LayoutProfileStore.Save(template, Path.GetFileName(dialog.FileName));
      this._state.LoadFrom(template, entry);
      this.UpdateStatus($"Saved {Path.GetFileName(entry.FilePath)}.");
      this.RefreshProfilesList(initialSelectFirst: false);

      var index = this._profileEntries.FindIndex(e => string.Equals(e.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
      if (index >= 0) this._profilesList.SelectedIndex = index;

      this.UpdateButtonStates();
    } catch (Exception ex) {
      MessageBox.Show(this, $"Save failed: {ex.Message}", "Save As", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  private static string SafeDirectory(string path) {
    try {
      Directory.CreateDirectory(path);
    } catch {
      // Fall through to the documents folder.
    }

    return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
  }

  private bool TryBuildTemplate(out LayoutTemplate template) {
    template = null!;
    try {
      template = this._state.Build();
      return true;
    } catch (FormatException ex) {
      MessageBox.Show(this, ex.Message, "Profile validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return false;
    }
  }

  private void OnDelete() {
    if (this.SelectedEntry() is not { } entry) return;

    if (entry.Origin == ProfileOrigin.Builtin) {
      MessageBox.Show(this, "Built-in profiles are read-only.", "Delete", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }

    if (MessageBox.Show(this, $"Delete profile '{entry.Name}'?", "Confirm delete",
        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

    try {
      LayoutProfileStore.Delete(entry);
      if (this._state.SourceEntry is { } source
          && string.Equals(source.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
        this.ClearEditor();

      this.RefreshProfilesList(initialSelectFirst: true);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Delete failed: {ex.Message}", "Delete", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  private void OnFormClosing(object? sender, FormClosingEventArgs e) {
    if (e.Cancel) return;
    if (this._state.IsDirty && !this.PromptDiscardChanges()) e.Cancel = true;
  }

  /// <summary>
  /// Asks whether to discard unsaved changes. Returns true when the caller may proceed and false
  /// when it should abort. Saving is offered too, and counts as proceeding only if it actually left
  /// the state clean.
  /// </summary>
  private bool PromptDiscardChanges() {
    switch (MessageBox.Show(this, "Profile has unsaved changes. Save before continuing?",
        "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question)) {
      case DialogResult.Yes:
        this.OnSave();
        return !this._state.IsDirty;
      case DialogResult.No:
        this._state.MarkClean();
        return true;
      default:
        return false;
    }
  }

  private void UpdateButtonStates() {
    var hasZone = this._selectedZone is not null;
    var zoneIndex = this.SelectedZoneIndex();

    this._removeZone.Enabled = zoneIndex >= 0;
    this._moveUp.Enabled = zoneIndex > 0;
    this._moveDown.Enabled = zoneIndex >= 0 && zoneIndex < this._zoneRows.Count - 1;
    this._addSort.Enabled = hasZone;
    this._removeSort.Enabled = hasZone && this._sortList.SelectedItem is SortRow;

    var entry = this._state.SourceEntry;
    var canSave = entry is null || entry.Origin == ProfileOrigin.User;
    this._save.Enabled = canSave && this._state.IsDirty;
    this._toolTips.SetToolTip(this._save, entry?.Origin == ProfileOrigin.Builtin
      ? "Built-in profiles are read-only — use Save As to make a copy."
      : this._state.IsDirty ? "Save changes to the current profile." : "Nothing to save.");
    this._delete.Enabled = entry?.Origin == ProfileOrigin.User;
  }

  private void UpdateStatus(string? message)
    => this._status.Text = message ?? (this._state.IsDirty ? "Modified." : "");

  // ── Row models ──────────────────────────────────────────────────────────────────────────────

  /// <summary>A zone as the list shows it; mirrors the source so edits appear after a refresh.</summary>
  private sealed class ZoneRow(LayoutProfileEditorState.EditableZone source) {
    public LayoutProfileEditorState.EditableZone Source { get; } = source;
    public string Name => this.Source.Name;
    public string Range => this.Source.Range;
    public string Filter => this.Source.Filter;

    public string SortSummary => this.Source.SortBy.Count == 0
      ? "—"
      : string.Join(", ", this.Source.SortBy.Select(s => $"{FieldToText(s.Field)} {(s.Direction == SortDirection.Ascending ? "asc" : "desc")}"));

    internal static string FieldToText(DefragSortField field) => field switch {
      DefragSortField.Path => "path",
      DefragSortField.Extension => "extension",
      DefragSortField.Size => "size",
      DefragSortField.LastModified => "lastModified",
      DefragSortField.LastAccessed => "lastAccessed",
      DefragSortField.Created => "created",
      DefragSortField.Attributes => "attributes",
      _ => "name",
    };
  }

  /// <summary>One sort key, held as display text so the grid's combo cells bind to strings.</summary>
  private sealed class SortRow {
    public string FieldText { get; set; } = "name";
    public string DirectionText { get; set; } = "ascending";

    public DefragSortField Field => this.FieldText switch {
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
      => this.DirectionText == "descending" ? SortDirection.Descending : SortDirection.Ascending;

    public static SortRow From(LayoutProfileEditorState.EditableSortKey key) => new() {
      FieldText = ZoneRow.FieldToText(key.Field),
      DirectionText = key.Direction == SortDirection.Descending ? "descending" : "ascending",
    };
  }

  /// <summary>
  /// A band across a 0–100% track showing where the zone falls. Absolute byte ranges are resolved
  /// against a notional one-gibibyte image so they still render something useful.
  /// </summary>
  private sealed class RangePreviewBar : OwnerDrawnControl {
    private const long NotionalImageSize = 1024L * 1024L * 1024L;

    private double _start;
    private double _end;

    public void SetRange(string? rangeText) {
      if (rangeText is null) {
        this._start = this._end = 0;
        this.Invalidate();
        return;
      }

      try {
        var (start, end) = RangeSpec.Parse(rangeText).Resolve(NotionalImageSize);
        this._start = (double)start / NotionalImageSize;
        this._end = (double)end / NotionalImageSize;
      } catch {
        this._start = this._end = 0;
      }

      this.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.FillRectangle(Color.FromArgb(0xF4, 0xF4, 0xF4), new(0, 0, this.Width, this.Height));
      if (this._end <= this._start) return;

      var x0 = (int)(this._start * this.Width);
      var x1 = (int)(this._end * this.Width);
      g.FillRectangle(Color.FromArgb(0x42, 0xA5, 0xF5), new(x0, 0, Math.Max(1, x1 - x0), this.Height));
    }
  }
}
