using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

[TestFixture]
public class Tux3WriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_EmitsValidSuperblock() {
    var w = new Tux3Writer();
    w.AddFile("a.txt", "alpha"u8.ToArray());
    var image = w.Build();

    Assert.That(image.AsSpan(4096, 8).SequenceEqual(Tux3Reader.Magic), Is.True);
    var blockBits = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(4096 + 0x30, 8));
    Assert.That(blockBits, Is.EqualTo(12UL));
    var volBlocks = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(4096 + 0x38, 8));
    Assert.That(volBlocks, Is.GreaterThan(0UL));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_EmitsWormTableAtBlock2() {
    var w = new Tux3Writer();
    w.AddFile("a.txt", "alpha"u8.ToArray());
    var image = w.Build();
    Assert.That(image.AsSpan(8192, 8).SequenceEqual(Tux3Reader.WormTableMagic), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(8192 + 8, 4)), Is.EqualTo(1u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTripsSingleFile() {
    var body = Encoding.UTF8.GetBytes("Hello TUX3!");
    var w = new Tux3Writer();
    w.AddFile("hello.txt", body);
    using var ms = new MemoryStream(w.Build());

    var r = new Tux3Reader(ms);
    Assert.That(r.ValidSuperblock, Is.True);
    Assert.That(r.HasWormTable, Is.True);
    Assert.That(r.WormFileCount, Is.EqualTo(1u));

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("hello.txt"), Is.True);
    Assert.That(r.Extract(byName["hello.txt"]), Is.EqualTo(body));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTripsMultipleFiles() {
    var w = new Tux3Writer();
    w.AddFile("a.txt", "alpha"u8.ToArray());
    w.AddFile("b.bin", new byte[] { 1, 2, 3, 4, 5 });
    w.AddFile("c.dat", new byte[1024]);
    using var ms = new MemoryStream(w.Build());

    var r = new Tux3Reader(ms);
    Assert.That(r.WormFileCount, Is.EqualTo(3u));
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("a.txt"), Is.True);
    Assert.That(byName.ContainsKey("b.bin"), Is.True);
    Assert.That(byName.ContainsKey("c.dat"), Is.True);
    Assert.That(Encoding.UTF8.GetString(r.Extract(byName["a.txt"])), Is.EqualTo("alpha"));
    Assert.That(r.Extract(byName["b.bin"]), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    Assert.That(r.Extract(byName["c.dat"]).Length, Is.EqualTo(1024));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTripsEmptyFile() {
    var w = new Tux3Writer();
    w.AddFile("empty.txt", []);
    using var ms = new MemoryStream(w.Build());

    var r = new Tux3Reader(ms);
    Assert.That(r.WormFileCount, Is.EqualTo(1u));
    var entry = r.Entries.First(e => e.Name == "empty.txt");
    Assert.That(entry.Data.Length, Is.EqualTo(0));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_ImageSizeAlignedToBlock() {
    var w = new Tux3Writer();
    w.AddFile("a.txt", "abc"u8.ToArray());
    var image = w.Build();
    Assert.That(image.Length % 4096, Is.EqualTo(0));
    Assert.That(image.Length, Is.GreaterThanOrEqualTo(3 * 4096)); // boot + superblock + worm-table blocks
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_NoFiles_StillProducesValidImage() {
    var w = new Tux3Writer();
    var image = w.Build();
    using var ms = new MemoryStream(image);
    var r = new Tux3Reader(ms);
    Assert.That(r.ValidSuperblock, Is.True);
    Assert.That(r.HasWormTable, Is.True);
    Assert.That(r.WormFileCount, Is.EqualTo(0u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_List_Roundtrip() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
    File.WriteAllBytes(tmp, Encoding.ASCII.GetBytes("file contents"));
    try {
      var desc = new Tux3FormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmp, "myfile.txt", false)], new FormatCreateOptions());
      ms.Position = 0;
      var listed = desc.List(ms, null);
      Assert.That(listed.Select(e => e.Name), Does.Contain("myfile.txt"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_Extract_Roundtrip() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
    var body = "round-trip contents"u8.ToArray();
    File.WriteAllBytes(tmp, body);
    var outDir = Path.Combine(Path.GetTempPath(), $"tux3-out-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outDir);
    try {
      var desc = new Tux3FormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmp, "out.bin", false)], new FormatCreateOptions());
      ms.Position = 0;
      desc.Extract(ms, outDir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(outDir, "out.bin"));
      Assert.That(extracted, Is.EqualTo(body));
    } finally {
      File.Delete(tmp);
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Reader_NoWormTable_StillReadsLegacyImages() {
    // Build an image with only the documented superblock (no WORM sentinel) —
    // simulates a real linux-tux3 prototype dump (HasWormTable=false).
    var image = new byte[8 * 1024];
    var sb = 4096;
    Tux3Reader.Magic.CopyTo(image.AsSpan(sb));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x30, 8), 12UL);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x38, 8), 1024UL);

    using var ms = new MemoryStream(image);
    var r = new Tux3Reader(ms);
    Assert.That(r.ValidSuperblock, Is.True);
    Assert.That(r.HasWormTable, Is.False);
    Assert.That(r.WormFileCount, Is.EqualTo(0u));
    Assert.That(r.Entries.Select(e => e.Name), Does.Contain("FULL.tux3"));
    Assert.That(r.Entries.Select(e => e.Name), Does.Contain("superblock.bin"));
  }

  [Test, Category("EdgeCase")]
  public void Writer_AddFile_EmptyName_Throws() {
    var w = new Tux3Writer();
    Assert.That(() => w.AddFile("", [1, 2, 3]), Throws.InstanceOf<ArgumentException>());
  }
}
