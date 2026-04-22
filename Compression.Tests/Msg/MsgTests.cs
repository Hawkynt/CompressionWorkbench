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

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Msg.MsgFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_ProducesParseableCfb() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "fake msg body"u8.ToArray());
      var d = new FileFormat.Msg.MsgFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "__substg1.0_001A001F", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Has.Member("__substg1.0_001A001F"));
    } finally {
      File.Delete(tmp);
    }
  }
}
