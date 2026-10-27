using Compression.Core.Entropy.UnixPack;

namespace Compression.Tests.UnixPack;

/// <summary>GNU gzip still decodes System V pack files (<c>gzip -d</c> recognises magic 1F 1E).</summary>
[TestFixture]
[Category("ExternalInterop")]
public class UnixPackGzipInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_pack_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  public static IEnumerable<TestCaseData> Payloads() => UnixPackTests.Payloads();

  [TestCaseSource(nameof(Payloads))]
  public void OurPack_IsDecodedByGzip(byte[] data) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("gzip"))
      Assert.Ignore("gzip not available.");
    var packed = Path.Combine(this._tmpDir, "in.z");
    var plain = Path.Combine(this._tmpDir, "out.bin");
    File.WriteAllBytes(packed, UnixPackBuildingBlock.Encode(data));
    var r = FsInteropToolbox.RunWsl($"gzip -dc < {FsInteropToolbox.WinToWsl(packed)} > {FsInteropToolbox.WinToWsl(plain)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"gzip rejected our pack file:\n{r.StdErr}");
    Assert.That(File.ReadAllBytes(plain), Is.EqualTo(data));
  }

  [Test]
  public void HandAssembledVector_IsDecodedByGzipAsWell() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("gzip"))
      Assert.Ignore("gzip not available.");
    var packed = Path.Combine(this._tmpDir, "aab.z");
    File.WriteAllBytes(packed, [0x1F, 0x1E, 0, 0, 0, 3, 2, 1, 0, (byte)'a', (byte)'b', 0xC4]);
    var r = FsInteropToolbox.RunWsl($"gzip -dc < {FsInteropToolbox.WinToWsl(packed)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdErr);
    Assert.That(r.StdOut, Is.EqualTo("aab"));
  }
}
