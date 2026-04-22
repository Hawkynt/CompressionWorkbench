using System.Buffers.Binary;
using System.Text;
using FileSystem.Atari8;

namespace Compression.Tests.Atari8;

[TestFixture]
public class Atari8Tests {

  private const int SectorSize = Atari8Reader.DefaultSectorSize;  // 128
  private const int Header = Atari8Reader.AtrHeaderSize;           // 16

  /// <summary>
  /// Returns image offset for a 1-based sector number (SS/SD only, all 128-byte sectors).
  /// </summary>
  private static int SectorOffset(int sector1Based) => Header + (sector1Based - 1) * SectorSize;

  /// <summary>
  /// Builds a minimal valid SS/SD ATR image with one AtariDOS 2.x file stored in a
  /// single data sector (sector <paramref name="dataSectorNo"/>). Default layout uses
  /// sector 4 for data (stays clear of boot sectors and VTOC).
  /// </summary>
  private static byte[] BuildMinimalAtr(string baseName, string ext, byte[] payload, int dataSectorNo = 4) {
    // Standard SS/SD: 720 sectors x 128 bytes + 16-byte header = 92 176 bytes.
    var img = new byte[Header + 720 * SectorSize];

    // ATR header.
    img[0] = 0x96;
    img[1] = 0x02;
    // paragraphs = (720*128)/16 = 5760
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(2), 5760);
    // sector size
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(4), SectorSize);

    if (payload.Length > SectorSize - 3)
      throw new ArgumentException($"payload > {SectorSize - 3} bytes not supported by this fixture.");

    // Directory slot 0 in sector 361.
    var dirOff = SectorOffset(361);
    var slot = dirOff + 0 * 16;
    slot += 0;  // zero-copy: slot is a local int
    // Flags: active (0x80) + DOS-2 (0x42) = 0xC2.
    img[dirOff + 0 * 16 + 0] = 0x42;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(dirOff + 0 * 16 + 1), 1);             // sector count
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(dirOff + 0 * 16 + 3), (ushort)dataSectorNo);

    var nameBytes = new byte[8];
    for (var i = 0; i < 8; i++)
      nameBytes[i] = i < baseName.Length ? (byte)baseName[i] : (byte)0x20;
    Buffer.BlockCopy(nameBytes, 0, img, dirOff + 0 * 16 + 5, 8);

    var extBytes = new byte[3];
    for (var i = 0; i < 3; i++)
      extBytes[i] = i < ext.Length ? (byte)ext[i] : (byte)0x20;
    Buffer.BlockCopy(extBytes, 0, img, dirOff + 0 * 16 + 13, 3);

    // Re-set flags to active + DOS-2 file (bit 7 + bit 6) = 0xC0. Real AtariDOS uses 0x42.
    img[dirOff + 0 * 16 + 0] = 0x42;

    // Data sector content + chain trailer at last 3 bytes.
    var dOff = SectorOffset(dataSectorNo);
    Buffer.BlockCopy(payload, 0, img, dOff, payload.Length);
    img[dOff + SectorSize - 3] = 0x00;                   // file# top bits | next-hi = 0
    img[dOff + SectorSize - 2] = 0x00;                   // next-lo = 0 (chain end)
    img[dOff + SectorSize - 1] = (byte)(payload.Length & 0x7F);  // byte count in this sector

    return img;
  }

  [Test, Category("HappyPath")]
  public void Reader_ListsSingleFile() {
    var payload = Encoding.ASCII.GetBytes("HELLO ATARI");
    var img = BuildMinimalAtr("HELLO", "TXT", payload);

    using var r = new Atari8Reader(new MemoryStream(img));
    Assert.That(r.SectorSize, Is.EqualTo(128));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.TXT"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.Entries[0].StartSector, Is.EqualTo(4));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsFileContent() {
    var payload = Encoding.ASCII.GetBytes("ATARI 800 XL");
    var img = BuildMinimalAtr("TEST", "DAT", payload);

    using var r = new Atari8Reader(new MemoryStream(img));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new Atari8FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Atari8"));
    Assert.That(d.Extensions, Does.Contain(".atr"));
    Assert.That(d.MagicSignatures, Is.Not.Empty);
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x96, 0x02 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var img = BuildMinimalAtr("ABC", "EXT", "data"u8.ToArray());
    var d = new Atari8FormatDescriptor();
    var entries = d.List(new MemoryStream(img), null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("ABC.EXT"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_MissingMagic_Throws() {
    var img = new byte[Header + 720 * SectorSize];  // zeros, no magic
    Assert.Throws<InvalidDataException>(() =>
      _ = new Atari8Reader(new MemoryStream(img)));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    Assert.Throws<InvalidDataException>(() =>
      _ = new Atari8Reader(new MemoryStream(new byte[32])));
  }
}
