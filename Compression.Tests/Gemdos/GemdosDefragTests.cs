#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Gemdos;

namespace Compression.Tests.Gemdos;

[TestFixture]
public class GemdosDefragTests {

  private static byte[] BuildImage() {
    var w = new GemdosWriter();
    w.AddFile("A.TXT", Encoding.ASCII.GetBytes(new string('A', 5000)));
    w.AddFile("B.TXT", Encoding.ASCII.GetBytes(new string('B', 2000)));
    w.AddFile("DIR/C.TXT", Encoding.ASCII.GetBytes(new string('C', 3000)));
    return w.Build();
  }

  [Test]
  public void Descriptor_OffersDefragCapability() {
    Assert.That(new GemdosFormatDescriptor(), Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Defragment_ConsolidatesAtStart_PreservesAllFileContent() {
    var d = new GemdosFormatDescriptor();
    var image = BuildImage();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    d.Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(entries, Does.ContainKey("A.TXT"));
    Assert.That(entries, Does.ContainKey("B.TXT"));
    Assert.That(entries, Does.ContainKey("DIR/C.TXT"));
    Assert.That(entries["A.TXT"].OriginalSize, Is.EqualTo(5000));
    Assert.That(entries["DIR/C.TXT"].OriginalSize, Is.EqualTo(3000));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    var w = new GemdosWriter();
    var alpha = Encoding.ASCII.GetBytes(new string('A', 5000));
    var beta = Encoding.ASCII.GetBytes(new string('B', 2000));
    var gamma = Encoding.ASCII.GetBytes(new string('G', 1234)); // not multiple of cluster
    var delta = Encoding.ASCII.GetBytes(new string('D', 700));
    w.AddFile("ALPHA.BIN", alpha);
    w.AddFile("BETA.BIN", beta);
    w.AddFile("DIR/GAMMA.BIN", gamma);
    w.AddFile("DIR/DELTA.BIN", delta);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var originalLength = ms.Length;

    var d = new GemdosFormatDescriptor();
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

  private static Dictionary<string, byte[]> Snapshot(GemdosFormatDescriptor d, MemoryStream ms) {
    ms.Position = 0;
    var dir = Path.Combine(Path.GetTempPath(), "gemdos-defrag-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      ms.Position = 0;
      d.Extract(ms, dir, null, null);
      var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
        map[rel] = File.ReadAllBytes(f);
      }
      return map;
    } finally {
      try { Directory.Delete(dir, true); } catch { }
    }
  }
}
