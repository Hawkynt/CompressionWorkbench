namespace Compression.Tests.Ppt;

[TestFixture]
public class PptTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Ppt.PptFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ppt"));
    Assert.That(d.Extensions, Contains.Item(".ppt"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Ppt.PptFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_ProducesParseableCfb() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "fake ppt body"u8.ToArray());
      var d = new FileFormat.Ppt.PptFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "PowerPoint Document", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Has.Member("PowerPoint Document"));
    } finally {
      File.Delete(tmp);
    }
  }
}
