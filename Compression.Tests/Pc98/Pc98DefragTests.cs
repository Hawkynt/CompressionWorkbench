#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Pc98;

namespace Compression.Tests.Pc98;

/// <summary>
/// Defragment via extract-and-rebuild for PC-98. Verifies the descriptor
/// implements <see cref="IArchiveDefragmentable"/>, the rebuilt image
/// parses, and every original (name, bytes) tuple round-trips.
/// </summary>
[TestFixture]
public class Pc98DefragTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new Pc98Writer();
    w.SetBytesPerSector(512);
    w.SetSectorsPerCluster(1);
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  [Test]
  public void DescriptorAdvertisesDefragmentable() {
    Assert.That(new Pc98FormatDescriptor(), Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test, Category("HappyPath")]
  public void DefragRebuilds_PreservingEntries() {
    var image = BuildImage(
      ("ALPHA.BIN", new byte[] { 1, 2, 3, 4 }),
      ("BETA.BIN", new byte[] { 5, 6, 7, 8, 9 }),
      ("GAMMA.BIN", new byte[] { 0xAA, 0xBB, 0xCC }));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = new Pc98FormatDescriptor();
    d.Defragment(ms);

    ms.Position = 0;
    using var reader = new Pc98Reader(ms);
    Assert.That(reader.ValidVolume, Is.True, "Rebuilt image must still parse.");
    var names = reader.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("ALPHA.BIN"));
    Assert.That(names, Does.Contain("BETA.BIN"));
    Assert.That(names, Does.Contain("GAMMA.BIN"));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    var alpha = new byte[256]; Array.Fill(alpha, (byte)1);
    var beta = new byte[1024]; Array.Fill(beta, (byte)2);
    var gamma = new byte[1234]; Array.Fill(gamma, (byte)3); // not cluster-aligned
    var delta = new byte[100]; Array.Fill(delta, (byte)4);
    var image = BuildImage(
      ("ALPHA.BIN", alpha),
      ("BETA.BIN", beta),
      ("GAMMA.BIN", gamma),
      ("DELTA.BIN", delta));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var originalLength = ms.Length;

    var d = new Pc98FormatDescriptor();
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
    using var r = new Pc98Reader(ms);
    var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      result[e.Name] = r.Extract(e);
    }
    return result;
  }
}
