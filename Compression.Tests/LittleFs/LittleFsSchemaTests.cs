using Compression.Registry;
using FileSystem.LittleFs;

namespace Compression.Tests.LittleFs;

/// <summary>
/// Schema-wiring coverage for <see cref="LittleFsFormatDescriptor"/>: verifies the
/// published BlockSize knob and that Create() records it in the superblock geometry,
/// with file round-trip.
/// </summary>
[TestFixture]
public class LittleFsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesBlockSizeSchema() {
    var desc = new LittleFsFormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("BlockSize"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultBlockSize_StampsSuperblockAndRoundTrips() {
    var desc = new LittleFsFormatDescriptor();
    var payload = "littlefs round-trip payload"u8.ToArray();

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "8 KB" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("file.txt", payload)], opts);
    var image = ms.ToArray();

    var reader = new LittleFsReader(image);
    Assert.That(reader.BlockSize, Is.EqualTo(8u * 1024), "non-default 8 KB block size must reach the superblock.");

    var entry = reader.Files.Single(f => f.Path.EndsWith("file.txt", StringComparison.Ordinal));
    Assert.That(reader.ReadFile(entry), Is.EqualTo(payload));
  }
}
