using FileFormat.Ecm;

namespace Compression.Tests.Ecm;

/// <summary>
/// Neill Corlett's reference ECM tool as the oracle, both ways: <c>ecm2bin</c> must restore
/// what we encode byte for byte, and we must restore what <c>bin2ecm</c> encodes. The tool is
/// built from alucryd/ecm-tools (see docs/ECM-ON-DISK.md for the pinned revision); it installs
/// as <c>bin2ecm</c> plus an <c>ecm2bin</c> link, and chooses its direction from argv[0].
/// Skipped when absent, unless <c>CWB_REQUIRE_ECM=1</c> makes that a failure (the CI gate).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class EcmExternalConformanceTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-ecm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static void RequireEcmTools() {
    var required = Environment.GetEnvironmentVariable("CWB_REQUIRE_ECM") == "1";
    var present = FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool("bin2ecm") && FsInteropToolbox.WslHasTool("ecm2bin");
    if (present)
      return;
    const string hint = "bin2ecm/ecm2bin not installed: build https://github.com/alucryd/ecm-tools (gcc -O2 -o bin2ecm ecm.c; ln -s bin2ecm ecm2bin) onto the PATH.";
    if (required)
      Assert.Fail(hint);
    Assert.Ignore(hint);
  }

  private static (string StdOut, string StdErr, int ExitCode) Tool(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  public static IEnumerable<TestCaseData> Images() {
    yield return new TestCaseData(EcmFixtures.MixedImage()).SetArgDisplayNames("mixed");
    var random = new Random(3);
    using var ms = new MemoryStream();
    for (var i = 0; i < 40; ++i) ms.Write(i % 3 == 0 ? EcmFixtures.Mode2Form2(i, random) : EcmFixtures.Mode2Form1(i, random));
    yield return new TestCaseData(ms.ToArray()).SetArgDisplayNames("mode2");
    var noise = new byte[10_000];
    random.NextBytes(noise);
    yield return new TestCaseData(noise).SetArgDisplayNames("noise");
  }

  [TestCaseSource(nameof(Images))]
  public void GivenOurEcm_WhenEcm2binDecodesIt_ThenTheImageIsRestored(byte[] image) {
    RequireEcmTools();
    var ecmPath = Path.Combine(this._tmp, "ours.bin.ecm");
    using (var fs = File.Create(ecmPath))
      EcmCodec.Encode(new MemoryStream(image), fs);
    var outPath = Path.Combine(this._tmp, "restored.bin");

    var r = Tool($"ecm2bin {FsInteropToolbox.WinToWsl(ecmPath)} {FsInteropToolbox.WinToWsl(outPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), "ecm2bin refused our stream");
    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(image));
  }

  [TestCaseSource(nameof(Images))]
  public void GivenBin2ecmOutput_WhenWeDecodeIt_ThenTheImageIsRestored(byte[] image) {
    RequireEcmTools();
    var binPath = Path.Combine(this._tmp, "source.bin");
    File.WriteAllBytes(binPath, image);
    var ecmPath = Path.Combine(this._tmp, "theirs.bin.ecm");

    var r = Tool($"bin2ecm {FsInteropToolbox.WinToWsl(binPath)} {FsInteropToolbox.WinToWsl(ecmPath)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), "bin2ecm failed");
    using var output = new MemoryStream();
    using (var fs = File.OpenRead(ecmPath))
      EcmCodec.Decode(fs, output);
    Assert.That(output.ToArray(), Is.EqualTo(image));
  }
}
