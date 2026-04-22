using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Bfs;

[TestFixture]
public class BfsTests {

  /// <summary>Build a minimal BFS image: name + magic1 at sector 1 (offset 512) + plausible sizes.</summary>
  private static byte[] BuildMinimal(int superblockOffset = 512) {
    var image = new byte[superblockOffset + 2048];
    // name at offset 0
    Encoding.ASCII.GetBytes("testvol").CopyTo(image.AsSpan(superblockOffset));
    // magic1 '1SFB' at offset 32
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 32, 4), 0x42465331u);
    // fs_byte_order at 36
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 36, 4), 0x42494745u);
    // block_size at 40 = 2048
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 40, 4), 2048);
    // block_shift at 44 = 11
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 44, 4), 11);
    // num_blocks at 48 = 128
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 48, 8), 128);
    // used_blocks at 56 = 10
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 56, 8), 10);
    // inode_size at 64 = 1024
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 64, 4), 1024);
    // magic2 at 68
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 68, 4), 0xDD121031u);
    // blocks_per_ag at 72
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 72, 4), 128);
    // num_ags at 80
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 80, 4), 1);
    // magic3 at 112
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 112, 4), 0x15B6830Eu);
    // root_dir_ino at 116
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 116, 8), 1);
    // indices_dir_ino at 124
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 124, 8), 2);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Bfs"));
    Assert.That(d.Extensions, Does.Contain(".bfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Confidence, Is.LessThanOrEqualTo(0.35));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface_AtOffset512() {
    var img = BuildMinimal(512);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface_AtOffset0() {
    var img = BuildMinimal(0);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bfs"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFiles() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "bfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "FULL.bfs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);

      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("block_size=2048"));
      Assert.That(meta, Does.Contain("num_blocks=128"));
      Assert.That(meta, Does.Contain("magic1_ok=True"));
      Assert.That(meta, Does.Contain("magic3_ok=True"));

      var sb = File.ReadAllBytes(Path.Combine(outDir, "superblock.bin"));
      Assert.That(sb.Length, Is.EqualTo(1024));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[2048]);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.bfs"));
  }
}
