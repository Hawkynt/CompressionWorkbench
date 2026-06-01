using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.ApplePascal;

[TestFixture]
public class ApplePascalDetectionTests {

  // Build a minimal Apple Pascal volume image with one file in the
  // directory at block 2 (file offset 0x400).
  private static byte[] BuildMinimalImage() {
    const int blockSize = 512;
    const int directoryOffset = 2 * blockSize; // 0x400
    var img = new byte[blockSize * 10];

    // Volume header (entry 0): type 0, first=0, next=6, name "TESTVOL".
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(directoryOffset + 0, 2), 0); // first block
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(directoryOffset + 2, 2), 6); // next block
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(directoryOffset + 4, 2), 0); // entry type
    img[directoryOffset + 6] = 7; // volume name length
    Encoding.ASCII.GetBytes("TESTVOL").CopyTo(img.AsSpan(directoryOffset + 7));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(directoryOffset + 14, 2), 10); // total blocks
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(directoryOffset + 16, 2), 1);  // file count

    // First file entry (at offset directoryOffset + 26): block 6..7, kind=3 (text),
    // name "HELLO", 256 bytes in last block.
    var entryOffset = directoryOffset + 26;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(entryOffset + 0, 2), 6); // start
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(entryOffset + 2, 2), 8); // end (exclusive)
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(entryOffset + 4, 2), 3); // kind=text
    img[entryOffset + 6] = 5; // filename length
    Encoding.ASCII.GetBytes("HELLO").CopyTo(img.AsSpan(entryOffset + 7));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(entryOffset + 22, 2), 256); // bytes in last

    // Put recognizable content in the file data region (blocks 6..7).
    Encoding.ASCII.GetBytes("Hello Pascal!").CopyTo(img.AsSpan(6 * blockSize));

    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.ApplePascal.ApplePascalFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("ApplePascal"));
    Assert.That(d.DisplayName, Is.EqualTo("Apple UCSD Pascal"));
    Assert.That(d.Extensions, Does.Contain(".pvol"));
    Assert.That(d.Extensions, Does.Contain(".pdv"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Is.Empty); // Geometry-only detection.
  }

  [Test, Category("HappyPath")]
  public void Detect_VolumeHeader_AtBlock2() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.ApplePascal.ApplePascalReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    Assert.That(r.VolumeName, Is.EqualTo("TESTVOL"));
    Assert.That(r.TotalBlocks, Is.EqualTo(10));
    Assert.That(r.FileCount, Is.EqualTo(1));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.text"));
    Assert.That(r.Entries[0].StartBlock, Is.EqualTo(6));
    Assert.That(r.Entries[0].EndBlock, Is.EqualTo(8));
  }

  [Test, Category("HappyPath")]
  public void Extract_File_ReturnsExpectedBytes() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.ApplePascal.ApplePascalReader(new MemoryStream(img));
    var data = r.Extract(r.Entries[0]);
    Assert.That(data.Length, Is.GreaterThan(0));
    Assert.That(Encoding.ASCII.GetString(data.AsSpan(0, 13).ToArray()), Is.EqualTo("Hello Pascal!"));
  }

  [Test, Category("Sad")]
  public void Detect_NotApplePascal_HasNoValidVolume() {
    var img = new byte[2048];
    using var r = new FileSystem.ApplePascal.ApplePascalReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.False);
  }
}
