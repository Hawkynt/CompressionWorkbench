namespace Compression.Tests.Msg;

[TestFixture]
public class MsgTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Msg.MsgFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Msg"));
    Assert.That(d.Extensions, Contains.Item(".msg"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }
}
