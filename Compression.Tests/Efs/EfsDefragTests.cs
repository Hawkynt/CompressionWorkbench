#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Efs;

namespace Compression.Tests.Efs;

[TestFixture]
public class EfsDefragTests {

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesAllFiles() {
    var w = new EfsWriter();
    w.AddFile("a.txt", "aaaa"u8.ToArray());
    w.AddFile("docs/b.txt", "bbbb"u8.ToArray());
    w.AddFile("docs/api/c.txt", "cccc"u8.ToArray());
    var img = w.Build();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);

    new EfsFormatDescriptor().Defragment(ms);

    ms.Position = 0;
    var r = new EfsReader(ms);
    var byName = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName["a.txt"], Is.EqualTo("aaaa"u8.ToArray()));
    Assert.That(byName["docs/b.txt"], Is.EqualTo("bbbb"u8.ToArray()));
    Assert.That(byName["docs/api/c.txt"], Is.EqualTo("cccc"u8.ToArray()));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    // 5 files of varied sizes; 'large.bin' is 4321 bytes (not a multiple of EFS block size).
    var w = new EfsWriter();
    var alpha = new byte[256]; Array.Fill(alpha, (byte)1);
    var beta = new byte[1024]; Array.Fill(beta, (byte)2);
    var gamma = new byte[4321]; Array.Fill(gamma, (byte)3);
    var delta = new byte[100]; Array.Fill(delta, (byte)4);
    var epsilon = new byte[2048]; Array.Fill(epsilon, (byte)5);
    w.AddFile("alpha.bin", alpha);
    w.AddFile("dir/beta.bin", beta);
    w.AddFile("dir/gamma.bin", gamma);
    w.AddFile("dir/sub/delta.bin", delta);
    w.AddFile("epsilon.bin", epsilon);
    var img = w.Build();

    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length);
    var originalLength = ms.Length;

    var d = new EfsFormatDescriptor();
    ms.Position = 0;
    var before = SnapshotFiles(d, ms);

    ms.Position = 0;
    d.Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    Assert.That(ms.Length, Is.EqualTo(originalLength), "defrag must not grow or shrink the image");
    ms.Position = 0;
    var after = SnapshotFiles(d, ms);
    Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
    foreach (var (name, bytes) in before)
      Assert.That(after[name], Is.EqualTo(bytes), $"file '{name}' bytes changed across defrag");
  }

  private static Dictionary<string, byte[]> SnapshotFiles(EfsFormatDescriptor d, MemoryStream ms) {
    ms.Position = 0;
    var r = new EfsReader(ms);
    var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      result[e.Name] = r.Extract(e);
    }
    return result;
  }
}
