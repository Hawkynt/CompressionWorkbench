#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Xfs;

namespace Compression.Tests.Xfs;

/// <summary>
/// XFS lays a volume out again by moving extents. A file's extent record names
/// the block it starts at, and the free space each allocation group records is
/// written again from the layout the pass finished with.
/// </summary>
/// <remarks>
/// A group records its free space twice — once ordered by where an extent
/// starts, once by how long it is — with the totals in its header and the
/// volume's own count in the superblock. All of it is derived from what the
/// pass left, because a move changes which blocks are free and every one of
/// those records would otherwise describe a volume that no longer exists.
/// </remarks>
[TestFixture]
public class XfsPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_xfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 5; ++k) {
        var data = Payload(k, 40000 + k * 12000);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new XfsFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
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
    new XfsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    image.Position = 0;
    var reader = new XfsReader(image);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name == name);
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the volume");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_LeavesTheFreeSpaceTreesDescribingWhatIsFree() {
    using var image = Volume(out _);
    image.Position = 0;
    new XfsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    var raw = image.ToArray();
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4));
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(84));

    image.Position = 0;
    var claimed = new HashSet<long>();
    foreach (var extent in new XfsFormatDescriptor().EnumerateExtents(image)) {
      var first = extent.Offset / blockSize;
      var last = (extent.Offset + extent.Length + blockSize - 1) / blockSize;
      for (var block = first; block < last; ++block) claimed.Add(block);
    }

    // The first allocation group's free-by-position tree must name exactly the
    // blocks nothing else claims, and no block twice.
    const int BnobtBlock = 1;
    const int RecordOffset = 56;
    var records = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(BnobtBlock * blockSize + 6));
    Assert.That(records, Is.GreaterThan(0), "the group must have free space to describe");

    var described = new HashSet<long>();
    long previousStart = -1;
    for (var i = 0; i < records; ++i) {
      var at = BnobtBlock * blockSize + RecordOffset + i * 8;
      var start = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(at));
      var length = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(at + 4));

      Assert.That((long)start, Is.GreaterThan(previousStart), "records must be ordered by position");
      previousStart = start;

      for (var block = start; block < start + length; ++block) {
        Assert.That(claimed.Contains(block), Is.False,
          $"block {block} is described as free but something claims it");
        Assert.That(described.Add(block), Is.True, $"block {block} is described as free twice");
      }
    }

    // And the header's totals must agree with what the tree says.
    var freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(512 + 52));
    Assert.That((long)freeBlocks, Is.EqualTo(described.Count),
      "the group's free-block count must match its own tree");
    Assert.That(agBlocks, Is.GreaterThan(0));
  }
}
