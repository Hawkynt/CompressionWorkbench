using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Ti99;

[TestFixture]
public class Ti99DetectionTests {

  // Build a minimal TIFiles-wrapped image (the easier of the two wrappers
  // to construct synthetically).
  private static byte[] BuildTifilesImage() {
    var content = "Hello TI-99/4A!"u8.ToArray();
    var img = new byte[128 + content.Length];
    // Magic: 0x07 "TIFILES"
    img[0] = 0x07;
    Encoding.ASCII.GetBytes("TIFILES").CopyTo(img.AsSpan(1));
    // sectors-used count at offset 8 (BE u16) = 1.
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(8, 2), 1);
    // flags at offset 10
    img[10] = 0x80;
    img[11] = 1; // records per sector
    // name at offset 16..25 (space-padded ASCII)
    Encoding.ASCII.GetBytes("MYFILE    ").CopyTo(img.AsSpan(16, 10));
    // Copy content after 128-byte header.
    content.CopyTo(img.AsSpan(128));
    return img;
  }

  // Build a minimal sector-dump image with VIB + FDIR.
  private static byte[] BuildSectorDumpImage() {
    var img = new byte[256 * 16]; // 16 sectors
    // VIB: name "DISK1     " (10 chars space-padded).
    Encoding.ASCII.GetBytes("DISK1     ").CopyTo(img.AsSpan(0, 10));
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(0x0A, 2), 720); // total sectors
    img[0x0C] = 9; // sectors per track
    img[0x0D] = (byte)'D'; img[0x0E] = (byte)'S'; img[0x0F] = (byte)'K';
    img[0x11] = 40; // tracks
    img[0x12] = 2;  // sides
    img[0x13] = 2;  // density

    // FDIR at sector 1 (offset 256): first pointer to sector 4.
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(256 + 0, 2), 4);

    // FDR at sector 4 (offset 1024).
    var fdrOff = 4 * 256;
    Encoding.ASCII.GetBytes("FOO       ").CopyTo(img.AsSpan(fdrOff, 10));
    img[fdrOff + 0x0C] = 0x00; // flags
    img[fdrOff + 0x0D] = 1;    // records per sector
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(fdrOff + 0x0E, 2), 1); // total sectors
    img[fdrOff + 0x10] = 0; // eof byte
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Ti99.Ti99FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ti99"));
    Assert.That(d.DisplayName, Is.EqualTo("TI-99/4A DSR"));
    Assert.That(d.Extensions, Does.Contain(".tifd"));
    Assert.That(d.Extensions, Does.Contain(".tifiles"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Detect_Tifiles_Wrapper() {
    var img = BuildTifilesImage();
    using var r = new FileSystem.Ti99.Ti99Reader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    Assert.That(r.IsTifilesWrapper, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("MYFILE"));
  }

  [Test, Category("HappyPath")]
  public void Detect_SectorDump_Dsk() {
    var img = BuildSectorDumpImage();
    using var r = new FileSystem.Ti99.Ti99Reader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    Assert.That(r.IsTifilesWrapper, Is.False);
    Assert.That(r.VolumeName, Is.EqualTo("DISK1"));
    Assert.That(r.SectorsPerTrack, Is.EqualTo(9));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("FOO"));
  }

  [Test, Category("Sad")]
  public void Detect_NotTi99_HasNoValidVolume() {
    var img = new byte[512];
    using var r = new FileSystem.Ti99.Ti99Reader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.False);
  }
}
