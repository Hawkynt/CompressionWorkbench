using Compression.Registry;
using Compression.Registry.Layout;

namespace Compression.Lib.Layout;

/// <summary>
/// Pure logic backing the <c>LayoutProfileEditor</c> WPF window: dirty
/// tracking, validation, and "can save" predicates. Keeping these out of
/// code-behind makes them unit-testable without spinning up WPF.
///
/// <para>The model mirrors a <see cref="LayoutTemplate"/> but exposes
/// mutable fields so the editor can capture in-progress edits before the
/// user commits. <see cref="Build"/> snapshots the current state into an
/// immutable <see cref="LayoutTemplate"/>.</para>
/// </summary>
public sealed class LayoutProfileEditorState {
  private string _name = "New profile";
  private MetadataZone _metadataZone = MetadataZone.Unchanged;
  private string _leftoverStrategy = "fill_gaps";
  private bool _isDirty;

  /// <summary>Mutable zone list — editor reorders / inserts / removes here.</summary>
  public List<EditableZone> Zones { get; } = [];

  /// <summary>Underlying profile entry; null = unsaved in-memory profile.</summary>
  public LayoutProfileEntry? SourceEntry { get; private set; }

  /// <summary>True after any property is mutated since the last
  /// <see cref="MarkClean"/> / <see cref="LoadFrom(LayoutTemplate, LayoutProfileEntry?)"/>.</summary>
  public bool IsDirty => this._isDirty;

  /// <summary>Profile name. Setting this marks the state dirty.</summary>
  public string Name {
    get => this._name;
    set { if (this._name == value) return; this._name = value ?? string.Empty; this._isDirty = true; }
  }

  /// <summary>Metadata zone placement. Setting this marks the state dirty.</summary>
  public MetadataZone MetadataZone {
    get => this._metadataZone;
    set { if (this._metadataZone == value) return; this._metadataZone = value; this._isDirty = true; }
  }

  /// <summary>Leftover strategy text (<c>fill_gaps</c> / <c>append_at_end</c>).</summary>
  public string LeftoverStrategy {
    get => this._leftoverStrategy;
    set { if (this._leftoverStrategy == value) return; this._leftoverStrategy = value ?? "fill_gaps"; this._isDirty = true; }
  }

  /// <summary>Marks the state clean (e.g. after a successful save).</summary>
  public void MarkClean() => this._isDirty = false;

  /// <summary>Marks the state dirty. Editor calls this from change handlers
  /// on collections (e.g. when a zone is added, removed, or reordered) that
  /// the property setters can't see.</summary>
  public void MarkDirty() => this._isDirty = true;

  /// <summary>
  /// Hydrates this state from <paramref name="template"/>, replacing any
  /// existing fields and clearing the dirty flag. <paramref name="entry"/>
  /// is the on-disk entry the template was loaded from (<c>null</c> for an
  /// unsaved new profile).
  /// </summary>
  public void LoadFrom(LayoutTemplate template, LayoutProfileEntry? entry) {
    ArgumentNullException.ThrowIfNull(template);
    this._name = template.Name;
    this._metadataZone = template.MetadataZone;
    this._leftoverStrategy = template.LeftoverStrategyText;
    this.Zones.Clear();
    foreach (var z in template.Zones)
      this.Zones.Add(EditableZone.FromZone(z));
    this.SourceEntry = entry;
    this._isDirty = false;
  }

  /// <summary>
  /// Snapshots the current editor state into an immutable
  /// <see cref="LayoutTemplate"/>. Validates each zone — throws
  /// <see cref="FormatException"/> when range / filter / sort expressions
  /// can't be parsed.
  /// </summary>
  public LayoutTemplate Build() {
    if (string.IsNullOrWhiteSpace(this._name))
      throw new FormatException("Profile name cannot be blank.");

    var zones = new List<LayoutZone>(this.Zones.Count);
    foreach (var z in this.Zones) {
      var built = z.Build();
      zones.Add(built);
    }

    return new LayoutTemplate {
      Name = this._name,
      MetadataZone = this._metadataZone,
      LeftoverStrategyText = string.IsNullOrWhiteSpace(this._leftoverStrategy) ? "fill_gaps" : this._leftoverStrategy,
      Zones = zones,
    };
  }

  /// <summary>
  /// True when the editor is bound to a writable profile (an unsaved new
  /// profile or a user profile). False when the source is a built-in.
  /// </summary>
  public bool CanSave => this.SourceEntry?.Origin != ProfileOrigin.Builtin;

  /// <summary>Editor binding for a single zone.</summary>
  public sealed class EditableZone {
    public string Name { get; set; } = "";
    public string Range { get; set; } = "0%-100%";
    public string Filter { get; set; } = "";
    public List<EditableSortKey> SortBy { get; } = [];

    public static EditableZone FromZone(LayoutZone z) {
      var ez = new EditableZone {
        Name = z.Name,
        Range = z.Range,
        Filter = z.Filter ?? string.Empty,
      };
      foreach (var s in z.SortBy)
        ez.SortBy.Add(new EditableSortKey { Field = s.Field, Direction = s.Direction });
      return ez;
    }

    public LayoutZone Build() {
      if (string.IsNullOrWhiteSpace(this.Name))
        throw new FormatException("Zone name cannot be blank.");
      try { _ = RangeSpec.Parse(this.Range); }
      catch (FormatException ex) { throw new FormatException($"Zone '{this.Name}' range: {ex.Message}", ex); }

      var filter = string.IsNullOrWhiteSpace(this.Filter) ? null : this.Filter.Trim();
      if (filter != null) {
        try { _ = FilterExpression.Parse(filter); }
        catch (FormatException ex) { throw new FormatException($"Zone '{this.Name}' filter: {ex.Message}", ex); }
      }

      var sortKeys = this.SortBy.Select(s => new DefragSortKey(s.Field, s.Direction)).ToList();
      return new LayoutZone {
        Name = this.Name,
        Range = this.Range,
        Filter = filter,
        SortBy = sortKeys,
      };
    }
  }

  /// <summary>Editor binding for a single sort key row.</summary>
  public sealed class EditableSortKey {
    public DefragSortField Field { get; set; } = DefragSortField.Name;
    public SortDirection Direction { get; set; } = SortDirection.Ascending;
  }

  // ── Validation helpers (testable, no WPF dependency) ────────────────────

  /// <summary>
  /// Validates a range spec string. Returns <c>null</c> on success or the
  /// parser's error message on failure. The editor surfaces this as inline
  /// red text next to the range textbox.
  /// </summary>
  public static string? ValidateRange(string range) {
    if (string.IsNullOrWhiteSpace(range)) return "Range is required.";
    try { _ = RangeSpec.Parse(range); return null; }
    catch (FormatException ex) { return ex.Message; }
  }

  /// <summary>
  /// Validates a filter expression. Empty / whitespace is treated as "no
  /// filter" and considered valid. Returns the parser's error message on
  /// failure, or <c>null</c> on success.
  /// </summary>
  public static string? ValidateFilter(string filter) {
    if (string.IsNullOrWhiteSpace(filter)) return null;
    try { _ = FilterExpression.Parse(filter); return null; }
    catch (FormatException ex) { return ex.Message; }
  }
}
