using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.MinixV1;

namespace Compression.Tests.MinixV1;

/// <summary>
/// Verifies the Minix v1 creation-option schema: the published NameLength knob
/// is real (it flips the superblock magic between the 14-byte 0x137F and the
/// 30-byte 0x138F variant) and files still round-trip through the reader.
/// </summary>
[TestFixture]
public class MinixV1SchemaTests {

  // s_magic at superblock offset 1024 + 16 == file offset 1040.
  private const int MagicOffset = 1040;

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesNameLengthSchema() {
    var desc = (IFormatOptionsSchema)new MinixV1FormatDescriptor();
    Assert.That(desc.OptionsSchema.Select(o => o.Key), Does.Contain("NameLength"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNameLength30_SetsLongNameMagicAndRoundTrips() {
    var desc = new MinixV1FormatDescriptor();
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["NameLength"] = "30" },
    };

    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("a-longer-file-name.txt", "minix-v1"u8.ToArray())], opts);
    var image = ms.ToArray();

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(MagicOffset, 2));
    Assert.That(magic, Is.EqualTo(0x138F), "NameLength=30 must select the 0x138F magic.");

    using var rs = new MemoryStream(image);
    var entries = desc.List(rs, null);
    Assert.That(entries.Any(e => e.Name == "a-longer-file-name.txt"), Is.True,
      "the >14-char name must survive at NameLength=30.");
  }

  [Test, Category("HappyPath")]
  public void Create_DefaultNameLength14_UsesShortNameMagic() {
    var desc = new MinixV1FormatDescriptor();
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("a.txt", "x"u8.ToArray())], new FormatCreateOptions());
    var image = ms.ToArray();
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(MagicOffset, 2));
    Assert.That(magic, Is.EqualTo(0x137F));
  }
}
