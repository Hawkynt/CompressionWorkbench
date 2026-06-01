using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesNonEmptyOptionsSchema() {
    var descriptor = new ZipFormatDescriptor();
    Assert.That(descriptor, Is.AssignableTo<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)descriptor).OptionsSchema;
    Assert.That(schema, Is.Not.Empty);

    var keys = schema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("Method"));
    Assert.That(keys, Does.Contain("Level"));
    Assert.That(keys, Does.Contain("Password"));
    Assert.That(keys, Does.Contain("EncryptionMethod"));
  }

  [Test, Category("HappyPath")]
  public void Create_MethodStore_ProducesUncompressedEntries() {
    // When schema Method=store, the resulting ZIP should have entries whose
    // compression method byte (offset 8 of each local file header) is 0 (Stored).
    var descriptor = new ZipFormatDescriptor();
    var data = new byte[8 * 1024];
    new Random(42).NextBytes(data);
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_zip_schema_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      var dataPath = Path.Combine(tempDir, "blob.bin");
      File.WriteAllBytes(dataPath, data);

      var output = new MemoryStream();
      var options = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["Method"] = "store" }
      };
      descriptor.Create(output, [new ArchiveInputInfo(dataPath, "blob.bin", false)], options);

      // The first local file header sits at offset 0 of the resulting ZIP.
      var bytes = output.ToArray();
      Assert.That(bytes.Length, Is.GreaterThan(30), "ZIP must include at least one LFH.");
      var method = BitConverter.ToUInt16(bytes, 8);
      Assert.That(method, Is.EqualTo(0), "Method=store must produce Stored (method=0) entries.");

      // Sanity: compressed-size field (offset 18) should equal uncompressed-size (offset 22)
      // for stored entries.
      var compressedSize = BitConverter.ToUInt32(bytes, 18);
      var uncompressedSize = BitConverter.ToUInt32(bytes, 22);
      Assert.That(compressedSize, Is.EqualTo(uncompressedSize),
        "Stored entries must have compressed-size == uncompressed-size.");
    } finally {
      try { Directory.Delete(tempDir, true); } catch { }
    }
  }
}
