namespace Compression.Tests.ThumbsDb;

[TestFixture]
public class ThumbsDbTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.ThumbsDb.ThumbsDbFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("ThumbsDb"));
    Assert.That(d.Extensions, Contains.Item(".db"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.ThumbsDb.ThumbsDbFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_ProducesParseableCfb() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "fake thumb body"u8.ToArray());
      var d = new FileFormat.ThumbsDb.ThumbsDbFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "Catalog", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Has.Member("Catalog"));
    } finally {
      File.Delete(tmp);
    }
  }
}
