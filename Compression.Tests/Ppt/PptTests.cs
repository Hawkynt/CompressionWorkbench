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
}
