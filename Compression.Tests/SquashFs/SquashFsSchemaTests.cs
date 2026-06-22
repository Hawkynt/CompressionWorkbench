using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.SquashFs;

namespace Compression.Tests.SquashFs;

/// <summary>
/// Schema-wiring coverage for <see cref="SquashFsFormatDescriptor"/>: verifies the
/// published BlockSize knob and that Create() honours a non-default block size by
/// inspecting the superblock block_size / block_log fields, with file round-trip.
/// </summary>
[TestFixture]
public class SquashFsSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_PublishesBlockSizeSchema() {
    var desc = new SquashFsFormatDescriptor();
    Assert.That(desc, Is.AssignableTo<IFormatOptionsSchema>());
    var keys = ((IFormatOptionsSchema)desc).OptionsSchema.Select(o => o.Key).ToList();
    Assert.That(keys, Does.Contain("BlockSize"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithNonDefaultBlockSize_StampsSuperblockAndRoundTrips() {
    var desc = new SquashFsFormatDescriptor();
    var payload = new byte[200_000];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);

    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "4 KB" },
    };
    using var ms = new MemoryStream();
    desc.Create(ms, [ArchiveInputInfo.InMemory("data.bin", payload)], opts);
    var image = ms.ToArray();

    // Superblock: block_size at +12 (u32), block_log at +22 (u16).
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(12, 4));
    var blockLog = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(22, 2));
    Assert.That(blockSize, Is.EqualTo(4096u), "non-default 4 KB block size must reach the superblock.");
    Assert.That(blockLog, Is.EqualTo((ushort)12), "block_log must equal log2(block_size).");

    using var rs = new MemoryStream(image);
    var back = desc.ExtractEntryToMemory(rs, "data.bin", null);
    Assert.That(back, Is.EqualTo(payload));
  }
}
