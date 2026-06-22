using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ubifs;

namespace Compression.Tests.Ubifs;

/// <summary>
/// Schema-wiring coverage for <see cref="UbifsFormatDescriptor"/>: verifies the
/// published LebSize knob and that Create() writes it into the superblock leb_size
/// field and LEB-aligns the image, with file round-trip.
/// </summary>
[TestFixture]
public class UbifsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesLebSizeSchema() {
    var desc = new UbifsFormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("LebSize"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultLebSize_StampsSuperblockAndRoundTrips() {
    var desc = new UbifsFormatDescriptor();
    var payload = "ubifs content"u8.ToArray();

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["LebSize"] = "128 KB" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("note.txt", payload)], opts);
    var image = ms.ToArray();

    // Superblock node is at offset 0; leb_size lives at common-header(24) + 8 = 32.
    var lebSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(32, 4));
    Assert.That(lebSize, Is.EqualTo(128u * 1024), "non-default 128 KB LEB size must reach the superblock.");
    Assert.That(image.Length % (128 * 1024), Is.EqualTo(0), "image must be LEB-aligned to 128 KB.");

    using var rs = new MemoryStream(image);
    var back = desc.ExtractEntryToMemory(rs, "note.txt", null);
    Assert.That(back, Is.EqualTo(payload));
  }
}
