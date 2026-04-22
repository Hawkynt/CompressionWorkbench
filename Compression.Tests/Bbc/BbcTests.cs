using FileSystem.Bbc;

namespace Compression.Tests.Bbc;

[TestFixture]
public class BbcTests {

  private const int SectorSize = BbcReader.SectorSize;  // 256

  /// <summary>
  /// Builds a minimal 40-track SSD image (100 000 bytes) holding one file on track 0
  /// starting at sector 2 (the catalog takes sectors 0 and 1).
  /// </summary>
  private static byte[] BuildMinimalImage(string title, string name, byte[] fileData,
                                          char dir = '$', bool locked = false,
                                          uint loadAddr = 0x1900, uint execAddr = 0x1900) {
    const int tracks = 40;
    const int sectorsPerTrack = 10;
    var img = new byte[tracks * sectorsPerTrack * SectorSize];

    // Title chars 0-7 in sector 0, chars 8-11 in sector 1 bytes 0-3.
    var titlePadded = title.PadRight(12).Substring(0, 12);
    for (var i = 0; i < 8; i++) img[0 + i] = (byte)titlePadded[i];
    for (var i = 0; i < 4; i++) img[SectorSize + i] = (byte)titlePadded[8 + i];

    // Number of entries: 1. byte 5 holds count * 8.
    img[SectorSize + 5] = 1 * 8;

    // Total sector count = 400 (40 tracks x 10). byte 7 low, byte 6 bits 0-1 high.
    img[SectorSize + 7] = 400 & 0xFF;
    img[SectorSize + 6] = (byte)((400 >> 8) & 0x03);

    // --- Name entry at sector 0 offset 8 (first 8-byte slot) ---
    var namePadded = name.PadRight(7).Substring(0, 7);
    for (var i = 0; i < 7; i++) img[8 + i] = (byte)namePadded[i];
    img[8 + 7] = (byte)((locked ? 0x80 : 0) | (dir & 0x7F));

    // --- Metadata entry at sector 1 offset 8 ---
    var m = SectorSize + 8;
    img[m + 0] = (byte)(loadAddr & 0xFF);
    img[m + 1] = (byte)((loadAddr >> 8) & 0xFF);
    img[m + 2] = (byte)(execAddr & 0xFF);
    img[m + 3] = (byte)((execAddr >> 8) & 0xFF);
    img[m + 4] = (byte)(fileData.Length & 0xFF);
    img[m + 5] = (byte)((fileData.Length >> 8) & 0xFF);

    // Packed high bits: start sector hi in 0-1, load hi in 2-3, length hi in 4-5, exec hi in 6-7.
    var startSector = 2;  // data begins at sector 2 (catalog uses 0-1).
    var packed = 0;
    packed |= (startSector >> 8) & 0x03;
    packed |= ((int)((loadAddr >> 16) & 0x03)) << 2;
    packed |= ((fileData.Length >> 16) & 0x03) << 4;
    packed |= ((int)((execAddr >> 16) & 0x03)) << 6;
    img[m + 6] = (byte)packed;
    img[m + 7] = (byte)(startSector & 0xFF);

    // --- File data at sector 2 ---
    Array.Copy(fileData, 0, img, startSector * SectorSize, fileData.Length);

    return img;
  }

  [Test, Category("HappyPath")]
  public void Reader_ListsOneFile() {
    var data = "HELLO"u8.ToArray();
    var img = BuildMinimalImage("MYDISK", "PROG", data);

    using var r = new BbcReader(new MemoryStream(img));
    Assert.That(r.DiskTitle, Is.EqualTo("MYDISK"));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("PROG"));
    Assert.That(r.Entries[0].Directory, Is.EqualTo('$'));
    Assert.That(r.Entries[0].FullName, Is.EqualTo("$.PROG"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsExactBytes() {
    var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03 };
    var img = BuildMinimalImage("D", "FILE", data);

    using var r = new BbcReader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Reader_PreservesLoadExecAddresses() {
    var data = "x"u8.ToArray();
    // DFS load/exec addresses are 18-bit (16 data bits + 2 high bits packed elsewhere).
    // We test a value that fits in that range. 0x021900 => high bits = 0x02, low = 0x1900.
    // The reader sign-extends the 0xFF prefix when the top of the 2-bit field is set.
    var img = BuildMinimalImage("D", "F", data, loadAddr: 0x021900, execAddr: 0x028023);

    using var r = new BbcReader(new MemoryStream(img));
    // Sign-extended result: 0x02 in the high field -> 0xFF000000 prefix.
    Assert.That(r.Entries[0].LoadAddress, Is.EqualTo(0xFF021900u));
    Assert.That(r.Entries[0].ExecAddress, Is.EqualTo(0xFF028023u));
  }

  [Test, Category("HappyPath")]
  public void Reader_LockedFlag() {
    var img = BuildMinimalImage("D", "F", "x"u8.ToArray(), locked: true);
    using var r = new BbcReader(new MemoryStream(img));
    Assert.That(r.Entries[0].IsLocked, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new BbcFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Bbc"));
    Assert.That(d.Extensions, Does.Contain(".ssd"));
    Assert.That(d.Extensions, Does.Contain(".dsd"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var img = BuildMinimalImage("D", "T", "data"u8.ToArray());
    var d = new BbcFormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("$.T"));
  }
}
