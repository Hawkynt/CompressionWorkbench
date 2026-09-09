using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Pak;

namespace Compression.Tests.Pak;

[TestFixture]
public class PakDescriptorTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new PakFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(desc.Id, Is.EqualTo("Pak"));
      Assert.That(desc.DefaultExtension, Is.EqualTo(".pak"));
      Assert.That(desc.Category, Is.EqualTo(FormatCategory.Archive));
      Assert.That(desc.Description, Does.Contain("Quake"));
      Assert.That(desc.MagicSignatures.Single().Bytes, Is.EqualTo("PACK"u8.ToArray()));
    });
  }

  [Test, Category("KnownAnswer")]
  public void Reader_ParsesCanonicalPackVector_NotArc() {
    // Independent minimal PACK image:
    // header: "PACK", directory @ 15, one 64-byte record
    // payload @ 12: "abc"
    var image = new byte[12 + 3 + 64];
    "PACK"u8.CopyTo(image);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(4, 4), 15);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(8, 4), 64);
    "abc"u8.CopyTo(image.AsSpan(12, 3));
    Encoding.ASCII.GetBytes("maps/test.bsp").CopyTo(image, 15);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(15 + 56, 4), 12);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(15 + 60, 4), 3);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new PakReader(stream);
    var entry = reader.GetNextEntry();

    Assert.Multiple(() => {
      Assert.That(entry, Is.Not.Null);
      Assert.That(entry!.FileName, Is.EqualTo("maps/test.bsp"));
      Assert.That(entry.FileOffset, Is.EqualTo(12));
      Assert.That(entry.Size, Is.EqualTo(3));
      Assert.That(reader.ReadEntryData(), Is.EqualTo("abc"u8.ToArray()));
      Assert.That(reader.GetNextEntry(), Is.Null);
    });
  }

  [Test, Category("KnownAnswer")]
  public void Writer_EmitsPackHeaderPayloadThenTrailingDirectory() {
    using var stream = new MemoryStream();
    using (var writer = new PakWriter(stream)) {
      writer.AddEntry("test.txt", "xyz"u8.ToArray());
      writer.Finish();
    }
    var bytes = stream.ToArray();

    Assert.Multiple(() => {
      Assert.That(bytes.AsSpan(0, 4).ToArray(), Is.EqualTo("PACK"u8.ToArray()));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)), Is.EqualTo(15));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)), Is.EqualTo(64));
      Assert.That(bytes.AsSpan(12, 3).ToArray(), Is.EqualTo("xyz"u8.ToArray()));
      Assert.That(Encoding.ASCII.GetString(bytes, 15, 8), Is.EqualTo("test.txt"));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(15 + 56, 4)), Is.EqualTo(12));
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(15 + 60, 4)), Is.EqualTo(3));
    });
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      var data = "Hello PAK archive!"u8.ToArray();
      File.WriteAllBytes(tmpFile, data);

      var desc = new PakFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmpFile, "test.txt", false)], new FormatCreateOptions());

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      ms.Position = 0;
      desc.Extract(ms, tmpDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmpDir, "test.txt")), Is.EqualTo(data));
    } finally {
      File.Delete(tmpFile);
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file content"u8.ToArray();
    var data2 = "Second file content"u8.ToArray();
    var data3 = "Third file content"u8.ToArray();
    var desc = new PakFormatDescriptor();
    using var ms = new MemoryStream();
    desc.Create(ms, [
      ArchiveInputInfo.InMemory("a.txt", data1),
      ArchiveInputInfo.InMemory("b.txt", data2),
      ArchiveInputInfo.InMemory("c.txt", data3),
    ], new FormatCreateOptions());

    ms.Position = 0;
    var entries = desc.List(ms, null);
    Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] { "a.txt", "b.txt", "c.txt" }));

    ms.Position = 0;
    Assert.That(desc.ExtractEntryToMemory(ms, "a.txt", null), Is.EqualTo(data1));
    ms.Position = 0;
    Assert.That(desc.ExtractEntryToMemory(ms, "b.txt", null), Is.EqualTo(data2));
    ms.Position = 0;
    Assert.That(desc.ExtractEntryToMemory(ms, "c.txt", null), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void Extract_WithFilter() {
    var desc = new PakFormatDescriptor();
    using var archive = new MemoryStream();
    desc.Create(archive, [
      ArchiveInputInfo.InMemory("alpha.txt", "Alpha"u8.ToArray()),
      ArchiveInputInfo.InMemory("bravo.txt", "Bravo"u8.ToArray()),
      ArchiveInputInfo.InMemory("charlie.txt", "Charlie"u8.ToArray()),
    ], new FormatCreateOptions());

    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      archive.Position = 0;
      desc.Extract(archive, tmpDir, null, ["bravo.txt"]);
      Assert.That(File.Exists(Path.Combine(tmpDir, "bravo.txt")), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(tmpDir, "bravo.txt")), Is.EqualTo("Bravo"u8.ToArray()));
      Assert.That(File.Exists(Path.Combine(tmpDir, "alpha.txt")), Is.False);
      Assert.That(File.Exists(Path.Combine(tmpDir, "charlie.txt")), Is.False);
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }
}
