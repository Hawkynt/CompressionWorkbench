using Compression.Registry;
using FileFormat.Tar;

namespace Compression.Tests.Tar;

[TestFixture]
public class TarSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesNonEmptyOptionsSchema() {
    var descriptor = new TarFormatDescriptor();
    Assert.That(descriptor, Is.AssignableTo<IFormatOptionsSchema>());
    var schema = ((IFormatOptionsSchema)descriptor).OptionsSchema;
    Assert.That(schema, Is.Not.Empty);

    var keys = schema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("BlockingFactor"));
    Assert.That(keys, Does.Contain("Format"));
  }

  [Test, Category("HappyPath")]
  public void Create_BlockingFactor1_ProducesSmallerArchiveThanFactor20() {
    // BlockingFactor=1 pads to 512-byte multiples; BlockingFactor=20 pads to 10 KiB.
    // For the same small input the latter must be at least as long, and (for tiny inputs)
    // strictly longer because the 10 KiB record gets padded out.
    var descriptor = new TarFormatDescriptor();
    var data = "tiny"u8.ToArray();

    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_tar_schema_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      var src = Path.Combine(tempDir, "t.txt");
      File.WriteAllBytes(src, data);
      var inputs = new[] { new ArchiveInputInfo(src, "t.txt", false) };

      var smallOut = new MemoryStream();
      descriptor.Create(smallOut, inputs, new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["BlockingFactor"] = "1" }
      });

      var bigOut = new MemoryStream();
      descriptor.Create(bigOut, inputs, new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["BlockingFactor"] = "20" }
      });

      Assert.That(bigOut.Length, Is.GreaterThan(smallOut.Length),
        $"BlockingFactor=20 must produce a longer archive than BlockingFactor=1 " +
        $"for a tiny input (small={smallOut.Length}, big={bigOut.Length}).");
      Assert.That(bigOut.Length % 10240, Is.EqualTo(0),
        "BlockingFactor=20 output must be aligned to 10 KiB.");
    } finally {
      try { Directory.Delete(tempDir, true); } catch { }
    }
  }
}
