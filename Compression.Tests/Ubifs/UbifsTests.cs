using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Ubifs;

[TestFixture]
public class UbifsTests {

  // Minimal UBIFS node: 24-byte common header + padding.
  //   magic(4) = 0x06101831
  //   crc(4)
  //   sqnum(8)
  //   len(4)   = total node length
  //   type(1)
  //   group_type(1)
  //   pad(2)
  private static void WriteNodeCommon(Span<byte> buf, byte type, uint totalLen, ulong sqnum = 1) {
    BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(0, 4), 0x06101831u);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(4, 4), 0xDEADBEEFu);
    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(8, 8), sqnum);
    BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(16, 4), totalLen);
    buf[20] = type;
    buf[21] = 0;
    buf[22] = 0;
    buf[23] = 0;
  }

  /// <summary>Build a tiny fixture: superblock node (type 6) + one inode (type 0) + one dentry (type 2).</summary>
  private static byte[] BuildMinimal() {
    // Lay out at LEB boundaries (32768) so the LEB-size auto-detection has something to chew on.
    const int leb = 32768;
    var image = new byte[3 * leb];

    // Superblock at LEB 0.
    WriteNodeCommon(image.AsSpan(0, 24), type: 6, totalLen: 24);

    // Inode at LEB 1.
    // Layout: common(24) + key(16) + creat_sqnum(8) + size(8) + ... (need >= 24+80+4 to read flags)
    const int inodeTotal = 24 + 16 + 8 + 8 + 3 * 8 + 3 * 4 + 4 * 4 + 4; // up to and including flags
    WriteNodeCommon(image.AsSpan(leb, 24), type: 0, totalLen: (uint)inodeTotal);
    // inode number at key[0..8]
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(leb + 24, 8), 42);
    // size at key(16) + creat_sqnum(8) => offset 24 + 16 + 8 = 48 relative to node start
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(leb + 24 + 16 + 8, 8), 1234);
    // flags at offset 24 + 80 = 104
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(leb + 24 + 80, 4), 0x5);

    // Dentry at LEB 2.
    // Layout: common(24) + key(16) + inum(8) + pad(1) + type(1) + nlen(2) + name
    var nameBytes = Encoding.UTF8.GetBytes("hello.txt");
    var dentryTotal = 24 + 16 + 8 + 4 + nameBytes.Length;
    WriteNodeCommon(image.AsSpan(2 * leb, 24), type: 2, totalLen: (uint)dentryTotal);
    // parent inode
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(2 * leb + 24, 8), 1);
    // target inode
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(2 * leb + 24 + 16, 8), 42);
    // type byte
    image[2 * leb + 24 + 16 + 8 + 1] = 1;
    // name length
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(2 * leb + 24 + 16 + 8 + 2, 2), (ushort)nameBytes.Length);
    // name
    nameBytes.CopyTo(image.AsSpan(2 * leb + 24 + 16 + 8 + 4));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Ubifs.UbifsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ubifs"));
    Assert.That(d.Extensions, Does.Contain(".ubifs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures[0].Confidence, Is.LessThanOrEqualTo(0.35));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ubifs.UbifsFormatDescriptor();

    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.ubifs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("inodes.txt"));
    Assert.That(names, Does.Contain("dentries.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFiles() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ubifs.UbifsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ubifs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "FULL.ubifs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("total_nodes="));
      Assert.That(meta, Does.Contain("superblock_found=True"));

      Assert.That(File.Exists(Path.Combine(outDir, "dentries.txt")), Is.True);
      var dentries = File.ReadAllText(Path.Combine(outDir, "dentries.txt"));
      Assert.That(dentries, Does.Contain("hello.txt"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore cleanup races */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyInput_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[16]);
    var d = new FileSystem.Ubifs.UbifsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.ubifs").And.Contain("metadata.ini"));
  }
}
