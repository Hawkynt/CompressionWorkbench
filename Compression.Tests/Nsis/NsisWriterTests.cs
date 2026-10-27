using FileFormat.Nsis;

namespace Compression.Tests.Nsis;

[TestFixture]
public class NsisWriterTests {

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new NsisFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleBlock_RoundTrips() {
    var payload = "nsis test block"u8.ToArray();
    var w = new NsisWriter();
    w.AddFile("block_0", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new NsisReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultipleBlocks_AllRoundTrip() {
    var p1 = "first"u8.ToArray();
    var p2 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    var w = new NsisWriter();
    w.AddFile("a", p1);
    w.AddFile("b", p2);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new NsisReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(p1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "nsis descriptor"u8.ToArray());
      var d = new NsisFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "data.bin", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmp);
    }
  }
}
