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
}
