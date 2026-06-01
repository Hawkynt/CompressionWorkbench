#pragma warning disable CS1591
using Compression.Lib;

namespace Compression.Tests.Ui;

/// <summary>
/// Exercises the pure-logic predicate that backs <c>DeleteSelectedCommand.CanExecute</c>
/// in <c>Compression.UI.ViewModels.MainViewModel</c>. The predicate lives in
/// <see cref="DeleteCapability.Evaluate"/> so it's testable without spinning up WPF —
/// the view-model just dispatches based on the returned <see cref="DeleteMode"/>.
/// <para>
/// The three cases the explorer Delete menu must handle:
/// </para>
/// <list type="number">
///   <item>Real-FS browsing → Delete enabled, route through <see cref="File"/> /
///         <see cref="Directory"/>.</item>
///   <item>Modifiable archive → Delete enabled, route through
///         <see cref="ArchiveOperations.Remove(string, string[], CompressionOptions?)"/>
///         so the modifier path runs.</item>
///   <item>Read-only archive → Delete still enabled (so the user can be told why), but
///         the click surfaces an info MessageBox instead of mutating bytes.</item>
/// </list>
/// </summary>
[TestFixture]
public class DeleteCommandTests {

  [OneTimeSetUp]
  public void EnsureRegistry() {
    // The predicate consults the format registry to look up
    // IArchiveModifiable; tests need it primed exactly like the UI does.
    FormatRegistration.EnsureInitialized();
  }

  // ── Case 1: real filesystem ─────────────────────────────────────────

  [Test, Category("UI")]
  public void Evaluate_RealFsBrowsingWithSelection_ReturnsRealFs() {
    // The view-model only flips IsBrowsingOsFolder when the user navigated UP
    // out of an archive into OS-browser mode. The archivePath argument is
    // irrelevant in that case — even if a stale path lingers, the OS-browser
    // selection wins because the entry list contains real OS paths.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: true, archivePath: null, selectedCount: 1);
    Assert.That(mode, Is.EqualTo(DeleteMode.RealFs));
  }

  [Test, Category("UI")]
  public void Evaluate_RealFsBrowsingMultiSelect_ReturnsRealFs() {
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: true, archivePath: "", selectedCount: 17);
    Assert.That(mode, Is.EqualTo(DeleteMode.RealFs));
  }

  [Test, Category("UI")]
  public void Evaluate_RealFsBrowsingNoSelection_ReturnsNone() {
    // Selection-count gate fires first — the menu must be disabled even in OS-browser mode
    // when nothing is highlighted.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: true, archivePath: null, selectedCount: 0);
    Assert.That(mode, Is.EqualTo(DeleteMode.None));
  }

  // ── Case 2: modifiable archive (FAT implements IArchiveModifiable) ──

  [Test, Category("UI")]
  public void Evaluate_ModifiableArchiveWithSelection_ReturnsModifiableArchive() {
    // FatFormatDescriptor implements IArchiveModifiable. Detection is by extension
    // — the file doesn't need to exist on disk, the predicate only inspects the
    // descriptor capabilities.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "test.fat", selectedCount: 1);
    Assert.That(mode, Is.EqualTo(DeleteMode.ModifiableArchive));
  }

  [Test, Category("UI")]
  public void Evaluate_ModifiableArchiveMultiSelect_ReturnsModifiableArchive() {
    // Multi-selection collapses to a single Remove call inside the view-model;
    // the predicate still classifies the archive as modifiable.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "x.fat", selectedCount: 5);
    Assert.That(mode, Is.EqualTo(DeleteMode.ModifiableArchive));
  }

  [Test, Category("UI")]
  public void Evaluate_ZipArchiveWithSelection_ReturnsModifiableArchive() {
    // ZipFormatDescriptor also implements IArchiveModifiable (rebuild-on-modify
    // via ModifyRebuilder), so a .zip is treated as modifiable too.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "test.zip", selectedCount: 1);
    Assert.That(mode, Is.EqualTo(DeleteMode.ModifiableArchive));
  }

  // ── Case 3: read-only archive (Arrow does NOT implement IArchiveModifiable) ──

  [Test, Category("UI")]
  public void Evaluate_ReadOnlyArchiveWithSelection_ReturnsReadOnlyArchive() {
    // ArrowFormatDescriptor implements IArchiveFormatOperations but NOT
    // IArchiveModifiable — its container is FlatBuffers messages with no
    // in-place mutation primitive. The Delete menu must stay enabled so the
    // click surfaces the "format is read-only" hint, but the destructive op
    // never runs.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "test.arrow", selectedCount: 1);
    Assert.That(mode, Is.EqualTo(DeleteMode.ReadOnlyArchive));
  }

  [Test, Category("UI")]
  public void Evaluate_ReadOnlyArchiveMultiSelect_ReturnsReadOnlyArchive() {
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "data.feather", selectedCount: 3);
    Assert.That(mode, Is.EqualTo(DeleteMode.ReadOnlyArchive));
  }

  // ── Edge cases ──────────────────────────────────────────────────────

  [Test, Category("UI")]
  public void Evaluate_NoArchiveNoBrowsingNoSelection_ReturnsNone() {
    // Cold-start state: no archive open, not in OS-browser mode, nothing selected.
    // The Delete menu must be disabled.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: null, selectedCount: 0);
    Assert.That(mode, Is.EqualTo(DeleteMode.None));
  }

  [Test, Category("UI")]
  public void Evaluate_NoArchivePathWithSelection_ReturnsNone() {
    // Selection without archive context or OS-browser mode is nonsense — the
    // EntryList shouldn't render anything to select. Guard returns None.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "", selectedCount: 2);
    Assert.That(mode, Is.EqualTo(DeleteMode.None));
  }

  [Test, Category("UI")]
  public void Evaluate_UnknownExtensionWithSelection_ReturnsNone() {
    // FormatDetector.DetectByExtension returns Unknown for unregistered
    // extensions; the predicate refuses to enable Delete on a container it
    // can't classify.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "weird.qqxyz", selectedCount: 1);
    Assert.That(mode, Is.EqualTo(DeleteMode.None));
  }

  [Test, Category("UI")]
  public void Evaluate_StreamFormatWithSelection_ReturnsReadOnlyArchive() {
    // .gz is a single-stream format with one logical entry; the modifier
    // interface doesn't apply. The menu surfaces the "read-only" hint
    // rather than enabling a destructive op that would corrupt the stream.
    var mode = DeleteCapability.Evaluate(isBrowsingOsFolder: false, archivePath: "data.gz", selectedCount: 1);
    Assert.That(mode, Is.EqualTo(DeleteMode.ReadOnlyArchive));
  }
}
