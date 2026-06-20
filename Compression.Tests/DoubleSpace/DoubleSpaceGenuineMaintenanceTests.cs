using System.Text;
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Add / remove / defragment / purge on the genuine DoubleSpace/DriveSpace v2
/// layout (rebuild path): each op keeps a genuine, driver-readable CVF and
/// preserves surviving files byte-exact.
/// </summary>
[TestFixture]
public class DoubleSpaceGenuineMaintenanceTests {

  private static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

  private static readonly (string Name, byte[] Data)[] Seed = [
    ("HELLO.TXT", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compressible v2 line\r\n", 50)))),
    ("RAND.BIN", Rnd(6000, 3)),
  ];

  private static byte[] MakeGenuine() {
    var w = new GenuineCvfWriter { VolumeLabel = "DBL", CompressionMethod = Compression.Registry.Cvf.CvfLzMethod.Stored };
    foreach (var (n, d) in Seed) w.AddFile(n, d);
    return w.Build();
  }

  private static Dictionary<string, byte[]> ReadAll(DoubleSpaceFormatDescriptor d, byte[] img) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb-dblmaint-{Guid.NewGuid():N}");
    var map = new Dictionary<string, byte[]>();
    try {
      d.Extract(new MemoryStream(img), dir, null, null);
      foreach (var f in Directory.GetFiles(dir)) map[Path.GetFileName(f)] = File.ReadAllBytes(f);
    } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    return map;
  }

  [Test]
  public void Defragment_PreservesGenuineContent() {
    var d = new DoubleSpaceFormatDescriptor();
    using var ms = new MemoryStream(); ms.Write(MakeGenuine()); ms.Position = 0;
    d.Defragment(ms);
    var img = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(img, 3, 8), Is.EqualTo("MSDBL6.0"), "stays genuine v2");
    var got = ReadAll(d, img);
    foreach (var (n, data) in Seed) Assert.That(got[n], Is.EqualTo(data), $"{n} survived defrag");
  }

  [Test]
  public void Add_Then_Remove_RoundTrips() {
    var d = new DoubleSpaceFormatDescriptor();
    using var ms = new MemoryStream(); ms.Write(MakeGenuine()); ms.Position = 0;

    var extra = Rnd(2000, 5);
    d.Add(ms, [ArchiveInputInfo.InMemory("EXTRA.BIN", extra)]);
    Assert.That(ReadAll(d, ms.ToArray()).GetValueOrDefault("EXTRA.BIN"), Is.EqualTo(extra));

    d.Remove(ms, ["RAND.BIN"]);
    var after = ReadAll(d, ms.ToArray());
    Assert.That(after.ContainsKey("RAND.BIN"), Is.False);
    Assert.That(after["HELLO.TXT"], Is.EqualTo(Seed[0].Data));
  }
}
