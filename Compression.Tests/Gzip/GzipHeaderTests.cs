using FileFormat.Gzip;

namespace Compression.Tests.Gzip;

[TestFixture]
public class GzipHeaderTests {
  [Category("HappyPath")]
  [Test]
  public void RoundTrip_DefaultHeader() {
    var header = new GzipHeader();
    using var ms = new MemoryStream();
    header.Write(ms);

    ms.Position = 0;
    var parsed = GzipHeader.Read(ms);

    Assert.That(parsed.Method, Is.EqualTo(GzipConstants.MethodDeflate));
    Assert.That(parsed.OperatingSystem, Is.EqualTo(GzipConstants.OsUnknown));
  }

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_WithFileName() {
    var header = new GzipHeader { FileName = "test.txt" };
    using var ms = new MemoryStream();
    header.Write(ms);

    ms.Position = 0;
    var parsed = GzipHeader.Read(ms);

    Assert.That(parsed.FileName, Is.EqualTo("test.txt"));
    Assert.That(parsed.Flags & GzipConstants.FlagName, Is.Not.Zero);
  }

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_WithComment() {
    var header = new GzipHeader { Comment = "This is a test comment" };
    using var ms = new MemoryStream();
    header.Write(ms);

    ms.Position = 0;
    var parsed = GzipHeader.Read(ms);

    Assert.That(parsed.Comment, Is.EqualTo("This is a test comment"));
  }

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_WithExtraField() {
    var header = new GzipHeader { ExtraField = [1, 2, 3, 4, 5] };
    using var ms = new MemoryStream();
    header.Write(ms);

    ms.Position = 0;
    var parsed = GzipHeader.Read(ms);

    Assert.That(parsed.ExtraField, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  [Category("HappyPath")]
  [Test]
  public void RoundTrip_WithAllFields() {
    var header = new GzipHeader {
      ModificationTime = 1234567890,
      ExtraFlags = 2,
      OperatingSystem = GzipConstants.OsUnix,
      FileName = "data.bin",
      Comment = "test",
      ExtraField = [0xAA, 0xBB]
    };

    using var ms = new MemoryStream();
    header.Write(ms);

    ms.Position = 0;
    var parsed = GzipHeader.Read(ms);

    Assert.That(parsed.ModificationTime, Is.EqualTo(1234567890u));
    Assert.That(parsed.ExtraFlags, Is.EqualTo(2));
    Assert.That(parsed.OperatingSystem, Is.EqualTo(GzipConstants.OsUnix));
    Assert.That(parsed.FileName, Is.EqualTo("data.bin"));
    Assert.That(parsed.Comment, Is.EqualTo("test"));
    Assert.That(parsed.ExtraField, Is.EqualTo(new byte[] { 0xAA, 0xBB }));
  }

  [Category("Exception")]
  [Test]
  public void Read_BadMagic_Throws() {
    using var ms = new MemoryStream([0x00, 0x00, 8, 0, 0, 0, 0, 0, 0, 0]);
    Assert.Throws<InvalidDataException>(() => GzipHeader.Read(ms));
  }

  [Category("Exception")]
  [Test]
  public void Read_BadMethod_Throws() {
    using var ms = new MemoryStream([0x1F, 0x8B, 9, 0, 0, 0, 0, 0, 0, 0]);
    Assert.Throws<InvalidDataException>(() => GzipHeader.Read(ms));
  }
}
