#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Xfs;

namespace Compression.Tests.Xfs;

/// <summary>
/// The log and the inode chunks are part of the volume, and the map has to say
/// so.
/// </summary>
/// <remarks>
/// It described the first eight blocks of each allocation group, the root
/// inode, the directory blocks and each file's extents — and nothing else. The
/// log starts far past those eight blocks and the inode chunks further still,
/// so both read as free space: a wipe would zero the log and every inode in
/// the volume, and a layout would put a file on top of them.
/// </remarks>
[TestFixture]
public class XfsMapCoversLogAndInodesTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume() {
    var work = Path.Combine(Path.GetTempPath(), "cwb_xfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 4; ++k) {
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, Payload(k, 40000 + k * 12000));
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
      }

      var image = new MemoryStream();
      new XfsFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test]
  public void ExtentMap_ClaimsTheLog() {
    using var image = Volume();
    var raw = image.ToArray();

    // The superblock says where the log is: a block address in the first field
    // and a length in blocks in the second.
    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4));
    var logStart = BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(48));
    var logBlocks = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(96));
    Assert.That(logBlocks, Is.GreaterThan(0), "the probe volume must have a log to protect");

    var agBlockLog = raw[124];
    var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(84));
    var ag = (long)(logStart >> agBlockLog);
    var withinAg = (long)(logStart & ((1UL << agBlockLog) - 1));
    var logOffset = (ag * agBlocks + withinAg) * blockSize;

    image.Position = 0;
    var extents = new XfsFormatDescriptor().EnumerateExtents(image).ToList();
    Assert.That(extents.Any(e => e.Kind != DefragBlockKind.Free
        && e.Offset <= logOffset && logOffset < e.Offset + e.Length),
      "the log must be described, or a wipe zeroes it");
  }

  [Test]
  public void ExtentMap_ClaimsTheInodeChunks() {
    using var image = Volume();
    image.Position = 0;
    var extents = new XfsFormatDescriptor().EnumerateExtents(image).ToList();

    // Every inode the map names sits inside a chunk, and the chunk is what the
    // volume allocates — so each inode's bytes must fall inside a claimed run
    // that is bigger than the inode itself.
    var inodes = extents.Where(e => e.FileName != null && e.FileName.StartsWith("inode:", StringComparison.Ordinal)).ToList();
    Assert.That(inodes, Is.Not.Empty, "the probe volume must have file inodes");

    var chunks = extents.Where(e => e.FileName != null && e.FileName.Contains("inode chunk", StringComparison.Ordinal)).ToList();
    Assert.That(chunks, Is.Not.Empty, "the inode chunks must be described");

    foreach (var inode in inodes)
      Assert.That(chunks.Any(c => c.Offset <= inode.Offset && inode.Offset + inode.Length <= c.Offset + c.Length),
        $"the inode at {inode.Offset} must sit inside a claimed chunk");
  }

  [Test, Category("RoundTrip")]
  public void WipeUnusedSpace_LeavesTheLogAndTheInodesAlone() {
    using var image = Volume();
    var before = image.ToArray();

    image.Position = 0;
    new XfsFormatDescriptor().WipeUnusedSpace(image, wipeClusterTips: true, wipeDeletedEntries: true);
    var after = image.ToArray();

    image.Position = 0;
    foreach (var region in new XfsFormatDescriptor().EnumerateExtents(image)
               .Where(e => e.Kind == DefragBlockKind.MetadataReserved)) {
      var at = (int)region.Offset;
      var length = (int)Math.Min(region.Length, before.Length - region.Offset);
      Assert.That(after.AsSpan(at, length).ToArray(), Is.EqualTo(before.AsSpan(at, length).ToArray()),
        $"the structure at {region.Offset} must survive a wipe");
    }
  }
}
