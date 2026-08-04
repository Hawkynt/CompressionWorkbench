#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.SquashFs;

namespace Compression.Tests.SquashFs;

/// <summary>
/// SquashFS lays an image out again by moving data blocks, even though the
/// field naming them lives inside a deflated metadata block.
/// </summary>
/// <remarks>
/// The block is taken apart, changed and packed again once the pass is over,
/// and it has to come back no longer than it was: a block's length is its own
/// header, and every table after it is found by an offset in the superblock, so
/// one that grew would move all of them. One that shrinks is padded back, which
/// a deflate stream tolerates because it ends where its own final block ends. A
/// block that will not fit is refused and the image goes through the rebuild.
/// </remarks>
[TestFixture]
public class SquashFsPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_sqfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 5; ++k) {
        var data = Payload(k, 9000 + k * 4000);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new SquashFsFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  private static Dictionary<string, byte[]> ReadBack(MemoryStream image) {
    image.Position = 0;
    var reader = new SquashFsReader(image, leaveOpen: true);
    return reader.Entries
      .Where(e => !e.IsDirectory && !e.IsSymlink)
      .ToDictionary(e => Path.GetFileName(e.FullPath), reader.Extract, StringComparer.Ordinal);
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheImagesSize(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new SquashFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "an image keeps its size");

    var read = ReadBack(image);
    foreach (var (name, data) in files) {
      Assert.That(read.Keys, Does.Contain(name), $"{name} must still be in the image");
      Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_ActuallyMovesTheData() {
    using var image = Volume(out _);
    var descriptor = new SquashFsFormatDescriptor();

    image.Position = 0;
    var before = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();
    Assert.That(before, Is.Not.Empty, "the probe image must have data to move");

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var after = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();

    Assert.That(after, Is.Not.EqualTo(before), "packing against the tail must move something");
    Assert.That(after.Max(), Is.GreaterThan(before.Max()), "and it must move towards the tail");
  }

  [Test]
  public void Defragment_LeavesTheTablesWhereTheSuperblockSaysTheyAre() {
    using var image = Volume(out _);
    var before = image.ToArray();

    image.Position = 0;
    new SquashFsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });
    var after = image.ToArray();

    // The inode, directory, fragment and id tables are all found by offsets in
    // the superblock; a metadata block that grew would have moved them.
    for (var field = 0x30; field <= 0x58; field += 8)
      Assert.That(BitConverter.ToInt64(after, field), Is.EqualTo(BitConverter.ToInt64(before, field)),
        $"the table offset at {field:X} must not move");
  }
}
