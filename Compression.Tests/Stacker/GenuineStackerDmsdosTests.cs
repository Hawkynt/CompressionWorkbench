using System.Text;
using Compression.Registry.Cvf;
using Compression.Tests.Support;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Driver-proof gate: a STACVOL produced by <see cref="GenuineStackerWriter"/>
/// must be detected as a real Stacker volume, mounted, and read back byte-exact
/// by the independent third-party <c>dmsdos</c> driver — and our own reader must
/// read the same driver-certified image. Skips when dmsdos cannot be built.
/// </summary>
[TestFixture]
public class GenuineStackerDmsdosTests {

  private static (string Name, byte[] Data)[] SampleFiles() {
    var hello = Encoding.ASCII.GetBytes(
      string.Concat(Enumerable.Repeat("Genuine Stacker STACVOL verification line.\r\n", 50)));
    var small = Encoding.ASCII.GetBytes("README: short file.\r\n");
    var rnd = new byte[4096];
    new Random(20260619).NextBytes(rnd);
    return [("HELLO.TXT", hello), ("README.TXT", small), ("RANDOM.BIN", rnd)];
  }

  [Test, Category("DriverProof")]
  [TestCase(CvfLzMethod.Stored)]
  [TestCase(CvfLzMethod.Ds)]
  public void GenuineStacker_IsMountedAndReadByteExact_ByRealDmsdosDriver(CvfLzMethod method) {
    var build = DmsdosCache.EnsureTools();
    if (build is null)
      Assert.Ignore("dmsdos tools unavailable (need Linux + git + cmake + C compiler, or set CWB_DMSDOS_BUILD). Skipping driver-proof gate.");

    var files = SampleFiles();
    var writer = new GenuineStackerWriter { CompressionMethod = method, CompressionLevel = 2 };
    foreach (var (n, d) in files) writer.AddFile(n, d);
    var image = writer.Build();

    var cvfPath = Path.Combine(Path.GetTempPath(), $"cwb-stac-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(cvfPath, image);

      // 1. Detection.
      var (_, detOut) = DmsdosCache.RunTool(DmsdosCache.CvfTest(build!), $"\"{cvfPath}\" -v");
      var detected = Encoding.ASCII.GetString(detOut);
      Assert.That(detected, Does.Contain("stacker version 3 CVF"),
        $"dmsdos did not detect a genuine Stacker 3 CVF; got: {detected.Trim()}");

      // 2. Write proof: the real driver reads every file back byte-exact.
      foreach (var (name, expected) in files) {
        var (exit, raw) = DmsdosCache.RunTool(DmsdosCache.DcRead(build!), $"\"{cvfPath}\" /{name} raw");
        Assert.That(exit, Is.EqualTo(0), $"dcread failed for {name}");
        var payload = DmsdosCache.PayloadAfterDiagnostics(raw);
        Assert.That(payload.Length, Is.GreaterThanOrEqualTo(expected.Length),
          $"dcread returned too few bytes for {name}");
        Assert.That(payload.AsSpan(0, expected.Length).ToArray(), Is.EqualTo(expected),
          $"{name} did not read back byte-exact through the dmsdos driver");
      }

      // 3. Read proof: our reader reads the same driver-certified image.
      using var ours = new GenuineStackerReader(new MemoryStream(image));
      foreach (var (name, expected) in files) {
        var entry = ours.Entries.FirstOrDefault(e => e.Name == name);
        Assert.That(entry, Is.Not.Null, $"our reader missed {name}");
        Assert.That(ours.Extract(entry!), Is.EqualTo(expected),
          $"our reader did not read {name} byte-exact from the driver-certified CVF");
      }
    } finally {
      try { File.Delete(cvfPath); } catch { /* best effort */ }
    }
  }
}
