#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// APFS lays a container out again by moving blocks: a file's position is the
/// physical block in its file extent record, and every block carries a
/// Fletcher-64 over itself, so a move is the copy, eight bytes, and one leaf's
/// checksum taken again.
/// </summary>
/// <remarks>
/// The in-place modifier cannot be used for this. It rebuilds the trees and
/// allocates their new nodes from the image's tail, which grows the container —
/// and a layout pass must leave its size alone. The same tail-allocating habit
/// is why the container's own blocks are not all in front of the file data, and
/// why the map has to name them wherever they ended up.
/// </remarks>
[TestFixture]
public class ApfsPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> kept) {
    var writer = new ApfsWriter();
    var all = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var k = 0; k < 5; ++k) {
      var data = Payload(k, 6000 + k * 1500);
      writer.AddFile($"F{k}.BIN", data);
      all[$"F{k}.BIN"] = data;
    }

    var image = new MemoryStream();
    writer.BuildTo(image);

    // Punch holes: a container worth laying out again.
    image.Position = 0;
    new ApfsFormatDescriptor().Remove(image, ["F1.BIN", "F3.BIN"]);
    all.Remove("F1.BIN");
    all.Remove("F3.BIN");

    kept = all;
    return image;
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheContainersSize(DefragMode mode) {
    using var image = Volume(out var kept);
    var size = image.Length;

    image.Position = 0;
    new ApfsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a container keeps its size");

    image.Position = 0;
    var reader = new ApfsReader(image);
    foreach (var (name, data) in kept) {
      var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name == name);
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the volume");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  public void Defragment_LeavesEveryChecksumAndEveryTreeIntact(DefragMode mode) {
    using var image = Volume(out _);
    image.Position = 0;
    new ApfsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    // Every block's Fletcher-64 must still hold and every tree must still be
    // reachable — a repointed record whose leaf was not re-stamped fails here.
    var report = ApfsStructuralValidator.Validate(image.ToArray());
    Assert.That(report.Errors, Is.Empty,
      "the container must still check out after being laid out again");
  }

  [Test]
  public void ExtentMap_NamesTheContainersOwnBlocksWhereverTheyEndedUp() {
    using var image = Volume(out _);

    // Adding goes through the modifier, which allocates from the tail — so this
    // is what puts container structures past the file data.
    var added = Payload(9, 5000);
    var path = Path.Combine(Path.GetTempPath(), "cwb_apfs_" + Guid.NewGuid().ToString("N")[..8]);
    File.WriteAllBytes(path, added);
    try {
      image.Position = 0;
      new ApfsFormatDescriptor().Add(image, [new ArchiveInputInfo(path, "ADDED.BIN", false)]);
    } finally {
      File.Delete(path);
    }

    image.Position = 0;
    var extents = new ApfsFormatDescriptor().EnumerateExtents(image).ToList();
    var reserved = extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved).ToList();
    Assert.That(reserved, Is.Not.Empty, "the container must reserve its own structures");

    // Whatever the trees reach, the map must not offer as free space.
    image.Position = 0;
    var used = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    foreach (var block in reserved)
      foreach (var file in used)
        Assert.That(block.Offset < file.Offset + file.Length && file.Offset < block.Offset + block.Length,
          Is.False, "a reserved block and a file may not claim the same bytes");
  }
}
