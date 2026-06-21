using System.Text;
using Compression.Registry.Cvf;
using Compression.Tests.Support;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Large genuine DoubleSpace/DriveSpace v2 volumes — well past the old fixed
/// ~69-cluster (552 KB) limit — must round-trip through our reader and stay
/// driver-mountable, with inner FAT12 + MDFAT geometry sized to the cluster count.
/// </summary>
[TestFixture]
public class DoubleSpaceHugeVolumeTests {

  private static (string Name, byte[] Data)[] BigSet(int files, int bytesEach, int seed) {
    var rnd = new Random(seed);
    var set = new (string, byte[])[files];
    for (var i = 0; i < files; i++) {
      var d = new byte[bytesEach];
      rnd.NextBytes(d.AsSpan(0, bytesEach / 4));   // semi-compressible
      set[i] = ($"F{i:D4}.BIN", d);
    }
    return set;
  }

  [Test, Category("Huge")]
  public void Large_Volume_RoundTrips_ViaReader() {
    // 300 files × 24 KB = ~900 clusters (8 KB) — well over the old 69-cluster cap.
    var files = BigSet(300, 24 * 1024, 1);
    var w = new GenuineCvfWriter { CompressionMethod = CvfLzMethod.Auto, CompressionLevel = 1 };
    foreach (var (n, d) in files) w.AddFile(n, d);
    var image = w.Build();

    using var r = new GenuineCvfReader(new MemoryStream(image));
    Assert.That(r.Entries, Has.Count.EqualTo(files.Length));
    foreach (var (name, data) in files)
      Assert.That(r.Extract(r.Entries.First(e => e.Name == name)), Is.EqualTo(data), $"{name} round-trip");
  }

  [Test, Category("DriverProof")]
  public void Large_Volume_IsReadByRealDmsdosDriver() {
    var build = DmsdosCache.EnsureTools();
    if (build is null) Assert.Ignore("dmsdos unavailable.");

    var files = BigSet(150, 16 * 1024, 2);   // ~300 clusters
    var w = new GenuineCvfWriter { CompressionMethod = CvfLzMethod.Auto, CompressionLevel = 1 };
    foreach (var (n, d) in files) w.AddFile(n, d);
    var image = w.Build();

    var cvf = Path.Combine(Path.GetTempPath(), $"cwb-dblhuge-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(cvf, image);
      var (_, det) = DmsdosCache.RunTool(DmsdosCache.CvfTest(build!), $"\"{cvf}\" -v");
      Assert.That(Encoding.ASCII.GetString(det), Does.Contain("drivespace CVF (version 2)"));
      foreach (var idx in new[] { 0, 50, 100, 149 }) {
        var (name, data) = files[idx];
        var (exit, raw) = DmsdosCache.RunTool(DmsdosCache.DcRead(build!), $"\"{cvf}\" /{name} raw");
        Assert.That(exit, Is.EqualTo(0), $"dcread {name}");
        var payload = DmsdosCache.PayloadAfterDiagnostics(raw);
        Assert.That(payload.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data), $"{name} byte-exact");
      }
    } finally { try { File.Delete(cvf); } catch { } }
  }
}
