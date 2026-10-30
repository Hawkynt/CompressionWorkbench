using FileFormat.UuEncoding;

namespace Compression.Tests.UuEncoding;

/// <summary>
/// Black-box oracle for XXencode: uudeview's <c>uuenview -x</c> encodes and
/// <c>uudeview</c> decodes. Both directions, so neither side can be wrong in step.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class XxEncodingUudeviewInteropTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_xx_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] Payload() {
    var data = new byte[1000];
    new Random(7).NextBytes(data);
    return data;
  }

  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL/Linux shell unavailable.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"'{tool}' not installed. Run: sudo apt install -y uudeview");
  }

  [Test]
  public void OurXxencode_IsDecodedByUudeview() {
    RequireTool("uudeview");
    var data = Payload();
    var encodedPath = Path.Combine(this._tmpDir, "ours.xxe");
    using (var fs = File.Create(encodedPath))
      XxEncoder.Encode(new MemoryStream(data), fs, "payload.bin");

    var outDir = Path.Combine(this._tmpDir, "out");
    Directory.CreateDirectory(outDir);
    var r = FsInteropToolbox.RunWsl(
      $"uudeview -i -o -q -p {FsInteropToolbox.WinToWsl(outDir)} {FsInteropToolbox.WinToWsl(encodedPath)}");
    var decoded = Path.Combine(outDir, "payload.bin");
    Assert.That(File.Exists(decoded), Is.True, $"uudeview produced nothing (exit {r.ExitCode}):\n{r.StdOut}\n{r.StdErr}");
    Assert.That(File.ReadAllBytes(decoded), Is.EqualTo(data));
  }

  [Test]
  public void UuenviewXxencode_IsDecodedByUs() {
    RequireTool("uuenview");
    var data = Payload();
    var inPath = Path.Combine(this._tmpDir, "payload.bin");
    var encodedPath = Path.Combine(this._tmpDir, "theirs.xxe");
    File.WriteAllBytes(inPath, data);
    var r = FsInteropToolbox.RunWsl(
      $"cd {FsInteropToolbox.WinToWsl(this._tmpDir)} && uuenview -x payload.bin > theirs.xxe");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"uuenview failed:\n{r.StdOut}\n{r.StdErr}");

    using var fs = File.OpenRead(encodedPath);
    var (name, _, decoded) = XxEncoder.Decode(fs);
    Assert.Multiple(() => {
      Assert.That(name, Is.EqualTo("payload.bin"));
      Assert.That(decoded, Is.EqualTo(data));
    });
  }
}
