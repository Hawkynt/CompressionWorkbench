#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Cromemco;

namespace Compression.Tests.Cromemco;

/// <summary>
/// Defragment via <see cref="DefragRebuilder.Rebuild"/> extract+rebuild
/// for Cromemco RDOS. Verifies the descriptor implements
/// <see cref="IArchiveDefragmentable"/>, the rebuilt image still passes
/// the reader, and every original (name, bytes) tuple round-trips.
/// </summary>
[TestFixture]
public class CromemcoDefragTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new CromemcoWriter();
    w.SetGeometry(77, 26);
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  [Test]
  public void DescriptorAdvertisesDefragmentable() {
    Assert.That(new CromemcoFormatDescriptor(), Is.InstanceOf<IArchiveDefragmentable>());
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

    var d = new CromemcoFormatDescriptor();
    d.Defragment(ms);

    ms.Position = 0;
    using var reader = new CromemcoReader(ms);
    Assert.That(reader.ValidVolume, Is.True, "Rebuilt image must still parse.");
    var names = reader.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("ALPHA.BIN"));
    Assert.That(names, Does.Contain("BETA.BIN"));
    Assert.That(names, Does.Contain("GAMMA.BIN"));
  }

  [Test]
  public void Defrag_PreservesSizeAndContents() {
    // Five files of varied size; GAMMA.BIN is 1234 bytes (not a 128-byte sector multiple).
    var inputs = new (string Name, byte[] Data)[] {
      ("ALPHA.BIN", new byte[256]),
      ("BETA.BIN", new byte[512]),
      ("GAMMA.BIN", new byte[1234]),
      ("DELTA.BIN", new byte[100]),
    };
    for (var i = 0; i < inputs.Length; ++i) Array.Fill(inputs[i].Data, (byte)(i + 1));
    var image = BuildImage(inputs);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var originalLength = ms.Length;
    ms.Position = 0;

    var d = new CromemcoFormatDescriptor();
    var before = Snapshot(d, ms);

    ms.Position = 0;
    d.Defragment(ms);

    Assert.That(ms.Length, Is.EqualTo(originalLength), "defrag must not grow or shrink the image");
    var after = Snapshot(d, ms);
    Assert.That(after.Keys, Is.EquivalentTo(before.Keys), "defrag must preserve the file set");
    foreach (var (name, bytes) in before)
      Assert.That(after[name], Is.EqualTo(bytes), $"file '{name}' bytes changed across defrag");
  }

  private static Dictionary<string, byte[]> Snapshot(CromemcoFormatDescriptor d, MemoryStream ms) {
    ms.Position = 0;
    var dir = Path.Combine(Path.GetTempPath(), "cromemco-defrag-" + Guid.NewGuid().ToString("N"));
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
