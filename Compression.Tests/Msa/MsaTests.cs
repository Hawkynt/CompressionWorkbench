using System.Buffers.Binary;

namespace Compression.Tests.Msa;

[TestFixture]
public class MsaTests {

  private static byte[] BuildDiskImage(ushort sectorsPerTrack = 9, ushort sides = 1) {
    var numSides = sides + 1;
    var trackSize = sectorsPerTrack * 512;
    var totalTracks = 80 * numSides;
    var data = new byte[totalTracks * trackSize];
    // Fill with a recognizable pattern
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 251);
    return data;
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_BasicDisk() {
    var disk = BuildDiskImage();
    using var msaStream = new MemoryStream();
    FileFormat.Msa.MsaWriter.Write(msaStream, disk);

    msaStream.Position = 0;
    var r = new FileFormat.Msa.MsaReader(msaStream);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("disk.st"));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_CompressibleData() {
    // Data with lots of runs (highly compressible via RLE)
    var trackSize = 9 * 512;
    var totalTracks = 80 * 2;
    var disk = new byte[totalTracks * trackSize];
    // Fill each track with a single repeated byte
    for (var t = 0; t < totalTracks; t++)
      Array.Fill(disk, (byte)(t % 256), t * trackSize, trackSize);

    using var msaStream = new MemoryStream();
    FileFormat.Msa.MsaWriter.Write(msaStream, disk);

    // Compressed should be smaller than raw
    Assert.That(msaStream.Length, Is.LessThan(disk.Length));

    msaStream.Position = 0;
    var r = new FileFormat.Msa.MsaReader(msaStream);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(disk));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsCorrect() {
    var disk = new byte[80 * 2 * 9 * 512];
    using var ms = new MemoryStream();
    FileFormat.Msa.MsaWriter.Write(ms, disk);

    var magic = BinaryPrimitives.ReadUInt16BigEndian(ms.ToArray());
    Assert.That(magic, Is.EqualTo(0x0E0F));
  }

  [Test, Category("HappyPath")]
  public void Header_Fields() {
    var disk = BuildDiskImage(sectorsPerTrack: 9, sides: 1);
    using var ms = new MemoryStream();
    FileFormat.Msa.MsaWriter.Write(ms, disk);

    ms.Position = 0;
    var r = new FileFormat.Msa.MsaReader(ms);
    Assert.That(r.SectorsPerTrack, Is.EqualTo(9));
    Assert.That(r.Sides, Is.EqualTo(1));
    Assert.That(r.StartTrack, Is.EqualTo(0));
    Assert.That(r.EndTrack, Is.EqualTo(79));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Msa.MsaFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Msa"));
    Assert.That(desc.Extensions, Does.Contain(".msa"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x0E, 0x0F }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var disk = BuildDiskImage();
    using var ms = new MemoryStream();
    FileFormat.Msa.MsaWriter.Write(ms, disk);

    ms.Position = 0;
    var desc = new FileFormat.Msa.MsaFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("disk.st"));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleSided() {
    var disk = BuildDiskImage(sectorsPerTrack: 9, sides: 0);
    using var ms = new MemoryStream();
    FileFormat.Msa.MsaWriter.Write(ms, disk, sectorsPerTrack: 9, sides: 0);

    ms.Position = 0;
    var r = new FileFormat.Msa.MsaReader(ms);
    Assert.That(r.Sides, Is.EqualTo(0));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(disk));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[8]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Msa.MsaReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    data[0] = 0xFF;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Msa.MsaReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_DataWith0xE5Byte() {
    // Test that 0xE5 bytes in the data are correctly handled
    var trackSize = 9 * 512;
    var totalTracks = 80 * 2;
    var disk = new byte[totalTracks * trackSize];
    // Fill with 0xE5 (the RLE marker byte)
    Array.Fill(disk, (byte)0xE5);

    using var ms = new MemoryStream();
    FileFormat.Msa.MsaWriter.Write(ms, disk);

    ms.Position = 0;
    var r = new FileFormat.Msa.MsaReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(disk));
  }
}
