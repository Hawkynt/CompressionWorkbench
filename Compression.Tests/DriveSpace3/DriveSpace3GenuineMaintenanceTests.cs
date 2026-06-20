using System.Text;
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

/// <summary>
/// Add / remove / defragment / purge on the genuine DriveSpace 3 layout (rebuild
/// path). Each operation must keep the image a genuine, driver-readable CVF and
/// preserve the surviving files byte-exact.
/// </summary>
[TestFixture]
public class DriveSpace3GenuineMaintenanceTests {

  private static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

  private static readonly (string Name, byte[] Data)[] Seed = [
    ("HELLO.TXT", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compressible line\r\n", 60)))),
    ("RAND.BIN", Rnd(9000, 1)),
    ("MID.TXT", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("more text\r\n", 80)))),
  ];

  private static byte[] MakeGenuine() {
    var w = new GenuineDvr3Writer { VolumeLabel = "DRV3", CompressionMethod = Compression.Registry.Cvf.CvfLzMethod.Stored };
    foreach (var (n, d) in Seed) w.AddFile(n, d);
    return w.Build();
  }

  private static Dictionary<string, byte[]> ReadAll(DriveSpace3FormatDescriptor d, byte[] img) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb-dvr3maint-{Guid.NewGuid():N}");
    var map = new Dictionary<string, byte[]>();
    try {
      d.Extract(new MemoryStream(img), dir, null, null);
      foreach (var f in Directory.GetFiles(dir)) map[Path.GetFileName(f)] = File.ReadAllBytes(f);
    } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    return map;
  }

  [Test]
  public void Defragment_ShrinksAndPreservesGenuineContent() {
    var d = new DriveSpace3FormatDescriptor();
    using var ms = new MemoryStream(); ms.Write(MakeGenuine()); ms.Position = 0;
    var before = ms.Length;

    d.Defragment(ms);                                   // rebuild with Auto compression

    var img = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(img, 3, 8), Is.EqualTo("MSDBL6.0"), "stays genuine");
    Assert.That(img.Length, Is.LessThan(before), "compression shrinks the stored seed image");
    var got = ReadAll(d, img);
    foreach (var (n, data) in Seed) Assert.That(got[n], Is.EqualTo(data), $"{n} survived defrag");
  }

  [Test]
  public void Add_Then_Remove_RoundTrips() {
    var d = new DriveSpace3FormatDescriptor();
    using var ms = new MemoryStream(); ms.Write(MakeGenuine()); ms.Position = 0;

    var extra = Rnd(3000, 9);
    d.Add(ms, [ArchiveInputInfo.InMemory("EXTRA.BIN", extra)]);
    Assert.That(ReadAll(d, ms.ToArray()).GetValueOrDefault("EXTRA.BIN"), Is.EqualTo(extra), "added file readable");

    d.Remove(ms, ["RAND.BIN"]);
    var after = ReadAll(d, ms.ToArray());
    Assert.That(after.ContainsKey("RAND.BIN"), Is.False, "removed file gone");
    Assert.That(after["HELLO.TXT"], Is.EqualTo(Seed[0].Data), "other files intact");
    Assert.That(after["EXTRA.BIN"], Is.EqualTo(extra));
  }

  [Test, Category("DriverProof")]
  public void RebuiltImage_StillReadByRealDmsdosDriver() {
    var build = DmsdosCache.EnsureTools();
    if (build is null) Assert.Ignore("dmsdos unavailable.");

    var d = new DriveSpace3FormatDescriptor();
    using var ms = new MemoryStream(); ms.Write(MakeGenuine()); ms.Position = 0;
    d.Defragment(ms);
    var img = ms.ToArray();

    var cvf = Path.Combine(Path.GetTempPath(), $"cwb-dvr3reb-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(cvf, img);
      var (_, det) = DmsdosCache.RunTool(DmsdosCache.CvfTest(build!), $"\"{cvf}\" -v");
      Assert.That(Encoding.ASCII.GetString(det), Does.Contain("drivespace 3 CVF"));
      foreach (var (n, data) in Seed) {
        var (exit, raw) = DmsdosCache.RunTool(DmsdosCache.DcRead(build!), $"\"{cvf}\" /{n} raw");
        Assert.That(exit, Is.EqualTo(0), $"dcread {n}");
        var payload = DmsdosCache.PayloadAfterDiagnostics(raw);
        Assert.That(payload.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data), $"{n} byte-exact after rebuild");
      }
    } finally { try { File.Delete(cvf); } catch { } }
  }
}
