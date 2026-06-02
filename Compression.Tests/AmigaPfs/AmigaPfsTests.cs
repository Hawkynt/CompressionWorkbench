using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.AmigaPfs;

[TestFixture]
public class AmigaPfsTests {

  // Minimal PFS3 image with single file in root directory.
  //   Block 0     boot block: signature "PFS\x03" at +0, root block ptr at +8
  //   Block 5     root block: disk name at +26, first dirblock ptr at +60
  //   Block 6     dirblock: header at +0 (id=0xC4), entries from +20
  //   Block 7     file data (anode number = 7 in the dirblock entry)
  private const int BlockSize = 512;
  private const uint RootBlock = 5;
  private const uint DirBlock = 6;
  private const uint FileBlock = 7;

  private static byte[] BuildMinimalPfs() {
    var image = new byte[16 * BlockSize];

    // Boot block: signature "PFS\x03" then 4 bytes pad then root-block ptr
    image[0] = (byte)'P';
    image[1] = (byte)'F';
    image[2] = (byte)'S';
    image[3] = 0x03;
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(8), RootBlock);

    // Root block at block 5:
    var rb = (int)(RootBlock * BlockSize);
    image[rb + 26] = 4; // BCPL length prefix
    "DISK"u8.ToArray().CopyTo(image.AsSpan(rb + 27));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(rb + 60), DirBlock);

    // Dirblock at block 6:
    var db = (int)(DirBlock * BlockSize);
    BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(db + 0), 0xC4); // id
    // No next chain — block ptr = 0
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(db + 12), 0);

    // Entry at +20:
    //   length, type, anode(4), fsize(4), date(2), time1(2), time2(2),
    //   nameLen, name, commentLen, ...
    var content = "Amiga PFS3 test"u8.ToArray();
    var nameBytes = "test.txt"u8.ToArray();
    var entryLen = 17 + nameBytes.Length + 1; // +1 for trailing comment-len byte
    image[db + 20 + 0] = (byte)entryLen;
    image[db + 20 + 1] = 0x20; // regular file, protection arbitrary
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(db + 20 + 2), FileBlock);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(db + 20 + 6), (uint)content.Length);
    image[db + 20 + 16] = (byte)nameBytes.Length;
    nameBytes.CopyTo(image.AsSpan(db + 20 + 17));
    // comment length byte = 0
    image[db + 20 + 17 + nameBytes.Length] = 0;
    // Sentinel — next entry's length byte = 0

    // File content at block 7
    content.CopyTo(image.AsSpan((int)(FileBlock * BlockSize)));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.AmigaPfs.AmigaPfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("AmigaPfs"));
    Assert.That(d.Extensions, Does.Contain(".pfs"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(3));
    // R/W-promoted: the descriptor exposes IArchiveCreatable so callers can
    // produce fresh PFS3 images via Create(), and IArchiveModifiable so they
    // can Add/Remove entries against an existing Stage 1 image.
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalPfs();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.AmigaPfs.AmigaPfsReader(ms);
    Assert.That(r.Signature, Is.EqualTo("PFS\x03"));
    Assert.That(r.DiskName, Is.EqualTo("DISK"));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(15));
    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Amiga PFS3 test"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalPfs();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.AmigaPfs.AmigaPfsFormatDescriptor();
    using var s = d.OpenEntry(ms, "test.txt", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(15));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(15));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalPfs();
    img[0] = 0x00; // wreck signature
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.AmigaPfs.AmigaPfsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalPfs();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.AmigaPfs.AmigaPfsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("test.txt"));
    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "test.txt", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Amiga PFS3 test"));
  }
}
