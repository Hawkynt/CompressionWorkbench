namespace Compression.Tests.Tap;

[TestFixture]
public class TapTests {

  private static byte[] RoundTripSingle(string name, byte[] data, byte fileType = 3) {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile(name, data, fileType);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    return r.Extract(r.Entries[0]);
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello ZX Spectrum!"u8.ToArray();
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile("hello", data);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var files = new (string Name, byte[] Data)[] {
      ("program",  "BASIC code here"u8.ToArray()),
      ("screen$",  "screen data here"u8.ToArray()),
      ("data",     new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }),
    };

    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    for (var i = 0; i < files.Length; i++) {
      Assert.That(r.Entries[i].Name, Is.EqualTo(files[i].Name));
      Assert.That(r.Extract(r.Entries[i]), Is.EqualTo(files[i].Data));
    }
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_FileTypes() {
    byte[] payload = [0xAA, 0xBB, 0xCC];

    foreach (var fileType in new byte[] { 0, 1, 2, 3 }) {
      using var ms = new MemoryStream();
      var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
      w.AddFile("test", payload, fileType);
      w.Finish();

      ms.Position = 0;
      var r = new FileFormat.Tap.TapReader(ms);
      Assert.That(r.Entries, Has.Count.EqualTo(1), $"fileType={fileType}");
      Assert.That(r.Entries[0].FileType, Is.EqualTo(fileType), $"fileType={fileType}");
      Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload), $"fileType={fileType}");
    }
  }

  [Test]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Tap.TapFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Tap"));
    Assert.That(desc.Extensions, Contains.Item(".tap"));
    Assert.That(desc.MagicSignatures, Is.Empty);
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test]
  public void EmptyStream_NoEntries() {
    using var ms = new MemoryStream();
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Is.Empty);
  }
}
