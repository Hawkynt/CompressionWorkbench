using System.Security.Cryptography;
using FileFormat.Dms;

namespace Compression.Tests.Dms;

/// <summary>
/// xDMS (public domain; Debian/Ubuntu <c>xdms</c>) as the oracle, both ways: it must decode the
/// real Aminet samples to the bytes we decode, and it must accept and decode what our writer
/// produces. Skipped when absent unless <c>CWB_REQUIRE_XDMS=1</c> (the CI gate).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DmsExternalConformanceTests {
  /// <summary>
  /// SHA-256 of the disk image xdms 1.3 decodes from each checked-in sample; see
  /// <c>ReferenceVectors/README.md</c>. Pinned so the core tier checks the decoder without xdms.
  /// </summary>
  internal static readonly Dictionary<string, string> ExpectedSha256 = new() {
    ["Damage-head.dms"] = "56866ab5eadba69a2417c2dffb864251af64838ff261576d2fffea044bbba915",
    ["Genesis-head.dms"] = "da311e176b2cf4d0fddca60df0cd13f1cb0658d9711d38ed3ff49b5280b35df8",
  };

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-dmsx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireXdms() {
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("xdms"))
      return;
    const string hint = "xdms not installed: `sudo apt-get install -y xdms`.";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_XDMS") == "1")
      Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static (string StdOut, string StdErr, int ExitCode) Xdms(string arguments) {
    var r = FsInteropToolbox.RunWsl("xdms " + arguments);
    TestContext.Out.WriteLine($"xdms {arguments} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  [TestCase("Damage-head.dms")]
  [TestCase("Genesis-head.dms")]
  public void GivenARealSample_WhenXdmsAndWeDecodeIt_ThenTheImagesMatch(string name) {
    RequireXdms();
    var sample = Path.Combine(this._tmp, name);
    File.Copy(DmsTests.VectorPath(name), sample);
    var adf = Path.Combine(this._tmp, "theirs.adf");
    var r = Xdms($"-q u {FsInteropToolbox.WinToWsl(sample)} +{FsInteropToolbox.WinToWsl(adf).Trim('\'')}");
    Assert.That(File.Exists(adf), Is.True, "xdms produced no image");

    using var reader = new DmsReader(File.OpenRead(sample));
    var ours = reader.ExtractDisk();
    var theirs = File.ReadAllBytes(adf);
    TestContext.Out.WriteLine($"{name}: xdms sha256 {Convert.ToHexStringLower(SHA256.HashData(theirs))}");
    Assert.That(ours, Is.EqualTo(theirs));
  }

  [TestCase(0)]
  [TestCase(1)]
  public void GivenOurDms_WhenXdmsTestsAndDecodesIt_ThenAcceptedAndIdentical(int mode) {
    RequireXdms();
    var image = new byte[11264 * 4];
    new Random(mode + 5).NextBytes(image.AsSpan(0, 11264));
    image.AsSpan(11264 * 2, 3000).Fill(0x90);
    var ours = Path.Combine(this._tmp, "ours.dms");
    using (var fs = File.Create(ours))
    using (var w = new DmsWriter(fs, leaveOpen: true))
      w.WriteDisk(image, mode);

    var test = Xdms($"t {FsInteropToolbox.WinToWsl(ours)}");
    Assert.That(test.ExitCode, Is.EqualTo(0), "xdms t refused our archive");
    Assert.That(test.StdOut + test.StdErr, Does.Not.Contain("rror"));
    var adf = Path.Combine(this._tmp, "theirs.adf");
    Xdms($"-q u {FsInteropToolbox.WinToWsl(ours)} +{FsInteropToolbox.WinToWsl(adf).Trim('\'')}");
    Assert.That(File.ReadAllBytes(adf), Is.EqualTo(image));
  }
}
