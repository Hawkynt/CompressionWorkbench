using Compression.Registry;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public class SevenZipSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesNonEmptyOptionsSchema() {
    var descriptor = new SevenZipFormatDescriptor();
    Assert.That(descriptor, Is.AssignableTo<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)descriptor).OptionsSchema;
    Assert.That(schema, Is.Not.Empty);

    var keys = schema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("Method"));
    Assert.That(keys, Does.Contain("Level"));
    Assert.That(keys, Does.Contain("DictSize"));
    Assert.That(keys, Does.Contain("SolidSize"));
    Assert.That(keys, Does.Contain("Password"));
  }

  [Test, Category("HappyPath")]
  public void Create_MethodDeflate_FromSchema_ProducesValid7zArchive() {
    // Smoke test that the schema Method knob round-trips through to the writer:
    // method=deflate should swap out the default LZMA2 codec and still produce a
    // valid 7z that lists back the original entry.
    var descriptor = new SevenZipFormatDescriptor();
    var data = "Hello, 7z schema knob!"u8.ToArray();

    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_7z_schema_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      var src = Path.Combine(tempDir, "hello.txt");
      File.WriteAllBytes(src, data);

      var output = new MemoryStream();
      var options = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["Method"] = "deflate" }
      };
      descriptor.Create(output, [new ArchiveInputInfo(src, "hello.txt", false)], options);

      Assert.That(output.Length, Is.GreaterThan(32), "7z archive must contain at least a signature header.");
      output.Position = 0;
      var entries = descriptor.List(output, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));
      Assert.That(entries[0].OriginalSize, Is.EqualTo(data.Length));
      // The Method column on the listing should reflect a Deflate-family codec.
      Assert.That(entries[0].Method, Does.Contain("Deflate").IgnoreCase
        .Or.Contain("0408").IgnoreCase
        .Or.Contain("flate").IgnoreCase);
    } finally {
      try { Directory.Delete(tempDir, true); } catch { }
    }
  }
}
