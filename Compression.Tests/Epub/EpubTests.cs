namespace Compression.Tests.Epub;

[TestFixture]
public class EpubTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Epub.EpubFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Epub"));
    Assert.That(d.Extensions, Contains.Item(".epub"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".epub"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_ViaDescriptor() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "test data"u8.ToArray());
      var desc = new FileFormat.Epub.EpubFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)desc).Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "test.txt", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));
    } finally {
      File.Delete(tmpFile);
    }
  }
}
