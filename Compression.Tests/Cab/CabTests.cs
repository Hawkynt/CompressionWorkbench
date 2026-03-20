using Compression.Core.Dictionary.Quantum;
using FileFormat.Cab;

namespace Compression.Tests.Cab;

[TestFixture]
public sealed class CabTests {
  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static byte[] WriteCab(Action<CabWriter> configure) {
    using var ms = new MemoryStream();
    var writer   = new CabWriter();
    configure(writer);
    writer.WriteTo(ms);
    return ms.ToArray();
  }

  private static CabReader OpenCab(byte[] data) =>
    new(new MemoryStream(data), leaveOpen: false);

  // -------------------------------------------------------------------------
  // MSZIP round-trip tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void MsZip_SingleFile_RoundTrip() {
    var original = "Hello, Cabinet!"u8.ToArray();

    var cabData = WriteCab(w => w.AddFile("hello.txt", original));
    using var reader = OpenCab(cabData);

    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("hello.txt"));
    Assert.That(reader.Entries[0].UncompressedSize, Is.EqualTo((uint)original.Length));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(original));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MsZip_MultipleFiles_RoundTrip() {
    var file1 = "File one content."u8.ToArray();
    var file2 = "File two has different content!"u8.ToArray();
    var file3 = new byte[1024];
    new Random(99).NextBytes(file3);

    var cabData = WriteCab(w => {
      w.AddFile("file1.txt", file1);
      w.AddFile("file2.txt", file2);
      w.AddFile("binary.bin", file3);
    });

    using var reader = OpenCab(cabData);

    Assert.That(reader.Entries.Count, Is.EqualTo(3));

    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(file1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(file2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(file3));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void MsZip_EmptyFile_RoundTrip() {
    var cabData = WriteCab(w => w.AddFile("empty.txt", []));
    using var reader = OpenCab(cabData);

    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].UncompressedSize, Is.EqualTo(0u));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void MsZip_LargeFile_MultiBlock_RoundTrip() {
    // 100 KB — forces multiple 32 KB CFDATA blocks.
    var original = new byte[100 * 1024];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 199);

    var cabData = WriteCab(w => w.AddFile("large.bin", original));
    using var reader = OpenCab(cabData);

    Assert.That(reader.Entries.Count, Is.EqualTo(1));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void MsZip_HighlyCompressible_RoundTrip() {
    // 64 KB of zeros — should compress very well.
    var original = new byte[65536];

    var cabData  = WriteCab(w => w.AddFile("zeros.bin", original));
    using var reader = OpenCab(cabData);

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Store (no compression) round-trip tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Store_SingleFile_RoundTrip() {
    var original = "Stored without compression."u8.ToArray();

    using var ms = new MemoryStream();
    var writer   = new CabWriter(CabCompressionType.None);
    writer.AddFile("stored.txt", original);
    writer.WriteTo(ms);

    using var reader = OpenCab(ms.ToArray());

    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("stored.txt"));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(original));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Store_MultipleFiles_RoundTrip() {
    var f1 = "First."u8.ToArray();
    var f2 = "Second file data."u8.ToArray();

    using var ms = new MemoryStream();
    var writer   = new CabWriter(CabCompressionType.None);
    writer.AddFile("a.txt", f1);
    writer.AddFile("b.txt", f2);
    writer.WriteTo(ms);

    using var reader = OpenCab(ms.ToArray());

    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(f1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(f2));
  }

  // -------------------------------------------------------------------------
  // CAB header / metadata tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void CabHeader_Signature_IsValid() {
    var cabData = WriteCab(w => w.AddFile("x.txt", "x"u8.ToArray()));

    // First four bytes must be "MSCF".
    Assert.That(cabData[0], Is.EqualTo(0x4D));
    Assert.That(cabData[1], Is.EqualTo(0x53));
    Assert.That(cabData[2], Is.EqualTo(0x43));
    Assert.That(cabData[3], Is.EqualTo(0x46));
  }

  [Category("HappyPath")]
  [Test]
  public void CabHeader_Version_IsCorrect() {
    var cabData = WriteCab(w => w.AddFile("x.txt", "x"u8.ToArray()));

    // versionMinor at byte 24, versionMajor at byte 25.
    Assert.That(cabData[24], Is.EqualTo(CabConstants.VersionMinor));
    Assert.That(cabData[25], Is.EqualTo(CabConstants.VersionMajor));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Entry_LastModified_RoundTrips() {
    var date = new DateTime(2024, 6, 15, 10, 30, 0);
    var data = "test"u8.ToArray();

    var cabData = WriteCab(w => w.AddFile("t.txt", data, lastModified: date));
    using var reader = OpenCab(cabData);

    var lm = reader.Entries[0].LastModified;
    Assert.That(lm, Is.Not.Null);
    Assert.That(lm!.Value.Year,   Is.EqualTo(2024));
    Assert.That(lm!.Value.Month,  Is.EqualTo(6));
    Assert.That(lm!.Value.Day,    Is.EqualTo(15));
    Assert.That(lm!.Value.Hour,   Is.EqualTo(10));
    Assert.That(lm!.Value.Minute, Is.EqualTo(30));
  }

  [Category("HappyPath")]
  [Test]
  public void Entry_FolderIndex_IsZero() {
    var cabData = WriteCab(w => {
      w.AddFile("a.txt", "a"u8.ToArray());
      w.AddFile("b.txt", "b"u8.ToArray());
    });
    using var reader = OpenCab(cabData);

    foreach (var entry in reader.Entries)
      Assert.That(entry.FolderIndex, Is.EqualTo(0));
  }

  [Category("Exception")]
  [Test]
  public void CabWriter_UnsupportedCompression_Throws() {
    Assert.Throws<ArgumentException>(() =>
      _ = new CabWriter((CabCompressionType)99));
  }

  [Category("Exception")]
  [Test]
  public void CabReader_InvalidSignature_Throws() {
    var bad = new byte[64];
    bad[0] = 0xFF; // Not "MSCF"

    Assert.Throws<InvalidDataException>(() => _ = new CabReader(new MemoryStream(bad)));
  }

  // -------------------------------------------------------------------------
  // LZX round-trip tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Lzx_SingleFile_RoundTrip() {
    var original = "Hello, Cabinet LZX compression test data!"u8.ToArray();
    var cabData = WriteCab(w => {
      var lzxWriter = new CabWriter(CabCompressionType.Lzx);
      lzxWriter.AddFile("hello.txt", original);
      lzxWriter.WriteTo(new MemoryStream()); // just to validate it works
    });

    // Use a direct LZX writer
    using var ms = new MemoryStream();
    var writer = new CabWriter(CabCompressionType.Lzx);
    writer.AddFile("lzx.txt", original);
    writer.WriteTo(ms);

    ms.Position = 0;
    using var reader = new CabReader(ms, leaveOpen: true);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Quantum round-trip tests
  // -------------------------------------------------------------------------

  private static CabReader OpenCabQuantum(byte[] data) =>
    new(new MemoryStream(data), leaveOpen: false,
      quantumRescaleThreshold: QuantumConstants.CompressorRescaleThreshold);

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Quantum_SingleFile_RoundTrip() {
    var original = "Hello, Quantum compression in a Cabinet!"u8.ToArray();

    using var ms = new MemoryStream();
    var writer = new CabWriter(CabCompressionType.Quantum, quantumWindowLevel: 4);
    writer.AddFile("hello.txt", original);
    writer.WriteTo(ms);

    using var reader = OpenCabQuantum(ms.ToArray());
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("hello.txt"));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(original));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Quantum_MultipleFiles_RoundTrip() {
    var f1 = "First file."u8.ToArray();
    var f2 = "Second file with more content."u8.ToArray();
    var f3 = new byte[256];
    new Random(42).NextBytes(f3);

    using var ms = new MemoryStream();
    var writer = new CabWriter(CabCompressionType.Quantum, quantumWindowLevel: 4);
    writer.AddFile("a.txt", f1);
    writer.AddFile("b.txt", f2);
    writer.AddFile("c.bin", f3);
    writer.WriteTo(ms);

    using var reader = OpenCabQuantum(ms.ToArray());
    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(f1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(f2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(f3));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(1)]
  [TestCase(4)]
  [TestCase(7)]
  public void Quantum_WindowLevels_RoundTrip(int level) {
    var original = new byte[500];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 131);

    using var ms = new MemoryStream();
    var writer = new CabWriter(CabCompressionType.Quantum, quantumWindowLevel: level);
    writer.AddFile("data.bin", original);
    writer.WriteTo(ms);

    using var reader = OpenCabQuantum(ms.ToArray());
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Folder offset tracking
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void Entry_FolderOffsets_AreCorrect() {
    var f1 = new byte[100];
    var f2 = new byte[200];
    var f3 = new byte[50];

    var cabData = WriteCab(w => {
      w.AddFile("f1.bin", f1);
      w.AddFile("f2.bin", f2);
      w.AddFile("f3.bin", f3);
    });

    using var reader = OpenCab(cabData);

    Assert.That(reader.Entries[0].FolderOffset, Is.EqualTo(0u));
    Assert.That(reader.Entries[1].FolderOffset, Is.EqualTo(100u));
    Assert.That(reader.Entries[2].FolderOffset, Is.EqualTo(300u));
  }
}
