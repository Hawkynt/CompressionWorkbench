namespace Compression.Tests.Xls;

[TestFixture]
public class XlsTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Xls.XlsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Xls"));
    Assert.That(d.Extensions, Contains.Item(".xls"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Xls.XlsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_ProducesParseableCfb() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "fake xls body"u8.ToArray());
      var d = new FileFormat.Xls.XlsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "Workbook", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Has.Member("Workbook"));
    } finally {
      File.Delete(tmp);
    }
  }
}
