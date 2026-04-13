namespace Compression.Tests.Ear;

[TestFixture]
public class EarTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Ear.EarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ear"));
    Assert.That(d.Extensions, Contains.Item(".ear"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".ear"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_ViaDescriptor() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "test data"u8.ToArray());
      var desc = new FileFormat.Ear.EarFormatDescriptor();
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
