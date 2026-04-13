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
}
