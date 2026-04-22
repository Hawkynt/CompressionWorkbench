using System.Buffers.Binary;

namespace Compression.Tests.Cso;

[TestFixture]
public class CsoTests {

  // Build a minimal synthetic CSO (or ZSO): 24-byte header, N+1 index entries, then fake block data.
  // uncompressed_size = 4096, block_size = 2048 => 2 blocks, 3 index entries.
  private static byte[] BuildSyntheticImage(bool isZso, byte[] block0, byte[] block1, bool block0Stored = false, bool block1Stored = false) {
    using var ms = new MemoryStream();
    ms.Write(isZso ? "ZISO"u8 : "CISO"u8);
    Span<byte> buf = stackalloc byte[8];
    // header_size = 0x18 (24)
    BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], 0x18u);
    ms.Write(buf[..4]);
    // uncompressed_size = 4096
    BinaryPrimitives.WriteUInt64LittleEndian(buf, 4096UL);
    ms.Write(buf);
    // block_size = 2048
    BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], 2048u);
    ms.Write(buf[..4]);
    // version, align, reserved[2]
    ms.WriteByte(1);
    ms.WriteByte(0);
    ms.WriteByte(0);
    ms.WriteByte(0);

    // Index table: 3 entries. Offsets point to byte positions in the file.
    var indexStart = (uint)ms.Position;
    var indexSize = 3u * 4u; // 12 bytes
    var block0Offset = indexStart + indexSize; // immediately after index
    var block1Offset = block0Offset + (uint)block0.Length;
    var endOffset = block1Offset + (uint)block1.Length;

    uint entry0 = block0Offset | (block0Stored ? 0x8000_0000u : 0u);
    uint entry1 = block1Offset | (block1Stored ? 0x8000_0000u : 0u);
    uint entry2 = endOffset;

    BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], entry0);
    ms.Write(buf[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], entry1);
    ms.Write(buf[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(buf[..4], entry2);
    ms.Write(buf[..4]);

    ms.Write(block0);
    ms.Write(block1);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Cso.CsoFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Cso"));
    Assert.That(d.Extensions, Contains.Item(".cso"));
    Assert.That(d.Extensions, Contains.Item(".ziso"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void List_SurfacesBlocksAndIndex() {
    var block0 = new byte[] { 0x10, 0x20, 0x30 };
    var block1 = new byte[] { 0xAA, 0xBB };
    var img = BuildSyntheticImage(isZso: false, block0, block1, block0Stored: false, block1Stored: true);

    var desc = new FileFormat.Cso.CsoFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = desc.List(ms, null);

    // FULL.cso, metadata.ini, index.bin, blocks (dir), block_00000.bin, block_00001.bin = 6 entries
    Assert.That(entries, Has.Count.EqualTo(6));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.cso"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
    Assert.That(entries[2].Name, Is.EqualTo("index.bin"));
    Assert.That(entries[2].OriginalSize, Is.EqualTo(12));
    Assert.That(entries[3].Name, Is.EqualTo("blocks"));
    Assert.That(entries[3].IsDirectory, Is.True);
    Assert.That(entries[4].Name, Is.EqualTo("blocks/block_00000.bin"));
    Assert.That(entries[4].OriginalSize, Is.EqualTo(block0.Length));
    Assert.That(entries[4].Method, Is.EqualTo("Deflate"));
    Assert.That(entries[5].Name, Is.EqualTo("blocks/block_00001.bin"));
    Assert.That(entries[5].OriginalSize, Is.EqualTo(block1.Length));
    Assert.That(entries[5].Method, Is.EqualTo("Stored"));
  }

  [Test, Category("HappyPath")]
  public void List_ZsoReportsLz4Method() {
    var img = BuildSyntheticImage(isZso: true, new byte[] { 1, 2 }, new byte[] { 3, 4 });
    var desc = new FileFormat.Cso.CsoFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = desc.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ziso"));
    Assert.That(entries[4].Method, Is.EqualTo("LZ4"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesBlocksAndMetadata() {
    var block0 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    var block1 = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
    var img = BuildSyntheticImage(isZso: false, block0, block1);

    var desc = new FileFormat.Cso.CsoFormatDescriptor();
    var tmp = Path.Combine(Path.GetTempPath(), "cso_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(img);
      desc.Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.cso")), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "FULL.cso")), Is.EqualTo(img));
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "index.bin")), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "blocks/block_00000.bin")), Is.EqualTo(block0));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "blocks/block_00001.bin")), Is.EqualTo(block1));
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("uncompressed_size=4096"));
      Assert.That(meta, Does.Contain("block_size=2048"));
      Assert.That(meta, Does.Contain("block_count=2"));
      Assert.That(meta, Does.Contain("is_zso=0"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_RejectsInvalidMagic() {
    var bogus = new byte[64];
    bogus[0] = (byte)'X';
    var desc = new FileFormat.Cso.CsoFormatDescriptor();
    using var ms = new MemoryStream(bogus);
    Assert.That(() => desc.List(ms, null), Throws.InstanceOf<InvalidDataException>());
  }
}
