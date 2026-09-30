#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Maintenance;

/// <summary>
/// A Rock Ridge + Joliet image made by the real mastering tool must survive the
/// maintenance verbs with its permissions, owners, times, symlink, hard link and
/// volume ID intact — or the verb must refuse and leave the image as it was.
/// </summary>
/// <remarks>
/// The wipe used to zero Rock Ridge continuation areas (every permission and
/// owner vanished and libisofs reported "Wrong or damaged Rock Ridge entry"), and
/// every defrag fell back to a rebuild with the volume ID "CDROM", no Rock Ridge,
/// no boot record and no empty folders.
/// </remarks>
[TestFixture]
[Category("ExternalFsInterop")]
[NonParallelizable]
public class IsoRealImageMaintenanceTests {

  private string _dir = null!;
  private string _reference = null!;
  private List<string> _before = null!;

  private static readonly RealImageLab.FsKind Iso =
    new("iso", "", 0, RealImageLab.Features.None, "", RequiredTool: null);

  [OneTimeSetUp]
  public void Build() {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("No Linux shell / WSL available.");
    if (!FsInteropToolbox.WslHasTool("guestfish")) Assert.Ignore("guestfish (libguestfs-tools) is not installed.");
    var tool = FsInteropToolbox.WslHasTool("xorriso") ? "xorriso -as mkisofs" :
      FsInteropToolbox.WslHasTool("genisoimage") ? "genisoimage" : null;
    if (tool == null) Assert.Ignore("Neither xorriso nor genisoimage is installed.");

    this._dir = Path.Combine(Path.GetTempPath(), "cwb_isoreal_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._dir);
    this._reference = Path.Combine(this._dir, "ref.iso");
    var tree = FsInteropToolbox.WinToWsl(Path.Combine(this._dir, "tree")).Trim('\'');
    var iso = FsInteropToolbox.WinToWsl(this._reference).Trim('\'');
    var r = FsInteropToolbox.RunWsl(
      $"mkdir -p '{tree}/docs/deep' '{tree}/empty' && printf 'hello top\\n' > '{tree}/top.txt' && " +
      $"printf 'readme\\n' > '{tree}/docs/readme.txt' && head -c 200000 /dev/urandom > '{tree}/docs/deep/nested.bin' && " +
      $"head -c 900000 /dev/urandom > '{tree}/big.bin' && ln -s docs/readme.txt '{tree}/link' && ln '{tree}/top.txt' '{tree}/hard' && " +
      $"chmod 0640 '{tree}/top.txt' && touch -h -d @{RealImageLab.SeedTime} '{tree}/top.txt' '{tree}/big.bin' '{tree}/docs/readme.txt' && " +
      $"{tool} -quiet -R -J -V {RealImageLab.Label} -o '{iso}' '{tree}'");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
    this._before = RealImageLab.Manifest(Iso, this._reference);
    Assert.That(this._before.Any(l => l.StartsWith("entry link|l|", StringComparison.Ordinal)), Is.True,
      "precondition: the kernel reads the Rock Ridge symlink");
  }

  [OneTimeTearDown]
  public void Clean() {
    try { if (this._dir != null) Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static IEnumerable<TestCaseData> Verbs() {
    yield return new TestCaseData("wipe").SetName("iso: wipe keeps Rock Ridge");
    yield return new TestCaseData("defrag_start").SetName("iso: pack at start keeps Rock Ridge");
    yield return new TestCaseData("defrag_end").SetName("iso: pack at end keeps Rock Ridge");
    yield return new TestCaseData("defrag_fill").SetName("iso: fill holes keeps Rock Ridge");
  }

  [TestCaseSource(nameof(Verbs)), CancelAfter(300_000)]
  public void Verb_KeepsEverything_OrRefusesUntouched(string verb) {
    var work = Path.Combine(this._dir, verb + ".iso");
    File.Copy(this._reference, work, true);
    var original = File.ReadAllBytes(work);
    var descriptor = new FileSystem.Iso.IsoFormatDescriptor();

    Exception? refusal = null;
    using (var stream = new FileStream(work, FileMode.Open, FileAccess.ReadWrite)) {
      try {
        switch (verb) {
          case "wipe": descriptor.WipeUnusedSpace(stream); break;
          case "defrag_start": descriptor.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtStart }); break;
          case "defrag_end": descriptor.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd }); break;
          case "defrag_fill": descriptor.Defragment(stream, new DefragOptions { Mode = DefragMode.FillHolesLazy }); break;
        }
      } catch (NotSupportedException ex) {
        refusal = ex;
      }
    }

    if (refusal != null) {
      Assert.That(File.ReadAllBytes(work), Is.EqualTo(original), $"{verb} refused but changed the image");
      return;
    }
    Assert.That(new FileInfo(work).Length, Is.EqualTo(original.LongLength), $"{verb} changed the image size");
    var after = RealImageLab.Manifest(Iso, work);
    var diff = RealImageLab.UnexpectedDifferences(this._before, after, []);
    Assert.That(diff, Is.Empty, $"{verb} changed:\n{string.Join("\n", diff)}");
  }
}
