using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

[TestFixture]
public class ProDosTests {

  private const int BlockSize = ProDosReader.BlockSize;  // 512

  private static void WriteLE16(byte[] buf, int off, int value) {
    buf[off] = (byte)(value & 0xFF);
    buf[off + 1] = (byte)((value >> 8) & 0xFF);
  }

  private static void WriteLE24(byte[] buf, int off, int value) {
    buf[off] = (byte)(value & 0xFF);
    buf[off + 1] = (byte)((value >> 8) & 0xFF);
    buf[off + 2] = (byte)((value >> 16) & 0xFF);
  }

  /// <summary>
  /// Builds a minimal 10-block ProDOS image with one seedling file.
  ///   Block 0-1: boot blocks (unused by us).
  ///   Block 2:   volume directory. Header entry + 1 file entry.
  ///   Block 5:   file data.
  /// </summary>
  private static byte[] BuildMinimalImage(string volumeName, string fileName, byte[] fileData) {
    if (fileData.Length > BlockSize)
      throw new ArgumentException("seedling holds <=512 bytes");
    var numBlocks = 10;
    var img = new byte[numBlocks * BlockSize];

    // Volume directory header at block 2, entry 0 (offset 4).
    var vdBlock = 2 * BlockSize;
    // prev=0, next=0 (no chaining in this tiny directory).
    WriteLE16(img, vdBlock + 0, 0);
    WriteLE16(img, vdBlock + 2, 0);

    var hdr = vdBlock + 4;
    // storage_type = 0xF, name_length = volumeName.Length
    img[hdr + 0] = (byte)((0xF << 4) | (volumeName.Length & 0x0F));
    for (var i = 0; i < volumeName.Length && i < 15; i++)
      img[hdr + 1 + i] = (byte)volumeName[i];
    // We leave the rest of the header at zero — sufficient for our parser.

    // File entry at slot 1 (offset 4 + 39 = 43 within the block).
    var fe = vdBlock + 4 + 39;
    var fileKeyBlock = 5;
    // storage_type = 1 (seedling), name_length = fileName.Length
    img[fe + 0] = (byte)((1 << 4) | (fileName.Length & 0x0F));
    for (var i = 0; i < fileName.Length && i < 15; i++)
      img[fe + 1 + i] = (byte)fileName[i];
    img[fe + 0x10] = 0x04;                             // file_type = BIN
    WriteLE16(img, fe + 0x11, fileKeyBlock);           // key_pointer
    WriteLE16(img, fe + 0x13, 1);                      // blocks_used
    WriteLE24(img, fe + 0x15, fileData.Length);        // EOF (24-bit)

    // File data at block 5.
    Array.Copy(fileData, 0, img, fileKeyBlock * BlockSize, fileData.Length);

    return img;
  }

  [Test, Category("HappyPath")]
  public void Reader_ListsSeedlingFile() {
    var content = "ProDOS rocks"u8.ToArray();
    var img = BuildMinimalImage("TESTVOL", "HELLO", content);

    using var r = new ProDosReader(img);
    Assert.That(r.VolumeName, Is.EqualTo("TESTVOL"));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].FullPath, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsSeedlingContent() {
    var content = "ProDOS data bytes"u8.ToArray();
    var img = BuildMinimalImage("VOL", "FILE", content);

    using var r = new ProDosReader(img);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesTwoImgHeader() {
    var content = "wrapped"u8.ToArray();
    var raw = BuildMinimalImage("VOL", "F", content);

    // Prepend a 64-byte .2mg header starting with "2IMG".
    var wrapped = new byte[64 + raw.Length];
    var magic = "2IMG"u8.ToArray();
    Array.Copy(magic, 0, wrapped, 0, 4);
    Array.Copy(raw, 0, wrapped, 64, raw.Length);

    using var r = new ProDosReader(wrapped);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ProDosFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("ProDos"));
    Assert.That(d.Extensions, Does.Contain(".po"));
    Assert.That(d.Extensions, Does.Contain(".2mg"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var img = BuildMinimalImage("VOL", "HI", "x"u8.ToArray());
    var d = new ProDosFormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("HI"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    Assert.Throws<InvalidDataException>(() =>
      _ = new ProDosReader(new MemoryStream(new byte[100])));
  }
}
