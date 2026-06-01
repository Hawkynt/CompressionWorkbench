#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Gfs1;

namespace Compression.Tests.Gfs1;

[TestFixture]
public class Gfs1DefragTests {

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesAllFiles() {
    var w = new Gfs1Writer();
    w.AddFile("a.txt", "aaaa"u8.ToArray());
    w.AddFile("docs/b.txt", "bbbb"u8.ToArray());
    w.AddFile("docs/api/c.txt", "cccc"u8.ToArray());
    var img = w.Build();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);

    new Gfs1FormatDescriptor().Defragment(ms);

    ms.Position = 0;
    var r = new Gfs1Reader(ms);
    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName["a.txt"], Is.EqualTo("aaaa"u8.ToArray()));
    Assert.That(byName["docs/b.txt"], Is.EqualTo("bbbb"u8.ToArray()));
    Assert.That(byName["docs/api/c.txt"], Is.EqualTo("cccc"u8.ToArray()));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    var w = new Gfs1Writer();
    var alpha = new byte[256]; Array.Fill(alpha, (byte)1);
    var beta = new byte[1024]; Array.Fill(beta, (byte)2);
    var gamma = new byte[4321]; Array.Fill(gamma, (byte)3); // not block-aligned
    var delta = new byte[100]; Array.Fill(delta, (byte)4);
    w.AddFile("alpha.bin", alpha);
    w.AddFile("dir/beta.bin", beta);
    w.AddFile("dir/gamma.bin", gamma);
    w.AddFile("dir/sub/delta.bin", delta);
    var img = w.Build();

    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);
    var originalLength = ms.Length;

    var d = new Gfs1FormatDescriptor();
    ms.Position = 0;
    var before = Snapshot(ms);

    ms.Position = 0;
    d.Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(ms.Length, Is.EqualTo(originalLength), "defrag must not grow or shrink the image");
    ms.Position = 0;
    var after = Snapshot(ms);
    Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
    foreach (var (name, bytes) in before)
      Assert.That(after[name], Is.EqualTo(bytes), $"file '{name}' bytes changed across defrag");
  }

  private static Dictionary<string, byte[]> Snapshot(MemoryStream ms) {
    ms.Position = 0;
    var r = new Gfs1Reader(ms);
    var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      result[e.Name] = r.Extract(e);
    }
    return result;
  }
}
