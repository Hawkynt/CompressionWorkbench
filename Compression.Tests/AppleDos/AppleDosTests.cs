using FileSystem.AppleDos;

namespace Compression.Tests.AppleDos;

[TestFixture]
public class AppleDosTests {

  private const int TrackSize = AppleDosReader.SectorsPerTrack * AppleDosReader.SectorSize;  // 4096
  private const int SectorSize = AppleDosReader.SectorSize;  // 256

  private static int SectorOffset(int track, int sector) => track * TrackSize + sector * SectorSize;

  /// <summary>
  /// Builds a minimal valid DOS 3.3 disk image holding one text-type file on track 1
  /// sector 0, with its T/S list on track 2 sector 0 and catalog on track 17.
  /// </summary>
  private static byte[] BuildMinimalImage(string name, byte[] fileData, byte fileType = 0x00) {
    var img = new byte[AppleDosReader.StandardSize];

    // --- VTOC at track 17, sector 0 ---
    var vtoc = SectorOffset(17, 0);
    img[vtoc + 0x01] = 17;    // first catalog track
    img[vtoc + 0x02] = 15;    // first catalog sector (convention: sector 15, descending)
    img[vtoc + 0x03] = 3;     // DOS version
    img[vtoc + 0x35] = 16;    // sectors per track
    img[vtoc + 0x27] = 122;   // T/S list pairs per sector (informational)

    // --- Catalog sector at track 17, sector 15 ---
    var cat = SectorOffset(17, 15);
    img[cat + 0x01] = 0;      // no next catalog
    img[cat + 0x02] = 0;

    // Entry 0: T/S list at track 2, sector 0.
    var e0 = cat + 0x0B;
    img[e0 + 0] = 2;           // T/S list track
    img[e0 + 1] = 0;           // T/S list sector
    img[e0 + 2] = fileType;    // file type byte

    // Filename (high-bit ASCII, padded with 0xA0).
    for (var i = 0; i < 30; i++) {
      img[e0 + 3 + i] = i < name.Length ? (byte)(name[i] | 0x80) : (byte)0xA0;
    }
    // Total sector count: 1 data sector + 1 T/S list sector.
    img[e0 + 33] = 2;
    img[e0 + 34] = 0;

    // --- T/S list at track 2, sector 0 ---
    var tsl = SectorOffset(2, 0);
    // Single data-sector pair at offset 0x0C: track 1, sector 0.
    img[tsl + 0x0C + 0] = 1;
    img[tsl + 0x0C + 1] = 0;

    // --- Data at track 1, sector 0 ---
    var dataOff = SectorOffset(1, 0);
    Array.Copy(fileData, 0, img, dataOff, Math.Min(fileData.Length, SectorSize));

    return img;
  }

  [Test, Category("HappyPath")]
  public void Reader_ListsSingleTextFile() {
    var content = "HELLO FROM APPLE II"u8.ToArray();
    var img = BuildMinimalImage("HELLO", content, fileType: 0x00);  // Text type

    using var r = new AppleDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    // Text files: length is sectors * 256.
    Assert.That(r.Entries[0].Size, Is.EqualTo(256));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsTextFileContent() {
    var content = "HELLO APPLE"u8.ToArray();
    var img = BuildMinimalImage("HELLO", content);

    using var r = new AppleDosReader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);

    // Text files aren't length-trimmed, so we get 256 bytes; the first 11 must match.
    Assert.That(extracted.AsSpan(0, content.Length).ToArray(), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsBinaryFileWithLengthPrefix() {
    // Binary (type 0x04): 2-byte load address, 2-byte LE length, then data.
    var payload = new byte[] { 1, 2, 3, 4, 5 };
    var fileData = new byte[4 + payload.Length];
    fileData[0] = 0x00; fileData[1] = 0x20;   // load addr $2000
    fileData[2] = (byte)payload.Length;        // length LE
    fileData[3] = 0;
    Array.Copy(payload, 0, fileData, 4, payload.Length);

    var img = BuildMinimalImage("BINFILE", fileData, fileType: 0x04);

    using var r = new AppleDosReader(new MemoryStream(img));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].FileType, Is.EqualTo(0x04));
    Assert.That(r.Entries[0].Size, Is.EqualTo(4 + payload.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted.Length, Is.EqualTo(4 + payload.Length));
    Assert.That(extracted.AsSpan(4).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new AppleDosFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("AppleDos"));
    Assert.That(d.Extensions, Does.Contain(".dsk"));
    Assert.That(d.Extensions, Does.Contain(".do"));
    Assert.That(d.MaxTotalArchiveSize, Is.EqualTo(143360));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var img = BuildMinimalImage("TEST", "data"u8.ToArray());
    var d = new AppleDosFormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("TEST"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    Assert.Throws<InvalidDataException>(() =>
      _ = new AppleDosReader(new MemoryStream(new byte[100])));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_InvalidVtoc_Throws() {
    // Right size but VTOC bytes don't look like DOS 3.3.
    var img = new byte[AppleDosReader.StandardSize];
    Assert.Throws<InvalidDataException>(() =>
      _ = new AppleDosReader(new MemoryStream(img)));
  }
}
