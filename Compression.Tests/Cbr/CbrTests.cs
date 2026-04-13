namespace Compression.Tests.Cbr;

[TestFixture]
public class CbrTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Cbr.CbrFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cbr"));
    Assert.That(d.Extensions, Contains.Item(".cbr"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }
}
