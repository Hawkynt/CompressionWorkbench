using System.Text;
using Compression.Registry;

namespace Compression.Tests.DriveSpace3;

[TestFixture]
public class DriveSpace3DetectionTests {

  // Build a minimal DriveSpace 3 MDBPB header: "MS_DSP3" at offset 3.
  private static byte[] BuildMinimalImage() {
    var img = new byte[4096];
    img[0] = 0xEB; img[1] = 0x3C; img[2] = 0x90;
    var sig = Encoding.ASCII.GetBytes("MS_DSP3");
    sig.CopyTo(img.AsSpan(3));
    // MDFAT entries = 16, reserved sectors = 1, volume sectors = 8
    img[0x0A] = 16; img[0x0B] = 0;
    img[0x0C] = 1; img[0x0D] = 0;
    img[0x0E] = 8; img[0x0F] = 0; img[0x10] = 0; img[0x11] = 0;
    // BitFAT entries = 8, sectors/cluster = 4
    img[0x12] = 8; img[0x13] = 0;
    img[0x14] = 4; img[0x15] = 0;
    // MDFAT offset = 1, BitFAT offset = 2
    img[0x16] = 1; img[0x17] = 0; img[0x18] = 0; img[0x19] = 0;
    img[0x1A] = 2; img[0x1B] = 0; img[0x1C] = 0; img[0x1D] = 0;
    // Put a distinguishing byte at the data region.
    img[2048] = 0xAB;
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.DriveSpace3.DriveSpace3FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("DriveSpace3"));
    Assert.That(d.DisplayName, Is.EqualTo("DriveSpace 3 CVF"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".cvf"));
    Assert.That(d.Extensions, Is.Empty); // shared with DoubleSpace by magic
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("MS_DSP3"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void Detect_Magic_MsDsp3() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.DriveSpace3.DriveSpace3Reader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.MdfatEntries, Is.EqualTo(16));
    Assert.That(r.BitfatEntries, Is.EqualTo(8));
    Assert.That(r.SectorsPerCluster, Is.EqualTo(4));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("drivespace3-volume.bin"));
  }

  [Test, Category("Sad")]
  public void Detect_NotDriveSpace3_HasNoValidHeader() {
    var img = new byte[1024];
    Encoding.ASCII.GetBytes("MSDSP6.2").CopyTo(img.AsSpan(3)); // DoubleSpace, not us.
    using var r = new FileSystem.DriveSpace3.DriveSpace3Reader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.False);
    Assert.That(r.Entries, Is.Empty);
  }
}
