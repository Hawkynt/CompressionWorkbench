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

  // ── WORM creation ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Mpq.MpqFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTripsThroughReader() {
    var payload = "blizzard worm payload"u8.ToArray();
    var w = new FileFormat.Mpq.MpqWriter();
    w.AddFile("readme.txt", payload);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Mpq.MpqReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.FileName.Equals("readme.txt", StringComparison.OrdinalIgnoreCase));
    Assert.That(entry, Is.Not.Null, "writer must produce a roundtrippable readme.txt entry");
    Assert.That(r.Extract(entry!), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultipleFiles_AllRoundTrip() {
    var p1 = new byte[100];
    var p2 = new byte[200];
    var p3 = new byte[5_000];
    new Random(1).NextBytes(p1);
    new Random(2).NextBytes(p2);
    new Random(3).NextBytes(p3);

    var w = new FileFormat.Mpq.MpqWriter();
    w.AddFile("data\\one.bin", p1);
    w.AddFile("data\\two.bin", p2);
    w.AddFile("scripts\\big.lua", p3);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Mpq.MpqReader(ms);
    var byName = r.Entries.ToDictionary(e => e.FileName, StringComparer.OrdinalIgnoreCase);
    // 3 user files + the auto-generated (listfile).
    Assert.That(byName.Keys, Does.Contain("(listfile)"));
    Assert.That(r.Extract(byName["data\\one.bin"]), Is.EqualTo(p1));
    Assert.That(r.Extract(byName["data\\two.bin"]), Is.EqualTo(p2));
    Assert.That(r.Extract(byName["scripts\\big.lua"]), Is.EqualTo(p3));
  }

  [Test, Category("HappyPath")]
  public void Writer_HasMpqMagic() {
    var w = new FileFormat.Mpq.MpqWriter();
    w.AddFile("a.txt", "x"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    // Magic "MPQ\x1A" little-endian = 0x1A51504D
    Assert.That(bytes[..4], Is.EqualTo(new byte[] { (byte)'M', (byte)'P', (byte)'Q', 0x1A }));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_ListfileName_IsReserved() {
    var w = new FileFormat.Mpq.MpqWriter();
    Assert.Throws<ArgumentException>(() => w.AddFile("(listfile)", [1]));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "mpq descriptor test"u8.ToArray());
      var d = new FileFormat.Mpq.MpqFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "test.txt", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Select(e => e.Name), Has.Member("test.txt"));
    } finally {
      File.Delete(tmp);
    }
  }
}
