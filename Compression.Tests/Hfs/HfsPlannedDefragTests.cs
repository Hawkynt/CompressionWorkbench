#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Hfs;

namespace Compression.Tests.Hfs;

/// <summary>
/// The planner moves a classic HFS volume's files in place, and these are the
/// things that went wrong when it first did.
/// </summary>
/// <remarks>
/// The map used to describe only a file's first extent and only the catalog,
/// which left the second and third extents of a fragmented file and the whole
/// extents overflow file reading as free space — <c>hmount</c> answered "read
/// unallocated block" on a volume we had just defragmented. The alternate
/// master directory block was equally invisible, so an end-packed layout put a
/// file on top of the volume's own spare copy.
/// </remarks>
[TestFixture]
public class HfsPlannedDefragTests {

  private const int MdbOffset = 1024;
  private const int SectorSize = 512;

  /// <summary>A volume of six files with every other one removed and re-added.</summary>
  private static MemoryStream FragmentedVolume(out IReadOnlyList<(string Name, byte[] Data)> files) {
    var built = new List<(string Name, byte[] Data)>();
    var writer = new HfsWriter();
    for (var k = 0; k < 6; ++k) {
      var data = new byte[8 * 1024 + k * 1024];
      for (var i = 0; i < data.Length; ++i) data[i] = (byte)((i * 13 + k * 29) % 251);
      writer.AddFile($"F{k}", data);
      built.Add(($"F{k}", data));
    }

    var image = new MemoryStream();
    var bytes = writer.Build();
    image.Write(bytes, 0, bytes.Length);

    var descriptor = new HfsFormatDescriptor();
    image.Position = 0;
    descriptor.Remove(image, ["F1", "F3"]);
    image.Position = 0;
    descriptor.Add(image, [
      new ArchiveInputInfo(InlineSource(built[1].Data), "F1", false),
      new ArchiveInputInfo(InlineSource(built[3].Data), "F3", false)]);

    files = built;
    return image;
  }

  /// <summary>Writes a payload to a scratch file, since Add takes paths.</summary>
  private static string InlineSource(byte[] data) {
    var path = Path.Combine(_Scratch.Value, "cwb_hfs_" + Guid.NewGuid().ToString("N")[..8]);
    File.WriteAllBytes(path, data);
    return path;
  }

  // One directory per fixture run, removed in OneTimeTearDown. These used to be written straight
  // into the temp directory and never deleted; on a tmpfs /tmp that is leaked RAM, and a run left
  // 608 of them behind.
  private static readonly Lazy<string> _Scratch = new(() => {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_hfs_scratch_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
  });

  [OneTimeTearDown]
  public void RemoveScratchDirectory() {
    if (!_Scratch.IsValueCreated)
      return;

    try { Directory.Delete(_Scratch.Value, recursive: true); }
    catch { /* scratch cleanup is best-effort. */ }
  }

  [Test]
  public void ExtentMap_CoversTheExtentsOverflowFileAndTheAlternateMdb() {
    using var image = FragmentedVolume(out _);
    var descriptor = new HfsFormatDescriptor();
    image.Position = 0;
    var extents = descriptor.EnumerateExtents(image).ToList();

    image.Position = MdbOffset;
    var mdb = new byte[SectorSize];
    image.ReadExactly(mdb);
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(20));
    var allocationBase = (long)BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(28)) * SectorSize;
    var overflowStart = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(134));
    var overflowBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(136));
    Assert.That(overflowBlocks, Is.GreaterThan(0), "the volume must have an extents overflow file to cover");

    var overflowAt = allocationBase + (long)overflowStart * blockSize;
    Assert.That(extents.Any(e => e.Kind != DefragBlockKind.Free
        && e.Offset <= overflowAt && overflowAt < e.Offset + e.Length),
      "the extents overflow file must be described, or a layout will write over it");

    var alternateMdb = (image.Length / SectorSize - 2) * SectorSize;
    Assert.That(extents.Any(e => e.Kind != DefragBlockKind.Free
        && e.Offset <= alternateMdb && alternateMdb < e.Offset + e.Length),
      "the alternate master directory block must be described");
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEverySystemFileAndEveryPayload(DefragMode mode) {
    using var image = FragmentedVolume(out var files);
    var descriptor = new HfsFormatDescriptor();

    image.Position = MdbOffset;
    var mdb = new byte[SectorSize];
    image.ReadExactly(mdb);
    var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(mdb.AsSpan(20));
    var allocationBase = (long)BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(28)) * SectorSize;
    var overflowStart = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(134));
    var overflowBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(136));
    var overflowAt = allocationBase + (long)overflowStart * blockSize;
    var overflowLength = overflowBlocks * blockSize;
    image.Position = overflowAt;
    var overflowBefore = new byte[overflowLength];
    image.ReadExactly(overflowBefore);

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = mode });

    image.Position = 0;
    var reader = new HfsReader(image);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(
        e => !e.IsDirectory && e.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the catalog");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }

    // Whatever moved, the extents overflow file is where the volume says it is
    // and holds what it held.
    image.Position = MdbOffset;
    image.ReadExactly(mdb);
    var overflowStartAfter = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(134));
    var overflowAtAfter = allocationBase + (long)overflowStartAfter * blockSize;
    image.Position = overflowAtAfter;
    var overflowAfter = new byte[overflowLength];
    image.ReadExactly(overflowAfter);
    Assert.That(overflowAfter, Is.EqualTo(overflowBefore),
      "the extents overflow file must survive the move");

    // The free count the MDB carries is a number of its own, and a stale one
    // makes a sound volume read as corrupt.
    var freeBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(34));
    var totalBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(18));
    var bitmapBase = (long)BinaryPrimitives.ReadUInt16BigEndian(mdb.AsSpan(14)) * SectorSize;
    var counted = 0;
    for (var block = 0; block < totalBlocks; ++block) {
      image.Position = bitmapBase + block / 8;
      var b = image.ReadByte();
      if (b >= 0 && (b & (1 << (7 - block % 8))) == 0) ++counted;
    }

    Assert.That(freeBlocks, Is.EqualTo(counted),
      "the MDB's free block count must agree with the bitmap");
  }
}
