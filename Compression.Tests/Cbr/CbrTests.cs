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

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Cbr.CbrFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_ViaDescriptor() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "comic page bytes"u8.ToArray());
      var desc = new FileFormat.Cbr.CbrFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmpFile, "page01.png", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("page01.png"));
      Assert.That(entries[0].OriginalSize, Is.EqualTo(16));
    } finally {
      File.Delete(tmpFile);
    }
  }
}
