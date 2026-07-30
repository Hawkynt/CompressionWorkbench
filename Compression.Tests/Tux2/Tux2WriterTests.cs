using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Tux2;

namespace Compression.Tests.Tux2;

[TestFixture]
public class Tux2WriterTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_EmitsValidHeader() {
    var w = new Tux2Writer();
    w.AddFile("hello.txt", "Hello TUX2!"u8.ToArray());
    var image = w.Build();

    Assert.That(image.AsSpan(0, 8).SequenceEqual(Tux2Reader.Magic), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(8, 4)), Is.EqualTo(1u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(12, 4)), Is.EqualTo(1u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTripsSingleFile() {
    var body = Encoding.UTF8.GetBytes("Hello TUX2 World!");
    var w = new Tux2Writer();
    w.AddFile("hello.txt", body);
    using var ms = new MemoryStream(w.Build());

    var r = new Tux2Reader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.FileCount, Is.EqualTo(1u));

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("hello.txt"), Is.True);
    Assert.That(r.Extract(byName["hello.txt"]), Is.EqualTo(body));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTripsMultipleFiles() {
    var w = new Tux2Writer();
    w.AddFile("a.txt", "alpha"u8.ToArray());
    w.AddFile("b.bin", new byte[] { 1, 2, 3, 4, 5 });
    w.AddFile("c.dat", new byte[1024]); // larger payload
    using var ms = new MemoryStream(w.Build());

    var r = new Tux2Reader(ms);
    Assert.That(r.FileCount, Is.EqualTo(3u));
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
    var w = new Tux2Writer();
    w.AddFile("empty.txt", []);
    using var ms = new MemoryStream(w.Build());

    var r = new Tux2Reader(ms);
    Assert.That(r.FileCount, Is.EqualTo(1u));
    var entry = r.Entries.First(e => e.Name == "empty.txt");
    Assert.That(entry.Data.Length, Is.EqualTo(0));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_HandlesUtf8Names() {
    var w = new Tux2Writer();
    w.AddFile("héllo-世界.txt", "unicode"u8.ToArray());
    using var ms = new MemoryStream(w.Build());

    var r = new Tux2Reader(ms);
    var entry = r.Entries.First(e => e.Name == "héllo-世界.txt");
    // The reader leaves a file's bytes in the image and records where they are,
    // so the content comes back through Extract rather than off the entry.
    Assert.That(Encoding.UTF8.GetString(r.Extract(entry)), Is.EqualTo("unicode"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_List_Roundtrip() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");
    File.WriteAllBytes(tmp, Encoding.ASCII.GetBytes("file contents"));
    try {
      var desc = new Tux2FormatDescriptor();
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
    var outDir = Path.Combine(Path.GetTempPath(), $"tux2-out-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outDir);
    try {
      var desc = new Tux2FormatDescriptor();
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
  public void Writer_AddFile_EmptyName_Throws() {
    var w = new Tux2Writer();
    Assert.That(() => w.AddFile("", [1, 2, 3]), Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("EdgeCase")]
  public void Writer_AddFile_NullData_Throws() {
    var w = new Tux2Writer();
    Assert.That(() => w.AddFile("x.txt", null!), Throws.InstanceOf<ArgumentNullException>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_NoInputs_EmitsValidEmptyImage() {
    var desc = new Tux2FormatDescriptor();
    using var ms = new MemoryStream();
    desc.Create(ms, [], new FormatCreateOptions());
    ms.Position = 0;
    var r = new Tux2Reader(ms);
    Assert.That(r.FileCount, Is.EqualTo(0u));
    Assert.That(r.ValidHeader, Is.True);
  }
}
