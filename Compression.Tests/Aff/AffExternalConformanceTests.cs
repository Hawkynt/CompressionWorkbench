using Compression.Registry;
using FileFormat.Aff;

namespace Compression.Tests.Aff;

/// <summary>
/// AFFLIB's own tools (<c>afflib-tools</c>) as the oracle, both ways: <c>affinfo</c> and
/// <c>affcat</c> must accept and reproduce what we write, and we must reproduce, hash-verified,
/// what <c>affconvert</c> writes (zlib and LZMA pages). Skipped when absent unless
/// <c>CWB_REQUIRE_AFFLIB=1</c> (the CI gate).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class AffExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-affx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireAfflib() {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("affcat") && FsInteropToolbox.WslHasTool("affconvert"))
      return;
    const string hint = "afflib-tools not installed: `sudo apt-get install -y afflib-tools`.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_AFFLIB") == "1")
      Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static (string StdOut, string StdErr, int ExitCode) Run(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  [TestCase(0, 65536)]
  [TestCase(300_000, 65536)]
  [TestCase(1 << 20, AffImage.DefaultPageSize)]
  public void GivenOurAff_WhenAffcatReadsIt_ThenTheImageIsRestored(int size, int pageSize) {
    RequireAfflib();
    var image = AffTests.SampleImage(size, size + 11);
    var ours = Path.Combine(this._tmp, "ours.aff");
    using (var fs = File.Create(ours))
      AffImage.Write(fs, image, pageSize: pageSize);
    var restored = Path.Combine(this._tmp, "restored.raw");

    var info = Run($"affinfo -a {FsInteropToolbox.WinToWsl(ours)}");
    Assert.That(info.ExitCode, Is.EqualTo(0), "affinfo refused our file");
    var cat = Run($"affcat {FsInteropToolbox.WinToWsl(ours)} > {FsInteropToolbox.WinToWsl(restored)}");
    Assert.That(cat.ExitCode, Is.EqualTo(0), "affcat refused our file");
    Assert.That(File.ReadAllBytes(restored), Is.EqualTo(image));
  }

  [Test]
  public void GivenOurAff_WhenAffverifyChecksIt_ThenTheHashesAgree() {
    RequireAfflib();
    if (!FsInteropToolbox.WslHasTool("affverify"))
      Assert.Ignore("affverify not installed.");
    var ours = Path.Combine(this._tmp, "ours.aff");
    using (var fs = File.Create(ours))
      AffImage.Write(fs, AffTests.SampleImage(200_000, 5), pageSize: 65536);
    var r = Run($"affverify {FsInteropToolbox.WinToWsl(ours)}");
    Assert.That(r.ExitCode, Is.EqualTo(0));
    Assert.That(r.StdOut + r.StdErr, Does.Not.Contain("FAIL").IgnoreCase.And.Not.Contain("BAD").IgnoreCase);
  }

  [TestCase("", 300_000)]
  [TestCase("-X9", 300_000)]
  [TestCase("-L", 300_000)]
  public void GivenAffconvertOutput_WhenWeReadIt_ThenTheImageIsRestoredAndVerified(string options, int size) {
    RequireAfflib();
    var image = AffTests.SampleImage(size, size + 7);
    var raw = Path.Combine(this._tmp, "source.raw");
    File.WriteAllBytes(raw, image);
    var theirs = Path.Combine(this._tmp, "theirs.aff");
    var r = Run($"affconvert {options} -o {FsInteropToolbox.WinToWsl(theirs)} {FsInteropToolbox.WinToWsl(raw)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), "affconvert failed");

    using var fs = File.OpenRead(theirs);
    Assert.That(AffImage.Read(fs).ReadVerifiedImage(), Is.EqualTo(image));
  }

  [Test]
  public void GivenAffconvertOutput_WhenWeReplaceTheImage_ThenAffcatReadsTheNewImage() {
    RequireAfflib();
    var raw = Path.Combine(this._tmp, "source.raw");
    File.WriteAllBytes(raw, AffTests.SampleImage(100_000, 1));
    var theirs = Path.Combine(this._tmp, "theirs.aff");
    Assert.That(Run($"affconvert -o {FsInteropToolbox.WinToWsl(theirs)} {FsInteropToolbox.WinToWsl(raw)}").ExitCode, Is.EqualTo(0));

    var replacement = AffTests.SampleImage(150_000, 2);
    var replacementPath = Path.Combine(this._tmp, "new.img");
    File.WriteAllBytes(replacementPath, replacement);
    using (var fs = File.Open(theirs, FileMode.Open, FileAccess.ReadWrite))
      new AffFormatDescriptor().Add(fs, [new ArchiveInputInfo(replacementPath, "disk.img", false)]);

    var restored = Path.Combine(this._tmp, "restored.raw");
    Assert.That(Run($"affcat {FsInteropToolbox.WinToWsl(theirs)} > {FsInteropToolbox.WinToWsl(restored)}").ExitCode, Is.EqualTo(0));
    Assert.That(File.ReadAllBytes(restored), Is.EqualTo(replacement));
  }
}
