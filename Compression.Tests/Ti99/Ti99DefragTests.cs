#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ti99;

namespace Compression.Tests.Ti99;

[TestFixture]
public class Ti99DefragTests {

  [Test]
  public void Descriptor_OffersDefragCapability() {
    Assert.That(new Ti99FormatDescriptor(), Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Defragment_SectorDump_PreservesAllFileContent() {
    var w = new Ti99Writer();
    w.AddFile("ALPHA", Encoding.ASCII.GetBytes(new string('A', 500)));
    w.AddFile("BETA", Encoding.ASCII.GetBytes(new string('B', 700)));
    w.AddFile("GAMMA", Encoding.ASCII.GetBytes(new string('G', 1100)));
    var image = w.BuildSectorDump();

    var d = new Ti99FormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    d.Defragment(ms);

    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(entries, Does.ContainKey("ALPHA"));
    Assert.That(entries, Does.ContainKey("BETA"));
    Assert.That(entries, Does.ContainKey("GAMMA"));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    var w = new Ti99Writer();
    var alpha = Encoding.ASCII.GetBytes(new string('A', 500));
    var beta = Encoding.ASCII.GetBytes(new string('B', 700));
    var gamma = Encoding.ASCII.GetBytes(new string('G', 1123)); // not sector-aligned
    var delta = Encoding.ASCII.GetBytes(new string('D', 256));
    w.AddFile("ALPHA", alpha);
    w.AddFile("BETA", beta);
    w.AddFile("GAMMA", gamma);
    w.AddFile("DELTA", delta);
    var image = w.BuildSectorDump();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var originalLength = ms.Length;

    var d = new Ti99FormatDescriptor();
    ms.Position = 0;
    var before = Snapshot(d, ms);

    ms.Position = 0;
    d.Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(ms.Length, Is.EqualTo(originalLength), "defrag must not grow or shrink the image");
    ms.Position = 0;
    var after = Snapshot(d, ms);
    Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
    foreach (var (name, bytes) in before)
      Assert.That(after[name], Is.EqualTo(bytes), $"file '{name}' bytes changed across defrag");
  }

  private static Dictionary<string, byte[]> Snapshot(Ti99FormatDescriptor d, MemoryStream ms) {
    ms.Position = 0;
    var dir = Path.Combine(Path.GetTempPath(), "ti99-defrag-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      ms.Position = 0;
      d.Extract(ms, dir, null, null);
      var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        map[Path.GetFileName(f)] = File.ReadAllBytes(f);
      return map;
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }
}
