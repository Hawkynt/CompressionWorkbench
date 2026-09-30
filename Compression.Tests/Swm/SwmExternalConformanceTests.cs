using FileFormat.Wim;

namespace Compression.Tests.Swm;

/// <summary>
/// Our split WIM sets against wimlib, the reference split-WIM implementation: <c>wimlib-imagex
/// verify</c> must accept the set and <c>wimlib-imagex apply</c> must reproduce every file.
/// Skips when <c>wimlib-imagex</c> is absent (<c>apt install wimtools</c>; CI's Linux leg installs it).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class SwmExternalConformanceTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_swmext_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static void RequireWimlib() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed; wimlib-imagex is checked on Linux.");
    if (!FsInteropToolbox.WslHasTool("wimlib-imagex"))
      Assert.Ignore("wimlib-imagex is not installed. Run (inside WSL on Windows): `sudo apt install -y wimtools`.");
  }

  [TestCase(WimConstants.CompressionNone)]
  [TestCase(WimConstants.CompressionXpress)]
  [TestCase(WimConstants.CompressionLzx)]
  [TestCase(WimConstants.CompressionLzms)]
  public void OurSplitSet_VerifiedAndAppliedByWimlib(uint compression) {
    RequireWimlib();
    var rng = new Random(0x5311);
    var files = new List<(string Name, byte[] Data)>();
    for (var i = 0; i < 6; ++i) {
      var data = new byte[40_000 + i * 3_000];
      rng.NextBytes(data.AsSpan(0, data.Length / 2)); // half random, half zeros: compresses a little
      files.Add((i % 2 == 0 ? $"f{i}.bin" : $"dir/f{i}.bin", data));
    }
    files.Add(("empty.txt", []));
    var volumes = WimWriter.CreateSplit(60_000, files, compression);
    Assert.That(volumes, Has.Length.GreaterThan(2));
    for (var i = 0; i < volumes.Length; ++i)
      File.WriteAllBytes(Path.Combine(this._dir, i == 0 ? "set.swm" : $"set{i + 1}.swm"), volumes[i]);

    var dir = FsInteropToolbox.WinToWsl(this._dir).Trim('\'');
    var verify = FsInteropToolbox.RunWsl($"cd '{dir}' && wimlib-imagex verify set.swm --ref='set*.swm' 2>&1");
    Assert.That(verify.ExitCode, Is.Zero, $"wimlib-imagex verify rejected our set:\n{verify.StdOut}\n{verify.StdErr}");

    var apply = FsInteropToolbox.RunWsl($"cd '{dir}' && wimlib-imagex apply set.swm 1 out --ref='set*.swm' 2>&1");
    Assert.That(apply.ExitCode, Is.Zero, $"wimlib-imagex apply failed:\n{apply.StdOut}\n{apply.StdErr}");
    foreach (var (name, data) in files)
      Assert.That(File.ReadAllBytes(Path.Combine(this._dir, "out", name.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(data), name);
  }
}
