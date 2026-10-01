using System;
using System.IO;
using System.Linq;
using System.Threading;
using Compression.Lib;
using Compression.NativeUI.Maintenance;
using Compression.Registry;
using NUnit.Framework;

namespace Compression.Tests.NativeUI;

/// <summary>
/// The Defragment tab's logic without the tab: what each real format is said to support, how typed
/// values are read, how the choices map onto the engine, and that a run keeps the defrag invariants
/// or refuses with the image untouched.
/// </summary>
[TestFixture]
public sealed class MaintenancePresenterTests {
  private string _root = null!;

  [SetUp]
  public void SetUp() {
    FormatRegistration.EnsureInitialized();
    this._root = MaintenanceFixtures.Scratch();
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._root, recursive: true); } catch { }
  }

  // ── capabilities ────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenFat_WhenCapabilitiesAreAsked_ThenDefragmentAndItsInPlaceModesAreOffered() {
    var caps = MaintenanceCapabilities.For("Fat");

    Assert.Multiple(() => {
      Assert.That(caps.Verb(MaintenanceVerb.Defragment).Supported, Is.True);
      Assert.That(caps.Verb(MaintenanceVerb.Shrink).Supported, Is.True);
      Assert.That(caps.Verb(MaintenanceVerb.WipeEmpty).Supported, Is.True);
      Assert.That(caps.Verb(MaintenanceVerb.Scramble).Supported, Is.True);
      Assert.That(caps.Verb(MaintenanceVerb.Optimize).Supported, Is.False, "a filesystem's layout operation is Defragment; Optimize would copy it through");
      foreach (var strategy in new[] { DefragStrategy.Consolidate, DefragStrategy.Defrag, DefragStrategy.Reorder, DefragStrategy.CarveHole })
        Assert.That(caps.Strategy(strategy).Supported, Is.True, strategy.ToString());
      Assert.That(caps.PackAtStart.Supported && caps.PackAtEnd.Supported, Is.True);
      Assert.That(caps.Interleave.Supported, Is.True, "FAT's planner honours block interleave");
      Assert.That(caps.MetadataZone.Supported, Is.True);
      Assert.That(caps.LayoutProfile.Supported, Is.False, "FAT refuses layout templates, so the profile is not offered");
    });
  }

  [Test]
  public void GivenAnyFormat_WhenSortEntriesIsAsked_ThenItIsRefusedWithAReason() {
    foreach (var format in new[] { "Fat", "Zip", "Ntfs", "ExFat" }) {
      var sort = MaintenanceCapabilities.For(format).Strategy(DefragStrategy.SortEntries);
      Assert.That(sort.Supported, Is.False, format);
      Assert.That(sort.Reason, Does.Contain("sorting directory entries"), format);
    }
  }

  [Test]
  public void GivenZip_WhenCapabilitiesAreAsked_ThenDefragmentIsNotOfferedButOptimizePurgeAndClearAre() {
    var caps = MaintenanceCapabilities.For("Zip");

    Assert.Multiple(() => {
      Assert.That(caps.Verb(MaintenanceVerb.Defragment).Supported, Is.False, "an archive has no free-space layout to defragment (#432)");
      Assert.That(caps.Verb(MaintenanceVerb.Optimize).Supported, Is.True);
      Assert.That(caps.OptimizeRoute, Is.EqualTo(OptimizeRoute.Reencode));
      Assert.That(caps.Verb(MaintenanceVerb.Purge).Supported, Is.True);
      Assert.That(caps.Verb(MaintenanceVerb.WipeEmpty).Supported, Is.True);
      Assert.That(caps.Verb(MaintenanceVerb.Scramble).Supported, Is.False);
      Assert.That(Enum.GetValues<DefragStrategy>().Any(s => caps.Strategy(s).Supported), Is.False);
    });
  }

  [Test]
  public void GivenAPartitionedDisk_WhenCapabilitiesAreAsked_ThenNothingIsOffered() {
    var caps = MaintenanceCapabilities.For("PartitionedDisk");

    Assert.That(caps.AnySupported, Is.False, "a partitioned disk is read-only; each volume is maintained on its own");
    Assert.That(caps.Verbs.Values.All(v => v.Reason.Length > 0), Is.True, "every refusal says why");
  }

  // ── typed values ────────────────────────────────────────────────────────────────────────────

  [TestCase("0", false)]
  [TestCase("1", true)]
  [TestCase("2", true)]
  [TestCase("256", true)]
  [TestCase("257", false)]
  [TestCase("-1", false)]
  [TestCase("abc", false)]
  [TestCase("1.5", false)]
  [TestCase("", false)]
  [TestCase(" 16 ", true)]
  public void GivenAnInterleave_WhenParsed_ThenOnlyOneTo256IsAccepted(string text, bool valid) {
    var parsed = MaintenanceInput.ParseInterleave(text);

    Assert.That(parsed.IsValid, Is.EqualTo(valid), parsed.Error);
    if (valid) Assert.That(parsed.Value, Is.EqualTo(int.Parse(text.Trim())));
  }

  [TestCase("524288", 524288L)]
  [TestCase("64k", 64L * 1024)]
  [TestCase("64m", 64L * 1024 * 1024)]
  [TestCase("1g", 1L << 30)]
  [TestCase("1G", 1L << 30)]
  public void GivenAHoleSize_WhenParsed_ThenSuffixesScale(string text, long expected)
    => Assert.That(MaintenanceInput.ParseSize(text).Value, Is.EqualTo(expected));

  [TestCase("0")]
  [TestCase("")]
  [TestCase("m")]
  [TestCase("-5")]
  [TestCase("ten")]
  [TestCase("99999999999999999g")]
  public void GivenAnUnusableHoleSize_WhenParsed_ThenItIsRefused(string text)
    => Assert.That(MaintenanceInput.ParseSize(text).IsValid, Is.False);

  [TestCase("auto", -1L, true)]
  [TestCase("", -1L, true)]
  [TestCase("4096", 4096L, true)]
  [TestCase("-4096", 0L, false)]
  [TestCase("start", 0L, false)]
  public void GivenAHoleOffset_WhenParsed_ThenAutoOrAnOffsetIsAccepted(string text, long expected, bool valid) {
    var parsed = MaintenanceInput.ParseHoleAt(text);
    Assert.That(parsed.IsValid, Is.EqualTo(valid));
    if (valid) Assert.That(parsed.Value, Is.EqualTo(expected));
  }

  // ── choices onto the engine ─────────────────────────────────────────────────────────────────

  [TestCase("Consolidate", false, DefragMode.ConsolidateAtStart)]
  [TestCase("Consolidate", true, DefragMode.ConsolidateAtEnd)]
  [TestCase("Defrag", false, DefragMode.FillHolesLazy)]
  [TestCase("Reorder", false, DefragMode.AscendingOrder)]
  [TestCase("CarveHole", false, DefragMode.CarveHole)]
  public void GivenAStrategy_WhenOptionsAreBuilt_ThenItMapsOntoItsEngineMode(string strategy, bool packAtEnd, DefragMode expected) {
    var presenter = new MaintenancePresenter(MaintenanceFixtures.FragmentedFat(this._root), "Fat") {
      Verb = MaintenanceVerb.Defragment, Strategy = Enum.Parse<DefragStrategy>(strategy), PackAtEnd = packAtEnd,
    };

    Assert.That(presenter.BuildDefragOptions().Mode, Is.EqualTo(expected));
  }

  [Test]
  public void GivenCarveHole_WhenOptionsAreBuilt_ThenTheTypedSizeAndOffsetReachTheEngine() {
    var presenter = new MaintenancePresenter(MaintenanceFixtures.FragmentedFat(this._root), "Fat") {
      Verb = MaintenanceVerb.Defragment, Strategy = DefragStrategy.CarveHole, HoleSizeText = "64k", HoleAtText = "8192", InterleaveText = "3",
    };

    var options = presenter.BuildDefragOptions();
    Assert.That((options.HoleSize, options.HoleAt, options.InterleaveStride), Is.EqualTo((64L * 1024, 8192L, 3)));
  }

  [Test]
  public void GivenAnOptionTheFormatRefuses_WhenOptionsAreBuilt_ThenItStaysAtItsDefault() {
    var presenter = new MaintenancePresenter(MaintenanceFixtures.FragmentedFat(this._root), "Fat") {
      Verb = MaintenanceVerb.Defragment, LayoutProfile = new() { Name = "ignored" },
    };

    Assert.That(presenter.BuildDefragOptions().LayoutTemplate, Is.Null, "a disabled option must not reach the engine to be refused or ignored");
  }

  [TestCase("0")]
  [TestCase("257")]
  [TestCase("x")]
  public void GivenAnInvalidInterleave_WhenDefragmentIsPicked_ThenStartIsBlockedWithTheReason(string stride) {
    var presenter = new MaintenancePresenter(MaintenanceFixtures.FragmentedFat(this._root), "Fat") { Verb = MaintenanceVerb.Defragment, InterleaveText = stride };

    Assert.That(presenter.StartBlocker, Does.Contain("Interleave"));
  }

  [Test]
  public void GivenAnInvalidInterleave_WhenAnotherOperationIsPicked_ThenItDoesNotBlockStart() {
    var presenter = new MaintenancePresenter(MaintenanceFixtures.FragmentedFat(this._root), "Fat") { Verb = MaintenanceVerb.WipeEmpty, InterleaveText = "0" };

    Assert.That(presenter.StartBlocker, Is.Null, "interleave only applies to Defragment");
  }

  // ── running ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public void GivenAFragmentedFat_WhenDefragmented_ThenSizeAndEveryFileAreUnchangedAndTheFilesAreContiguous() {
    var path = MaintenanceFixtures.FragmentedFat(this._root);
    var sizeBefore = new FileInfo(path).Length;
    var presenter = new MaintenancePresenter(path, "Fat") { Verb = MaintenanceVerb.Defragment, Strategy = DefragStrategy.Consolidate };
    Assume.That(presenter.Analyze().Status, Does.Contain("fragmented file"), "the fixture must start fragmented");

    var outcome = presenter.RunAsync(_ => { }, _ => { }, CancellationToken.None).Result;

    Assert.Multiple(() => {
      Assert.That(outcome.Kind, Is.EqualTo(MaintenanceOutcomeKind.Succeeded), outcome.Summary);
      Assert.That(new FileInfo(path).Length, Is.EqualTo(sizeBefore), "defragmenting never changes the image size");
      var after = MaintenanceFixtures.ReadFatFiles(path);
      foreach (var (name, data) in MaintenanceFixtures.FatFiles)
        Assert.That(after[name], Is.EqualTo(data), name);
      Assert.That(presenter.Analyze().Status, Does.Contain("no fragmentation"));
    });
  }

  [Test]
  public void GivenAHoleLargerThanTheVolume_WhenCarved_ThenTheRunIsRefusedAndTheImageIsByteIdentical() {
    var path = MaintenanceFixtures.FragmentedFat(this._root);
    var before = File.ReadAllBytes(path);
    var presenter = new MaintenancePresenter(path, "Fat") { Verb = MaintenanceVerb.Defragment, Strategy = DefragStrategy.CarveHole, HoleSizeText = "1g" };

    var outcome = presenter.RunAsync(_ => { }, _ => { }, CancellationToken.None).Result;

    Assert.Multiple(() => {
      Assert.That(outcome.Kind, Is.EqualTo(MaintenanceOutcomeKind.Refused), outcome.Summary);
      Assert.That(outcome.Mutated, Is.False);
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "a refusal leaves the image exactly as it was");
    });
  }

  [Test]
  public void GivenAnUnsupportedOperation_WhenRun_ThenItIsRefusedWithoutTouchingTheImage() {
    var path = MaintenanceFixtures.Zip(this._root);
    var before = File.ReadAllBytes(path);
    var presenter = new MaintenancePresenter(path, "Zip") { Verb = MaintenanceVerb.Defragment };

    var outcome = presenter.RunAsync(_ => { }, _ => { }, CancellationToken.None).Result;

    Assert.That((outcome.Kind, File.ReadAllBytes(path).SequenceEqual(before)), Is.EqualTo((MaintenanceOutcomeKind.Refused, true)));
  }
}
