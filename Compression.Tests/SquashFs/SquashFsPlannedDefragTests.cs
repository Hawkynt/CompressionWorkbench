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

  /// <summary>
  /// The times every inode of the probe image carries. They are part of the deflated inode
  /// table, so whether the re-packed table still fits depends on them: pinned, every run
  /// judges the same image. Built from the clock, about three seconds in a thousand made an
  /// image whose table no longer fit once the data moved to the tail.
  /// </summary>
  private static readonly DateTime FixtureTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>A time at which the probe image's inode table does not pack back after a move to the tail.</summary>
  private static readonly DateTime UnfittingTime = DateTime.UnixEpoch.AddSeconds(0x697F2E00);

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) => Volume(FixtureTime, out files);

  private static MemoryStream Volume(DateTime time, out Dictionary<string, byte[]> files) {
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var image = new MemoryStream();
    using (var writer = new SquashFsWriter(image, leaveOpen: true))
      for (var k = 0; k < 5; ++k) {
        var data = Payload(k, 9000 + k * 4000);
        writer.AddFile($"F{k}.BIN", data, time);
        files[$"F{k}.BIN"] = data;
      }

    return image;
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

  [Test, Category("EdgeCase")]
  public void GivenAnInodeTableThatNoLongerFits_WhenConsolidatedAtTheEnd_ThenItIsRefusedAndTheImageIsUnchanged() {
    using var image = Volume(UnfittingTime, out _);
    var before = image.ToArray();

    image.Position = 0;
    Assert.Throws<NotSupportedException>(() => new SquashFsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd }));
    Assert.That(image.ToArray(), Is.EqualTo(before), "a refused pass leaves the image byte for byte");
  }

  [Test, Category("HappyPath")]
  public void GivenTheSameFilesAndTimes_WhenWrittenTwice_ThenTheImagesAreIdentical() {
    using var first = Volume(out _);
    Thread.Sleep(1100);
    using var second = Volume(out _);
    Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()),
      "a directory without a time of its own takes its newest file's, not the clock's");
  }
}
