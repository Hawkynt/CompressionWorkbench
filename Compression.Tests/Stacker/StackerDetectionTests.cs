using Compression.Registry;

namespace Compression.Tests.Stacker;

[TestFixture]
public class StackerDetectionTests {

  // Build a minimal Stacker CVF header: "STK" + version 3, plus a tiny
  // fake inner FAT boot sector at offset 512 to give the reader something
  // to surface as an entry.
  private static byte[] BuildMinimalImage() {
    var img = new byte[2048];
    img[0] = 0x53; // 'S'
    img[1] = 0x54; // 'T'
    img[2] = 0x4B; // 'K'
    img[3] = 3;    // version 3
    // reserved sectors = 1, sectors per cluster = 4
    img[4] = 1; img[5] = 0;
    img[6] = 4; img[7] = 0;
    // volume sectors = 4 (LE 32-bit)
    img[8] = 4; img[9] = 0; img[10] = 0; img[11] = 0;
    // inner boot sector offset = 1 (sector unit)
    img[12] = 1; img[13] = 0; img[14] = 0; img[15] = 0;
    // Place a recognizable byte at the inner-boot-sector location (offset 512).
    img[512] = 0xEB;
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Stacker.StackerFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Stacker"));
    Assert.That(d.DisplayName, Is.EqualTo("Stacker CVF"));
    Assert.That(d.Extensions, Does.Contain(".sta"));
    Assert.That(d.Extensions, Does.Contain(".stk"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
  }

  [Test, Category("HappyPath")]
  public void Detect_Magic_StkV3() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.Stacker.StackerReader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Version, Is.EqualTo(3));
    Assert.That(r.ReservedSectors, Is.EqualTo(1));
    Assert.That(r.SectorsPerCluster, Is.EqualTo(4));
    Assert.That(r.VolumeSectors, Is.EqualTo(4));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("stacker-volume.bin"));
  }

  [Test, Category("Sad")]
  public void Detect_NotStacker_HasNoValidHeader() {
    var img = new byte[1024];
    img[0] = 0xFF; img[1] = 0xFF; img[2] = 0xFF;
    using var r = new FileSystem.Stacker.StackerReader(new MemoryStream(img));
    Assert.That(r.ValidHeader, Is.False);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalImage();
    var d = new FileSystem.Stacker.StackerFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("stacker-volume.bin"));
  }
}
