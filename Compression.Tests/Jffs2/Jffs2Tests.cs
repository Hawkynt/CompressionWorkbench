using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Jffs2;

[TestFixture]
public class Jffs2Tests {

  /// <summary>Build a minimal JFFS2 fixture: one cleanmarker + one inode + one dirent.</summary>
  private static byte[] BuildMinimal() {
    // Build each node into a list, then concatenate.
    var parts = new List<byte[]>();

    // Cleanmarker: 12 bytes.
    var cm = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(cm.AsSpan(0, 2), 0x1985);
    BinaryPrimitives.WriteUInt16LittleEndian(cm.AsSpan(2, 2), 0x2003);
    BinaryPrimitives.WriteUInt32LittleEndian(cm.AsSpan(4, 4), 12);
    parts.Add(cm);

    // Inode: 68 bytes fixed header is enough for our scanner.
    var ino = new byte[68];
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(0, 2), 0x1985);
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(2, 2), 0xE002);
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(4, 4), 68);
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(12, 4), 42); // ino
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(16, 4), 1);  // version
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(20, 4), 0x81A4); // mode 0644
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(24, 2), 1000); // uid
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(26, 2), 1001); // gid
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(28, 4), 12345); // isize
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(36, 4), 0x60000000); // mtime
    parts.Add(ino);

    // Dirent: header(40) + name.
    var nameBytes = Encoding.UTF8.GetBytes("README");
    var de = new byte[40 + nameBytes.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(de.AsSpan(0, 2), 0x1985);
    BinaryPrimitives.WriteUInt16LittleEndian(de.AsSpan(2, 2), 0xE001);
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(4, 4), (uint)(40 + nameBytes.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(12, 4), 1);   // pino
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(16, 4), 1);   // version
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(20, 4), 42);  // ino
    de[28] = (byte)nameBytes.Length; // nsize
    de[29] = 1; // type (regular)
    nameBytes.CopyTo(de.AsSpan(40));
    // Align to 4 for the next node.
    var pad = (4 - (de.Length % 4)) % 4;
    if (pad > 0) de = [.. de, .. new byte[pad]];
    parts.Add(de);

    var totalLen = parts.Sum(p => p.Length);
    var img = new byte[totalLen];
    var pos = 0;
    foreach (var p in parts) {
      p.CopyTo(img, pos);
      pos += p.Length;
    }
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Jffs2"));
    Assert.That(d.Extensions, Does.Contain(".jffs2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures[0].Confidence, Is.LessThanOrEqualTo(0.35));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.jffs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("dirents.txt"));
    Assert.That(names, Does.Contain("inodes.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFiles() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "jffs2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "FULL.jffs2")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("dirent_count=1"));
      Assert.That(meta, Does.Contain("inode_count=1"));
      Assert.That(meta, Does.Contain("cleanmarker_count=1"));

      var dirents = File.ReadAllText(Path.Combine(outDir, "dirents.txt"));
      Assert.That(dirents, Does.Contain("README"));
      Assert.That(dirents, Does.Contain("\t42\t"));

      var inodes = File.ReadAllText(Path.Combine(outDir, "inodes.txt"));
      Assert.That(inodes, Does.Contain("42\t1\t1000\t1001"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyInput_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[8]);
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.jffs2"));
  }
}
