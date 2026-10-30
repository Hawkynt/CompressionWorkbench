using Compression.Registry;
using FileFormat.DiskCopy42;

namespace Compression.Tests.DiskCopy42;

/// <summary>
/// MAME's <c>floptool</c> (Debian/Ubuntu <c>mame-tools</c>) as the oracle, both ways: it must decode
/// our Disk Copy 4.2 images to the same sectors, and the images it writes — with its own
/// checksums — must pass our checksum verification and yield its sectors. Skipped when
/// floptool is absent, unless <c>CWB_REQUIRE_FLOPTOOL=1</c> (the CI gate).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DiskCopy42ExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-dc42x-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  internal static void RequireFloptool() {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("floptool"))
      return;
    const string hint = "floptool not installed: `sudo apt-get install -y mame-tools`.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_FLOPTOOL") == "1")
      Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  internal static (string StdOut, string StdErr, int ExitCode) Floptool(string arguments) {
    var r = FsInteropToolbox.RunWsl("floptool " + arguments);
    TestContext.Out.WriteLine($"floptool {arguments} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  [TestCase(409600)]
  [TestCase(819200)]
  public void GivenOurImage_WhenFloptoolConvertsItToRawSectors_ThenTheSectorsMatch(int size) {
    RequireFloptool();
    var data = DiskCopy42Tests.Random(size, size + 1);
    var dataPath = Path.Combine(this._tmp, "data.bin");
    File.WriteAllBytes(dataPath, data);
    var ours = Path.Combine(this._tmp, "ours.dc42");
    using (var fs = File.Create(ours))
      new DiskCopy42FormatDescriptor().Create(fs, [new ArchiveInputInfo(dataPath, "disk.img", false)], new FormatCreateOptions());

    var id = Floptool($"identify {FsInteropToolbox.WinToWsl(ours)}");
    Assert.That(id.StdOut, Does.Contain("dc42"), "floptool does not recognise our header");

    var raw = Path.Combine(this._tmp, "raw.img");
    var r = Floptool($"flopconvert dc42 apple_gcr {FsInteropToolbox.WinToWsl(ours)} {FsInteropToolbox.WinToWsl(raw)}");
    Assert.That(r.ExitCode, Is.EqualTo(0));
    Assert.That(File.ReadAllBytes(raw), Is.EqualTo(data));
  }

  [TestCase(409600)]
  [TestCase(819200)]
  public void GivenAFloptoolImage_WhenWeReadIt_ThenChecksumsVerifyAndSectorsMatch(int size) {
    RequireFloptool();
    var data = DiskCopy42Tests.Random(size, size + 2);
    var raw = Path.Combine(this._tmp, "raw.img");
    File.WriteAllBytes(raw, data);
    var theirs = Path.Combine(this._tmp, "theirs.dc42");
    var r = Floptool($"flopconvert apple_gcr dc42 {FsInteropToolbox.WinToWsl(raw)} {FsInteropToolbox.WinToWsl(theirs)}");
    Assert.That(r.ExitCode, Is.EqualTo(0));

    using var fs = File.OpenRead(theirs);
    var image = DiskCopy42Image.Read(fs);
    Assert.That(image.DataChecksumValid, Is.True, "MAME's data checksum disagrees with ours");
    Assert.That(image.TagChecksumValid, Is.True, "MAME's tag checksum disagrees with ours");
    Assert.That(image.Data, Is.EqualTo(data));
  }
}
