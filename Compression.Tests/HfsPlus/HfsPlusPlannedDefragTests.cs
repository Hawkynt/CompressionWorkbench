#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// The planner moves an HFS+ volume's files in place, and these are the things
/// that went wrong when it first did.
/// </summary>
/// <remarks>
/// <c>fsck.hfsplus</c> called an end-packed volume corrupt over nothing but the
/// free block count, which the volume header carries as a number of its own and
/// which nothing updated once the bitmap had been settled. The extents overflow
/// file, the attributes file and the alternate volume header were not in the
/// map at all, so a layout was free to write over any of them.
/// </remarks>
[TestFixture]
public class HfsPlusPlannedDefragTests {

  private const int VolumeHeaderOffset = 1024;
  private const int VolumeHeaderSize = 512;

  private static MemoryStream FragmentedVolume(out IReadOnlyList<(string Name, byte[] Data)> files) {
    var built = new List<(string Name, byte[] Data)>();
    var writer = new HfsPlusWriter();
    for (var k = 0; k < 6; ++k) {
      var data = new byte[8 * 1024 + k * 1024];
      for (var i = 0; i < data.Length; ++i) data[i] = (byte)((i * 13 + k * 29) % 251);
      writer.AddFile($"F{k}.BIN", data);
      built.Add(($"F{k}.BIN", data));
    }

    var image = new MemoryStream();
    var bytes = writer.BuildAutoSized();
    image.Write(bytes, 0, bytes.Length);

    var descriptor = new HfsPlusFormatDescriptor();
    image.Position = 0;
    descriptor.Remove(image, ["F1.BIN", "F3.BIN"]);
    image.Position = 0;
    descriptor.Add(image, [
      new ArchiveInputInfo(Scratch(built[1].Data), "F1.BIN", false),
      new ArchiveInputInfo(Scratch(built[3].Data), "F3.BIN", false)]);

    files = built;
    return image;
  }

  private static string Scratch(byte[] data) {
    var path = Path.Combine(Path.GetTempPath(), "cwb_hfsplus_" + Guid.NewGuid().ToString("N")[..8]);
    File.WriteAllBytes(path, data);
    return path;
  }

  [Test]
  public void ExtentMap_CoversTheSystemFilesAndTheAlternateVolumeHeader() {
    using var image = FragmentedVolume(out _);
    var descriptor = new HfsPlusFormatDescriptor();
    image.Position = 0;
    var extents = descriptor.EnumerateExtents(image).ToList();

    image.Position = VolumeHeaderOffset;
    var header = new byte[VolumeHeaderSize];
    image.ReadExactly(header);
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(40));

    foreach (var (forkOffset, what) in new[] { (192, "extents overflow file"), (352, "attributes file") }) {
      var startBlock = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(forkOffset + 16));
      var blockCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(forkOffset + 20));
      if (blockCount == 0) continue;

      var at = (long)startBlock * blockSize;
      Assert.That(extents.Any(e => e.Kind != DefragBlockKind.Free
          && e.Offset <= at && at < e.Offset + e.Length),
        $"the {what} must be described, or a layout will write over it");
    }

    var alternate = image.Length - 1024;
    Assert.That(extents.Any(e => e.Kind != DefragBlockKind.Free
        && e.Offset <= alternate && alternate < e.Offset + e.Length),
      "the alternate volume header must be described");
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_LeavesTheFreeBlockCountAgreeingWithTheBitmap(DefragMode mode) {
    using var image = FragmentedVolume(out var files);
    var descriptor = new HfsPlusFormatDescriptor();

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = mode });

    image.Position = 0;
    var reader = new HfsPlusReader(image, leaveOpen: true);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(
        e => !e.IsDirectory && e.FullPath.EndsWith(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the catalog");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }

    image.Position = VolumeHeaderOffset;
    var header = new byte[VolumeHeaderSize];
    image.ReadExactly(header);
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(40));
    var totalBlocks = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(44));
    var freeBlocks = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(48));
    var bitmapBase = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(112 + 16)) * blockSize;

    var counted = 0;
    for (var block = 0; block < totalBlocks; ++block) {
      image.Position = bitmapBase + block / 8;
      var b = image.ReadByte();
      if (b >= 0 && (b & (1 << (7 - block % 8))) == 0) ++counted;
    }

    Assert.That(freeBlocks, Is.EqualTo(counted),
      "the volume header's free block count must agree with the bitmap fsck.hfsplus counts");
  }
}
