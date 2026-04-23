using System.Buffers.Binary;
using System.Text;
using FileFormat.UImage;

namespace Compression.Tests.UImage;

[TestFixture]
public class UImageTests {

  private const uint Crc32Init = 0xFFFFFFFFu;
  private const uint Crc32Poly = 0xEDB88320u;

  private static uint Crc32(byte[] data) {
    var crc = Crc32Init;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; k++)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ Crc32Poly : crc >> 1;
    }
    return crc ^ 0xFFFFFFFFu;
  }

  private static byte[] BuildUImage(byte[] body, byte os = 5, byte arch = 2,
      byte type = 2, byte comp = 0, string name = "test") {
    var header = new byte[UImageReader.HeaderSize];
    BinaryPrimitives.WriteUInt32BigEndian(header, UImageReader.Magic);
    // hcrc intentionally left 0 for now (will be computed below)
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x12345678u); // time
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)body.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 0x80008000u); // load
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 0x80008000u); // ep
    var dcrc = Crc32(body);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), dcrc);
    header[28] = os;
    header[29] = arch;
    header[30] = type;
    header[31] = comp;
    var nameBytes = Encoding.ASCII.GetBytes(name);
    Array.Copy(nameBytes, 0, header, 32, Math.Min(nameBytes.Length, UImageReader.NameLength));
    // Compute header CRC with hcrc field zeroed, then write it in.
    var hcrc = Crc32(header);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), hcrc);

    var all = new byte[header.Length + body.Length];
    Buffer.BlockCopy(header, 0, all, 0, header.Length);
    Buffer.BlockCopy(body, 0, all, header.Length, body.Length);
    return all;
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesHeaderAndBodyVerifiesChecksums() {
    var body = new byte[64];
    for (var i = 0; i < body.Length; i++) body[i] = (byte)i;
    var data = BuildUImage(body, comp: 0, name: "kernel");

    var img = UImageReader.Read(data);
    Assert.Multiple(() => {
      Assert.That(img.Magic, Is.EqualTo(UImageReader.Magic));
      Assert.That(img.Name, Is.EqualTo("kernel"));
      Assert.That(img.DataSize, Is.EqualTo((uint)body.Length));
      Assert.That(img.Body, Is.EqualTo(body).AsCollection);
      Assert.That(img.HeaderCrc, Is.EqualTo(img.ComputedHeaderCrc));
      Assert.That(img.DataCrc, Is.EqualTo(img.ComputedDataCrc));
      Assert.That(UImageReader.OsName(img.Os), Is.EqualTo("LINUX"));
      Assert.That(UImageReader.ArchName(img.Architecture), Is.EqualTo("ARM"));
      Assert.That(UImageReader.CompressionName(img.Compression), Is.EqualTo("none"));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_EmitsMetadataHeaderPayloadAndDecompressedForComp0() {
    var body = Encoding.ASCII.GetBytes("Hello, uImage!");
    var data = BuildUImage(body, comp: 0, name: "hello");

    using var ms = new MemoryStream(data);
    var entries = new UImageFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("header.bin"));
    Assert.That(names, Does.Contain("payload.bin"));
    Assert.That(names, Does.Contain("payload_decompressed.bin"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_SkipsDecompressedEntryForCompressedComp() {
    var body = new byte[32];
    var data = BuildUImage(body, comp: 1 /* gzip */);

    using var ms = new MemoryStream(data);
    var names = new UImageFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("payload.bin"));
    Assert.That(names, Does.Not.Contain("payload_decompressed.bin"));
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsWrongMagic() {
    var data = new byte[UImageReader.HeaderSize];
    Assert.That(() => UImageReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Reader_DetectsHeaderCorruption() {
    var body = new byte[16];
    var data = BuildUImage(body);
    data[8] ^= 0xFF; // flip a byte in the timestamp after CRC has been computed
    var img = UImageReader.Read(data);
    Assert.That(img.HeaderCrc, Is.Not.EqualTo(img.ComputedHeaderCrc));
  }
}
