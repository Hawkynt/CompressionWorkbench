namespace Compression.Tests.Mpq;

[TestFixture]
public class MpqTests {

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("test.mpq");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Mpq));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByMagic() {
    // MPQ\x1A header
    var header = new byte[512];
    header[0] = 0x4D; // M
    header[1] = 0x50; // P
    header[2] = 0x51; // Q
    header[3] = 0x1A;
    var format = Compression.Lib.FormatDetector.DetectByMagic(header);
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Mpq));
  }

  [Test, Category("HappyPath")]
  public void Detect_UserDataMagic() {
    var header = new byte[512];
    header[0] = 0x4D; // M
    header[1] = 0x50; // P
    header[2] = 0x51; // Q
    header[3] = 0x1B; // User data variant
    var format = Compression.Lib.FormatDetector.DetectByMagic(header);
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Mpq));
  }

  [Test, Category("EdgeCase")]
  public void InvalidData_ThrowsOnRead() {
    var data = new byte[] { 0x00, 0x00, 0x00, 0x00 };
    Assert.Throws<InvalidDataException>(() => new FileFormat.Mpq.MpqReader(new MemoryStream(data)));
  }

  [Test, Category("HappyPath")]
  public void MpqEntry_Flags() {
    var entry = new FileFormat.Mpq.MpqEntry {
      FileName = "test.txt",
      Flags = 0x80000000, // exists
    };
    Assert.That(entry.Exists, Is.True);
    Assert.That(entry.IsCompressed, Is.False);
    Assert.That(entry.IsEncrypted, Is.False);
  }

  [Test, Category("HappyPath")]
  public void MpqEntry_CompressedFlag() {
    var entry = new FileFormat.Mpq.MpqEntry {
      Flags = 0x80000200, // exists + compressed (imploded)
    };
    Assert.That(entry.Exists, Is.True);
    Assert.That(entry.IsCompressed, Is.True);
  }

  [Test, Category("HappyPath")]
  public void MpqEntry_EncryptedFlag() {
    var entry = new FileFormat.Mpq.MpqEntry {
      Flags = 0x80010000, // exists + encrypted
    };
    Assert.That(entry.Exists, Is.True);
    Assert.That(entry.IsEncrypted, Is.True);
  }

  [Test, Category("HappyPath")]
  public void MpqEntry_SingleUnitFlag() {
    var entry = new FileFormat.Mpq.MpqEntry {
      Flags = 0x81000000, // exists + single unit
    };
    Assert.That(entry.IsSingleUnit, Is.True);
  }
}
