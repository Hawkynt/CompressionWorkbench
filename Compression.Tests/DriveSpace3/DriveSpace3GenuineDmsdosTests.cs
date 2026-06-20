using System.Text;
using Compression.Registry.Cvf;
using Compression.Tests.Support;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Driver-proof gate: a CVF produced by <see cref="GenuineDvr3Writer"/> must be
/// detected as a real DriveSpace 3 volume, mounted, and read back byte-exact by
/// the independent third-party <c>dmsdos</c> driver — not by our own reader.
/// Skips (Assert.Ignore) when dmsdos cannot be built on the host.
/// </summary>
[TestFixture]
public class DriveSpace3GenuineDmsdosTests {

  private static (string Name, byte[] Data)[] SampleFiles() {
    var hello = Encoding.ASCII.GetBytes(
      string.Concat(Enumerable.Repeat("Genuine DriveSpace 3 CVF verification line.\r\n", 50)));
    var small = Encoding.ASCII.GetBytes("README: short file.\r\n");
    var rnd = new byte[4096];
    new Random(20260618).NextBytes(rnd);
    return [("HELLO.TXT", hello), ("README.TXT", small), ("RANDOM.BIN", rnd)];
  }

  [Test, Category("DriverProof")]
  [TestCase(CvfLzMethod.Stored)]
  [TestCase(CvfLzMethod.Ds)]
  [TestCase(CvfLzMethod.Jm)]
  [TestCase(CvfLzMethod.Auto)]
  public void GenuineDvr3_IsMountedAndReadByteExact_ByRealDmsdosDriver(CvfLzMethod method) {
    var build = DmsdosCache.EnsureTools();
    if (build is null)
      Assert.Ignore("dmsdos tools unavailable (need Linux + git + cmake + C compiler, or set CWB_DMSDOS_BUILD). Skipping driver-proof gate.");

    var files = SampleFiles();
    var writer = new GenuineDvr3Writer { CompressionMethod = method, CompressionLevel = 2 };
    foreach (var (n, d) in files) writer.AddFile(n, d);
    var image = writer.Build();

    var cvfPath = Path.Combine(Path.GetTempPath(), $"cwb-dvr3-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(cvfPath, image);

      // 1. Detection: the real driver must recognise it as DriveSpace 3.
      var (_, detOut) = DmsdosCache.RunTool(DmsdosCache.CvfTest(build!), $"\"{cvfPath}\" -v");
      var detected = Encoding.ASCII.GetString(detOut);
      Assert.That(detected, Does.Contain("drivespace 3 CVF"),
        $"dmsdos did not detect a genuine DriveSpace 3 CVF; got: {detected.Trim()}");

      // 2. Read-back (write proof): every file must decompress byte-exact
      //    through the real driver — i.e. our writer's output is genuine.
      foreach (var (name, expected) in files) {
        var (exit, raw) = DmsdosCache.RunTool(DmsdosCache.DcRead(build!), $"\"{cvfPath}\" /{name} raw");
        Assert.That(exit, Is.EqualTo(0), $"dcread failed for {name}");
        var payload = DmsdosCache.PayloadAfterDiagnostics(raw);
        Assert.That(payload.Length, Is.GreaterThanOrEqualTo(expected.Length),
          $"dcread returned too few bytes for {name}");
        Assert.That(payload.AsSpan(0, expected.Length).ToArray(), Is.EqualTo(expected),
          $"{name} did not read back byte-exact through the dmsdos driver");
      }

      // 3. Read proof: our own reader must read the SAME image — now certified
      //    genuine by the independent driver — byte-exact. Writer + reader thus
      //    form a full r/w path over the driver-verified genuine format.
      using var ours = new GenuineDvr3Reader(new MemoryStream(image));
      foreach (var (name, expected) in files) {
        var entry = ours.Entries.FirstOrDefault(e => e.Name == name);
        Assert.That(entry, Is.Not.Null, $"our reader missed {name} in the genuine CVF");
        Assert.That(ours.Extract(entry!), Is.EqualTo(expected),
          $"our reader did not read {name} byte-exact from the driver-certified CVF");
      }
    } finally {
      try { File.Delete(cvfPath); } catch { /* best effort */ }
    }
  }

}
