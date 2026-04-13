namespace Compression.Tests.Doc;

[TestFixture]
public class DocTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Doc.DocFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Doc"));
    Assert.That(d.Extensions, Contains.Item(".doc"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }
}
