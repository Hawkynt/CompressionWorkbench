using System.Text;
using Compression.Registry;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Add / remove / defragment / purge on the genuine Stacker layout (rebuild
/// path): operations keep a genuine STACVOL and preserve surviving files.
/// </summary>
[TestFixture]
public class StackerGenuineMaintenanceTests {

  private static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

  private static readonly (string Name, byte[] Data)[] Seed = [
    ("HELLO.TXT", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("stac line\r\n", 70)))),
    ("RAND.BIN", Rnd(7000, 2)),
  ];

  private static byte[] MakeGenuine() {
    var w = new GenuineStackerWriter { VolumeLabel = "STAC", CompressionMethod = Compression.Registry.Cvf.CvfLzMethod.Stored };
    foreach (var (n, d) in Seed) w.AddFile(n, d);
    return w.Build();
  }

  private static Dictionary<string, byte[]> ReadAll(StackerFormatDescriptor d, byte[] img) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb-stacmaint-{Guid.NewGuid():N}");
    var map = new Dictionary<string, byte[]>();
    try {
      d.Extract(new MemoryStream(img), dir, null, null);
      foreach (var f in Directory.GetFiles(dir)) map[Path.GetFileName(f)] = File.ReadAllBytes(f);
    } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    return map;
  }

  [Test]
  public void Defragment_PreservesGenuineStackerContent() {
    var d = new StackerFormatDescriptor();
    using var ms = new MemoryStream(); ms.Write(MakeGenuine()); ms.Position = 0;
    d.Defragment(ms);
    var img = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(img, 0, 7), Is.EqualTo("STACKER"), "stays a STACVOL");
    var got = ReadAll(d, img);
    foreach (var (n, data) in Seed) Assert.That(got[n], Is.EqualTo(data), $"{n} survived defrag");
  }

  [Test]
  public void Shrink_KeepsGenuineLayout_AndPreservesContent() {
    var d = new StackerFormatDescriptor();
    var img = MakeGenuine();
    using var input = new MemoryStream(img);
    using var output = new MemoryStream();

    d.Shrink(input, output);

    var shrunk = output.ToArray();
    Assert.That(shrunk.Length, Is.GreaterThan(0).And.LessThanOrEqualTo(img.Length),
      "shrink must never grow the image");
    // Must stay a *genuine* STACVOL (not silently converted to the
    // CompressionWorkbench-only Extended layout).
    using var gr = new GenuineStackerReader(new MemoryStream(shrunk));
    Assert.That(gr.Entries.Count(e => !e.IsDirectory), Is.EqualTo(Seed.Length),
      "shrunk image still parses as a genuine STACVOL with the full file set");
    var got = ReadAll(d, shrunk);
    foreach (var (n, data) in Seed) Assert.That(got[n], Is.EqualTo(data), $"{n} survived shrink");
  }

  [Test]
  public void Shrink_OnExtendedLayout_IsNonLossy() {
    var d = new StackerFormatDescriptor();
    using var created = new MemoryStream();
    d.Create(created,
      [.. Seed.Select(s => ArchiveInputInfo.InMemory(s.Name, s.Data))],
      new FormatCreateOptions());
    var img = created.ToArray();

    using var input = new MemoryStream(img);
    using var output = new MemoryStream();
    d.Shrink(input, output);

    Assert.That(output.Length, Is.GreaterThan(0).And.LessThanOrEqualTo(img.Length),
      "shrink must never grow the image");
    var got = ReadAll(d, output.ToArray());
    foreach (var (n, data) in Seed) Assert.That(got[n], Is.EqualTo(data), $"{n} survived shrink");
  }

  [Test]
  public void Add_Then_Remove_RoundTrips() {
    var d = new StackerFormatDescriptor();
    using var ms = new MemoryStream(); ms.Write(MakeGenuine()); ms.Position = 0;

    var extra = Rnd(2500, 8);
    d.Add(ms, [ArchiveInputInfo.InMemory("EXTRA.BIN", extra)]);
    Assert.That(ReadAll(d, ms.ToArray()).GetValueOrDefault("EXTRA.BIN"), Is.EqualTo(extra));

    d.Remove(ms, ["RAND.BIN"]);
    var after = ReadAll(d, ms.ToArray());
    Assert.That(after.ContainsKey("RAND.BIN"), Is.False);
    Assert.That(after["HELLO.TXT"], Is.EqualTo(Seed[0].Data));
  }
}
