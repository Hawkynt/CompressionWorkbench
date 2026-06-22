using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ext1;

namespace Compression.Tests.Ext1;

/// <summary>
/// Verifies the ext1 creation-option schema: the published BlockSize knob is
/// real (it changes the superblock s_log_block_size) and the files still
/// round-trip through the descriptor's reader at the non-default block size.
/// </summary>
[TestFixture]
public class Ext1SchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesBlockSizeSchema() {
    var desc = (IFormatOptionsSchema)new Ext1FormatDescriptor();
    Assert.That(desc.OptionsSchema.Select(o => o.Key), Does.Contain("BlockSize"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithBlockSize4096_SetsLogBlockSizeAndRoundTrips() {
    var desc = new Ext1FormatDescriptor();
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "4096" },
    };

    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("hello.txt", "ext1-block-size"u8.ToArray())], opts);
    var image = ms.ToArray();

    // s_log_block_size at superblock offset 1024 + 24; blockSize = 1024 << v.
    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 24, 4));
    Assert.That(1024 << (int)logBlockSize, Is.EqualTo(4096), "BlockSize=4096 must select s_log_block_size=2.");

    using var rs = new MemoryStream(image);
    var entries = desc.List(rs, null);
    Assert.That(entries.Any(e => e.Name == "hello.txt"), Is.True, "file must round-trip at the 4 KiB block size.");
  }

  [Test, Category("HappyPath")]
  public void Create_DefaultBlockSizeIs1024() {
    var desc = new Ext1FormatDescriptor();
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    var image = ms.ToArray();
    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 24, 4));
    Assert.That(1024 << (int)logBlockSize, Is.EqualTo(1024));
  }
}
