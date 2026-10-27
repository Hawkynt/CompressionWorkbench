using System.Buffers.Binary;
using FileFormat.Vmdk;

namespace Compression.Tests.Vmdk;

[TestFixture]
public class VmdkSparseWriteTests {
  [TestCase(false), TestCase(true), Category("ErrorHandling")]
  public void InvalidRedundantMetadata_IsRefusedBeforeEditing(bool invalidTable) {
    var writer = new VmdkWriter();
    writer.SetDiskData(new byte[128 * 512]);
    var bytes = writer.Build();
    if (invalidTable) {
      var directory = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(48)) * 512);
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory), uint.MaxValue);
    } else
      BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), ulong.MaxValue);

    using var input = new MemoryStream(bytes);
    Assert.That(VmdkStream.TryOpen(input), Is.Null);
  }

  [TestCase(false), TestCase(true), Category("RoundTrip")]
  public void AllocatedGrains_AreVisibleThroughBothDirectories(bool missingTables) {
    const int grainSize = 128 * 512;
    var expected = new byte[grainSize * 2];
    var writer = new VmdkWriter();
    writer.SetDiskData(expected);
    using var backing = new MemoryStream();
    backing.Write(writer.Build());
    if (missingTables) {
      var header = backing.ToArray();
      foreach (var field in new[] { 48, 56 }) {
        backing.Position = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(field)) * 512);
        backing.Write(new byte[4]);
      }
    }

    var payload = "newly allocated sparse grain"u8.ToArray();
    using (var disk = VmdkStream.TryOpen(backing)) {
      Assert.That(disk, Is.Not.Null);
      disk!.Position = grainSize + 17;
      disk.Write(payload);
      disk.Flush();
    }
    payload.CopyTo(expected, grainSize + 17);

    foreach (var useRedundant in new[] { false, true }) {
      var bytes = backing.ToArray();
      var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
      BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), useRedundant ? flags | 2u : flags & ~2u);
      using var input = new MemoryStream(bytes);
      var reader = new VmdkReader(input);
      Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(expected),
        $"Allocated data must survive reading through the {(useRedundant ? "redundant" : "primary")} directory.");
    }
  }
}
