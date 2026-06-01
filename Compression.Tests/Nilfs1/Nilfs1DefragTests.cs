#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Nilfs1;

namespace Compression.Tests.Nilfs1;

[TestFixture]
public class Nilfs1DefragTests {

  private static byte[] BuildImage() {
    var w = new Nilfs1Writer();
    w.AddFile("a.txt", Encoding.UTF8.GetBytes(new string('A', 5000)));
    w.AddFile("b.txt", Encoding.UTF8.GetBytes(new string('B', 2000)));
    w.AddFile("sub/c.txt", Encoding.UTF8.GetBytes(new string('C', 3000)));
    return w.Build();
  }

  [Test]
  public void Descriptor_OffersDefragCapability() {
    Assert.That(new Nilfs1FormatDescriptor(), Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Defragment_PreservesAllFileContent() {
    var d = new Nilfs1FormatDescriptor();
    var image = BuildImage();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    d.Defragment(ms);

    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(entries, Does.ContainKey("a.txt"));
    Assert.That(entries, Does.ContainKey("b.txt"));
    Assert.That(entries, Does.ContainKey("sub/c.txt"));
    Assert.That(entries["a.txt"].OriginalSize, Is.EqualTo(5000));
    Assert.That(entries["sub/c.txt"].OriginalSize, Is.EqualTo(3000));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    var w = new Nilfs1Writer();
    var alpha = Encoding.UTF8.GetBytes(new string('A', 5000));
    var beta = Encoding.UTF8.GetBytes(new string('B', 2000));
    var gamma = Encoding.UTF8.GetBytes(new string('G', 4321)); // not block-aligned
    var delta = Encoding.UTF8.GetBytes(new string('D', 700));
    w.AddFile("alpha.bin", alpha);
    w.AddFile("beta.bin", beta);
    w.AddFile("dir/gamma.bin", gamma);
    w.AddFile("dir/sub/delta.bin", delta);
    var img = w.Build();

    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);
    var originalLength = ms.Length;

    var d = new Nilfs1FormatDescriptor();
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

  private static Dictionary<string, byte[]> Snapshot(Nilfs1FormatDescriptor d, MemoryStream ms) {
    ms.Position = 0;
    var dir = Path.Combine(Path.GetTempPath(), "nilfs1-defrag-" + Guid.NewGuid().ToString("N"));
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
