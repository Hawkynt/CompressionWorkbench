using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.LittleFs;

[TestFixture]
public class LittleFsTests {

  /// <summary>
  /// Synthesize a LittleFS-shaped first metadata block: u32 revision at offset 0,
  /// "littlefs" ASCII at a fixed offset, followed by the inline-struct fields the
  /// parser cracks (version u32, block_size u32, block_count u32, name_max u32,
  /// file_max u32, attr_max u32 — all LE).
  /// </summary>
  private static byte[] BuildMinimal(uint blockSize = 4096, uint blockCount = 64) {
    var image = new byte[blockSize * 4];
    // block revision
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0, 4), 1);
    // "littlefs" signature at a typical offset within the metadata block.
    var sigOffset = 16;
    Encoding.ASCII.GetBytes("littlefs").CopyTo(image.AsSpan(sigOffset));
    var payload = sigOffset + 8;
    // version 2.1 → high word major, low word minor.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(payload + 0, 4), (2u << 16) | 1u);
    // block_size, block_count, name_max, file_max, attr_max
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(payload + 4, 4), blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(payload + 8, 4), blockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(payload + 12, 4), 255);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(payload + 16, 4), 0x7FFFFFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(payload + 20, 4), 1022);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.LittleFs.LittleFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("LittleFs"));
    Assert.That(d.DisplayName, Is.EqualTo("LittleFS"));
    Assert.That(d.Extensions, Does.Contain(".littlefs"));
    Assert.That(d.Extensions, Does.Contain(".lfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    foreach (var sig in d.MagicSignatures) {
      Assert.That(sig.Offset, Is.GreaterThanOrEqualTo(0));
      Assert.That(sig.Confidence, Is.InRange(0.0, 1.0));
    }
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.LittleFs.LittleFsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.littlefs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedSuperblock() {
    var img = BuildMinimal(blockSize: 4096, blockCount: 128);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.LittleFs.LittleFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "littlefs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.littlefs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("version_major=2"));
      Assert.That(meta, Does.Contain("version_minor=1"));
      Assert.That(meta, Does.Contain("block_size=4096"));
      Assert.That(meta, Does.Contain("block_count=128"));
      Assert.That(meta, Does.Contain("name_max=255"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[2048]);
    var d = new FileSystem.LittleFs.LittleFsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.littlefs"));
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyImage_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[8]);
    var d = new FileSystem.LittleFs.LittleFsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
  }
}
