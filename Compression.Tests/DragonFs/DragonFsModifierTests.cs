#pragma warning disable CS1591
using FileSystem.DragonFs;

namespace Compression.Tests.DragonFs;

/// <summary>
/// Round-trip + genuine-in-place verification for <see cref="DragonFsModifier"/>.
/// Add appends a record + data at the image tail and relinks the chain; remove
/// blanks the directory record. Either way, the bytes of existing files must
/// stay byte-identical at their original offsets.
/// </summary>
[TestFixture]
public class DragonFsModifierTests {

  private static MemoryStream BuildSeed(out byte[] seedData) {
    seedData = new byte[700];
    new Random(29).NextBytes(seedData);
    var w = new DragonFsWriter();
    w.AddFile("seed.dat", seedData);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  [Test]
  public void AddFile_ReadsBack() {
    var ms = BuildSeed(out _);
    var data = new byte[3000];
    new Random(2).NextBytes(data);
    DragonFsModifier.AddFile(ms, "data.bin", data);

    ms.Position = 0;
    using var r = new DragonFsReader(ms);
    var e = r.Entries.Single(x => x.Name == "data.bin");
    Assert.That(r.Extract(e).SequenceEqual(data), Is.True);
  }

  [Test]
  public void AddFile_DoesNotMoveExistingData() {
    var ms = BuildSeed(out var seedData);

    ms.Position = 0;
    int seedOffset;
    using (var r0 = new DragonFsReader(ms)) {
      var seed = r0.Entries.Single(e => e.Name == "seed.dat");
      seedOffset = seed.DataOffset;
      Assert.That(r0.Extract(seed).SequenceEqual(seedData), Is.True);
    }
    var lenBefore = ms.Length;

    DragonFsModifier.AddFile(ms, "extra.bin", new byte[1234]);

    // Seed bytes must remain byte-identical at their original offset.
    var after = new byte[seedData.Length];
    ms.Position = seedOffset;
    ms.ReadExactly(after);
    Assert.That(after.SequenceEqual(seedData), Is.True, "existing file data moved — not in-place");
    // Image grows only at the tail.
    Assert.That(ms.Length, Is.GreaterThan(lenBefore));
    Assert.That(ms.Length, Is.EqualTo(lenBefore + 32 + 1234));
  }

  [Test]
  public void RemoveFile_DeletesButKeepsSeed() {
    var ms = BuildSeed(out _);
    DragonFsModifier.AddFile(ms, "victim.bin", new byte[600]);
    Assert.That(DragonFsModifier.RemoveFile(ms, "victim.bin"), Is.True);

    ms.Position = 0;
    using var r = new DragonFsReader(ms);
    Assert.That(r.Entries.Any(e => e.Name == "victim.bin"), Is.False);
    Assert.That(r.Entries.Any(e => e.Name == "seed.dat"), Is.True);
  }

  [Test]
  public void Remove_DoesNotMoveOtherData() {
    var ms = BuildSeed(out var seedData);
    DragonFsModifier.AddFile(ms, "a.bin", new byte[300]);

    ms.Position = 0;
    int seedOffset;
    using (var r0 = new DragonFsReader(ms))
      seedOffset = r0.Entries.Single(e => e.Name == "seed.dat").DataOffset;

    Assert.That(DragonFsModifier.RemoveFile(ms, "a.bin"), Is.True);

    var after = new byte[seedData.Length];
    ms.Position = seedOffset;
    ms.ReadExactly(after);
    Assert.That(after.SequenceEqual(seedData), Is.True, "seed bytes moved on remove — not in-place");
  }
}
