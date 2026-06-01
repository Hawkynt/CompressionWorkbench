#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.ApplePascal;

namespace Compression.Tests.ApplePascal;

[TestFixture]
public class ApplePascalDefragTests {

  private static byte[] BuildImage() {
    var w = new ApplePascalWriter();
    w.AddFile("ALPHA.DAT", Encoding.ASCII.GetBytes(new string('A', 1500)));
    w.AddFile("BETA.DAT", Encoding.ASCII.GetBytes(new string('B', 700)));
    w.AddFile("GAMMA.DAT", Encoding.ASCII.GetBytes(new string('G', 2000)));
    return w.Build();
  }

  [Test]
  public void Descriptor_OffersDefragCapability() {
    Assert.That(new ApplePascalFormatDescriptor(), Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Defragment_PreservesAllFileContent() {
    var d = new ApplePascalFormatDescriptor();
    var image = BuildImage();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    d.Defragment(ms);

    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => !e.IsDirectory).ToDictionary(e => e.Name);
    Assert.That(entries, Does.ContainKey("ALPHA.DAT"));
    Assert.That(entries, Does.ContainKey("BETA.DAT"));
    Assert.That(entries, Does.ContainKey("GAMMA.DAT"));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    var d = new ApplePascalFormatDescriptor();
    var image = BuildImage();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    ms.Position = 0;

    var originalLength = ms.Length;
    var before = SnapshotFiles(d, ms);

    ms.Position = 0;
    d.Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    ms.Position = 0;
    Assert.That(ms.Length, Is.EqualTo(originalLength), "defrag must not grow or shrink the image");
    var after = SnapshotFiles(d, ms);
    Assert.That(after.Keys, Is.EquivalentTo(before.Keys), "defrag must preserve the file set");
    foreach (var (name, bytes) in before)
      Assert.That(after[name], Is.EqualTo(bytes), $"file '{name}' bytes changed across defrag");
  }

  private static Dictionary<string, byte[]> SnapshotFiles(ApplePascalFormatDescriptor d, MemoryStream ms) {
    ms.Position = 0;
    var dir = Path.Combine(Path.GetTempPath(), "applepascal-defrag-" + Guid.NewGuid().ToString("N"));
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
