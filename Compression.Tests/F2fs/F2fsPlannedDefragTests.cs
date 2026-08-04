#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// F2FS lays a volume out again by moving data blocks inside the region already
/// given over to file data.
/// </summary>
/// <remarks>
/// A block's address is one field, in the inode's array of them or in a node
/// below it. What reaches further are the two structures recording the same
/// fact: the segment information table's validity bitmaps and counts, and the
/// summary area that maps a block back to the node owning it. Both are keyed by
/// where a block sits, so both move with it — and a segment carries one type for
/// everything in it, which is why a pass stays inside the data region.
/// </remarks>
[TestFixture]
public class F2fsPlannedDefragTests {

  private const int SuperblockOffset = 1024;
  private const int SitEntrySize = 74;

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_f2fs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 5; ++k) {
        var data = Payload(k, 20000 + k * 9000);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new F2fsFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheVolumesSize(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new F2fsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    image.Position = 0;
    using var reader = new F2fsReader(image, leaveOpen: true);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith(name, StringComparison.Ordinal));
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the volume");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_LeavesTheSegmentTableCountingWhatIsThere() {
    using var image = Volume(out _);
    image.Position = 0;
    new F2fsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    var raw = image.ToArray();
    var blockSize = 1 << (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(SuperblockOffset + 16));
    var blocksPerSegment = 1 << (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(SuperblockOffset + 20));
    var segments = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(SuperblockOffset + 68));
    var sitBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(SuperblockOffset + 80));
    var mainBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(SuperblockOffset + 92));

    // Every segment's recorded count must equal the bits actually set in its
    // bitmap — that is the invariant fsck checks first, and the one a move
    // breaks if it clears an old bit and forgets the count.
    var entriesPerBlock = blockSize / SitEntrySize;
    for (var segment = 0; segment < segments; ++segment) {
      var at = (sitBlock + segment / entriesPerBlock) * blockSize + segment % entriesPerBlock * SitEntrySize;
      if (at + SitEntrySize > raw.Length) break;

      var packed = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at));
      var recorded = packed & 0x3FF;
      var counted = 0;
      for (var bit = 0; bit < blocksPerSegment; ++bit)
        if ((raw[at + 2 + bit / 8] & (1 << (7 - bit % 8))) != 0) ++counted;

      Assert.That(recorded, Is.EqualTo(counted),
        $"segment {segment} says {recorded} blocks are live and its bitmap says {counted}");
    }

    Assert.That(mainBlock, Is.GreaterThan(0), "the volume must have a main area");
  }

  [Test]
  public void Defragment_KeepsDataOutOfTheSegmentsMeantForNodes() {
    using var image = Volume(out _);
    var descriptor = new F2fsFormatDescriptor();

    image.Position = 0;
    var before = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).ToList();
    Assert.That(before, Is.Not.Empty, "the probe volume must have data blocks");

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    image.Position = 0;
    var nodes = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.MetadataReserved).ToList();
    var data = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).ToList();

    foreach (var block in data)
      foreach (var node in nodes)
        Assert.That(block.Offset < node.Offset + node.Length && node.Offset < block.Offset + block.Length,
          Is.False, "a data block and a node block may not claim the same bytes");
  }
}
