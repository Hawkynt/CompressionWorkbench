using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

[TestFixture]
public class InnoSetupWriterTests {

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new InnoSetupFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Writer_ProducesValidSignature() {
    var w = new InnoSetupWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 23), Is.EqualTo("Inno Setup Setup Data ("));
  }

  [Test, Category("HappyPath")]
  public void Writer_PassesThroughReader() {
    var w = new InnoSetupWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    // Reader doesn't throw on empty Setup.0 — returns empty entries list.
    var r = new InnoSetupReader(ms);
    Assert.That(r.Version, Is.EqualTo("1.0"));
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ProducesValidFile() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "inno test"u8.ToArray());
      var d = new InnoSetupFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "payload.bin", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      // Listing should not throw even if entries are empty.
      Assert.DoesNotThrow(() => d.List(ms, null));
    } finally {
      File.Delete(tmp);
    }
  }
}
