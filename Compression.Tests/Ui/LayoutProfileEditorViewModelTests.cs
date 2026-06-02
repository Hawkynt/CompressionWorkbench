#pragma warning disable CS1591
using Compression.Lib.Layout;
using Compression.Registry;
using Compression.Registry.Layout;

namespace Compression.Tests.Ui;

/// <summary>
/// Exercises <see cref="LayoutProfileEditorState"/> — the testable
/// view-model behind the WPF <c>LayoutProfileEditor</c> window. Covers
/// dirty tracking, validation surface, and the <c>CanSave</c> predicate.
///
/// <para>The editor's UI bindings dispatch into this object, so any
/// regression here corrupts the editor's enable/disable semantics or
/// loses the user's edits.</para>
/// </summary>
[TestFixture]
public class LayoutProfileEditorViewModelTests {

  // ── Dirty tracking ──────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void NewState_IsClean() {
    var s = new LayoutProfileEditorState();
    Assert.That(s.IsDirty, Is.False);
  }

  [Test, Category("HappyPath")]
  public void SettingName_MarksDirty() {
    var s = new LayoutProfileEditorState();
    s.Name = "Renamed";
    Assert.That(s.IsDirty, Is.True);
  }

  [Test, Category("Boundary")]
  public void SettingNameToSameValue_DoesNotMarkDirty() {
    var s = new LayoutProfileEditorState();
    s.MarkClean();
    s.Name = s.Name;
    Assert.That(s.IsDirty, Is.False);
  }

  [Test, Category("HappyPath")]
  public void SettingMetadataZone_MarksDirty() {
    var s = new LayoutProfileEditorState();
    s.MarkClean();
    s.MetadataZone = MetadataZone.Back;
    Assert.That(s.IsDirty, Is.True);
  }

  [Test, Category("HappyPath")]
  public void SettingLeftoverStrategy_MarksDirty() {
    var s = new LayoutProfileEditorState();
    s.MarkClean();
    s.LeftoverStrategy = "append_at_end";
    Assert.That(s.IsDirty, Is.True);
  }

  [Test, Category("HappyPath")]
  public void LoadFrom_ClearsDirtyFlag() {
    var s = new LayoutProfileEditorState();
    s.Name = "Pretend edit";
    Assert.That(s.IsDirty, Is.True);

    s.LoadFrom(SimpleTemplate("Loaded"), null);

    Assert.That(s.IsDirty, Is.False);
    Assert.That(s.Name, Is.EqualTo("Loaded"));
  }

  [Test, Category("HappyPath")]
  public void MarkDirty_FlipsFlag() {
    var s = new LayoutProfileEditorState();
    s.MarkDirty();
    Assert.That(s.IsDirty, Is.True);
    s.MarkClean();
    Assert.That(s.IsDirty, Is.False);
  }

  // ── CanSave predicate ──────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void CanSave_TrueForUnsavedInMemoryProfile() {
    var s = new LayoutProfileEditorState();
    Assert.That(s.CanSave, Is.True);
  }

  [Test, Category("HappyPath")]
  public void CanSave_TrueForUserProfile() {
    var s = new LayoutProfileEditorState();
    s.LoadFrom(SimpleTemplate("U"),
      new LayoutProfileEntry("U", "C:/tmp/u.json", ProfileOrigin.User));
    Assert.That(s.CanSave, Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void CanSave_FalseForBuiltinProfile() {
    var s = new LayoutProfileEditorState();
    s.LoadFrom(SimpleTemplate("B"),
      new LayoutProfileEntry("B", "C:/tmp/b.json", ProfileOrigin.Builtin));
    Assert.That(s.CanSave, Is.False);
  }

  // ── Validation surface ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ValidateRange_AcceptsKnownForms() {
    Assert.That(LayoutProfileEditorState.ValidateRange("0%-5%"), Is.Null);
    Assert.That(LayoutProfileEditorState.ValidateRange("10MB-50MB"), Is.Null);
    Assert.That(LayoutProfileEditorState.ValidateRange("[1024, 2048)"), Is.Null);
  }

  [Test, Category("ErrorHandling")]
  public void ValidateRange_RejectsBlank() {
    Assert.That(LayoutProfileEditorState.ValidateRange(""), Is.Not.Null);
    Assert.That(LayoutProfileEditorState.ValidateRange("   "), Is.Not.Null);
  }

  [Test, Category("ErrorHandling")]
  public void ValidateRange_ReturnsParseErrorMessage() {
    var msg = LayoutProfileEditorState.ValidateRange("totally broken garbage");
    Assert.That(msg, Is.Not.Null);
    Assert.That(msg, Is.Not.Empty);
  }

  [Test, Category("HappyPath")]
  public void ValidateFilter_AcceptsKnownForms() {
    Assert.That(LayoutProfileEditorState.ValidateFilter("size > 1MB"), Is.Null);
    Assert.That(LayoutProfileEditorState.ValidateFilter("name contains 'log'"), Is.Null);
    Assert.That(LayoutProfileEditorState.ValidateFilter("lastModified >= quartile(0.75)"), Is.Null);
  }

  [Test, Category("Boundary")]
  public void ValidateFilter_BlankIsValidNoFilter() {
    Assert.That(LayoutProfileEditorState.ValidateFilter(""), Is.Null);
    Assert.That(LayoutProfileEditorState.ValidateFilter("   "), Is.Null);
  }

  [Test, Category("ErrorHandling")]
  public void ValidateFilter_ReturnsParseErrorMessage() {
    var msg = LayoutProfileEditorState.ValidateFilter("notAField === bogus");
    Assert.That(msg, Is.Not.Null);
    Assert.That(msg, Is.Not.Empty);
  }

  // ── Build snapshot ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Build_ProducesEquivalentTemplate() {
    var s = new LayoutProfileEditorState();
    s.Name = "Round";
    s.MetadataZone = MetadataZone.Middle;
    s.LeftoverStrategy = "append_at_end";
    s.Zones.Add(new LayoutProfileEditorState.EditableZone {
      Name = "hot",
      Range = "0%-50%",
      Filter = "size > 1KB",
      SortBy = {
        new LayoutProfileEditorState.EditableSortKey { Field = DefragSortField.LastModified, Direction = SortDirection.Descending },
      },
    });

    var template = s.Build();

    Assert.That(template.Name, Is.EqualTo("Round"));
    Assert.That(template.MetadataZone, Is.EqualTo(MetadataZone.Middle));
    Assert.That(template.LeftoverStrategyText, Is.EqualTo("append_at_end"));
    Assert.That(template.Zones, Has.Count.EqualTo(1));
    Assert.That(template.Zones[0].Filter, Is.EqualTo("size > 1KB"));
    Assert.That(template.Zones[0].SortBy[0].Field, Is.EqualTo(DefragSortField.LastModified));
    Assert.That(template.Zones[0].SortBy[0].Direction, Is.EqualTo(SortDirection.Descending));
  }

  [Test, Category("ErrorHandling")]
  public void Build_ThrowsOnBlankName() {
    var s = new LayoutProfileEditorState { Name = " " };
    Assert.Throws<FormatException>(() => s.Build());
  }

  [Test, Category("ErrorHandling")]
  public void Build_ThrowsWhenZoneRangeMalformed() {
    var s = new LayoutProfileEditorState();
    s.Zones.Add(new LayoutProfileEditorState.EditableZone {
      Name = "z",
      Range = "this is not a range",
    });
    Assert.Throws<FormatException>(() => s.Build());
  }

  [Test, Category("ErrorHandling")]
  public void Build_ThrowsWhenFilterMalformed() {
    var s = new LayoutProfileEditorState();
    s.Zones.Add(new LayoutProfileEditorState.EditableZone {
      Name = "z",
      Range = "0%-100%",
      Filter = "notAField ?? bogus",
    });
    Assert.Throws<FormatException>(() => s.Build());
  }

  [Test, Category("HappyPath")]
  public void Build_EmptyFilterIsPersistedAsNull() {
    var s = new LayoutProfileEditorState();
    s.Zones.Add(new LayoutProfileEditorState.EditableZone {
      Name = "z",
      Range = "0%-100%",
      Filter = "   ",
    });
    var template = s.Build();
    Assert.That(template.Zones[0].Filter, Is.Null);
  }

  // ── LoadFrom mirrors zones into editable form ──────────────────────────

  [Test, Category("HappyPath")]
  public void LoadFrom_HydratesZonesAndSortKeys() {
    var src = new LayoutTemplate {
      Name = "src",
      MetadataZone = MetadataZone.Front,
      LeftoverStrategyText = "fill_gaps",
      Zones = [
        new LayoutZone {
          Name = "alpha",
          Range = "0%-25%",
          Filter = "size > 0",
          SortBy = [
            new DefragSortKey(DefragSortField.Size, SortDirection.Descending),
            new DefragSortKey(DefragSortField.Name, SortDirection.Ascending),
          ],
        },
      ],
    };
    var s = new LayoutProfileEditorState();
    s.LoadFrom(src, null);

    Assert.That(s.Zones, Has.Count.EqualTo(1));
    Assert.That(s.Zones[0].Name, Is.EqualTo("alpha"));
    Assert.That(s.Zones[0].SortBy, Has.Count.EqualTo(2));
    Assert.That(s.Zones[0].SortBy[0].Field, Is.EqualTo(DefragSortField.Size));
    Assert.That(s.Zones[0].SortBy[0].Direction, Is.EqualTo(SortDirection.Descending));
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static LayoutTemplate SimpleTemplate(string name) => new() {
    Name = name,
    MetadataZone = MetadataZone.Unchanged,
    LeftoverStrategyText = "fill_gaps",
    Zones = [],
  };
}
