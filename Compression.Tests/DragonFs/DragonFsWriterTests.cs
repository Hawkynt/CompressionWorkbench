using System.Text;
using Compression.Registry;
using FileSystem.DragonFs;

namespace Compression.Tests.DragonFs;

[TestFixture]
public class DragonFsWriterTests {

  // ── Round-trip: build with the writer, read back with the reader ──────

  [Test, Category("HappyPath")]
  public void SingleFile_RoundTrips_ThroughReader() {
    var content = "Hello DragonFS!"u8.ToArray();

    var w = new DragonFsWriter();
    w.AddFile("hello.txt", content);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new DragonFsReader(ms);

    Assert.That(r.ValidRoot, Is.True);
    Assert.That(r.RootOffset, Is.EqualTo(264));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void MultipleFiles_PreserveNamesAndContent() {
    var files = new (string Name, byte[] Data)[] {
      ("alpha.bin", new byte[] { 1, 2, 3, 4, 5 }),
      ("beta.dat", Encoding.ASCII.GetBytes("the quick brown fox")),
      ("gamma", new byte[256]), // exercises a 256-byte payload (one DragonDOS sector)
    };
    for (var i = 0; i < files[2].Data.Length; ++i) files[2].Data[i] = (byte)(i & 0xFF);

    var w = new DragonFsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);

    using var ms = new MemoryStream(w.Build());
    var r = new DragonFsReader(ms);

    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(files.Select(f => f.Name)));
    for (var i = 0; i < files.Length; ++i)
      Assert.That(r.Extract(r.Entries[i]), Is.EqualTo(files[i].Data), $"content of {files[i].Name}");
  }

  [Test, Category("HappyPath")]
  public void WriteTo_EmitsSameBytesAsBuild() {
    var w = new DragonFsWriter();
    w.AddFile("a.txt", "one"u8.ToArray());
    w.AddFile("b.txt", "two"u8.ToArray());

    using var ms = new MemoryStream();
    w.WriteTo(ms);

    Assert.That(ms.ToArray(), Is.EqualTo(w.Build()));
  }

  [Test, Category("HappyPath")]
  public void EmptyImage_HasValidRootAndNoEntries() {
    var w = new DragonFsWriter();
    using var ms = new MemoryStream(w.Build());
    var r = new DragonFsReader(ms);

    Assert.That(r.ValidRoot, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("EdgeCase")]
  public void NestedInputPath_IsFlattenedToLeafName() {
    var w = new DragonFsWriter();
    w.AddFile("assets/textures/wall.png", new byte[] { 0xDE, 0xAD });

    using var ms = new MemoryStream(w.Build());
    var r = new DragonFsReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("wall.png"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(new byte[] { 0xDE, 0xAD }));
  }

  // ── Descriptor wiring: IArchiveCreatable.Create round-trips ───────────

  [Test, Category("HappyPath")]
  public void Descriptor_Create_RoundTripsThroughList() {
    var descriptor = new DragonFsFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("readme.txt", "first"u8.ToArray()),
      ArchiveInputInfo.InMemory("data.bin", new byte[] { 9, 8, 7, 6 }),
    };

    using var ms = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = descriptor.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "readme.txt", "data.bin" }));

    ms.Position = 0;
    var r = new DragonFsReader(ms);
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries[0])), Is.EqualTo("first"));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(new byte[] { 9, 8, 7, 6 }));
  }
}
