#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// ZFS lays a pool out again by moving blocks. A block pointer holds the
/// address in sectors and a Fletcher-4 over the bytes it points at — bytes a
/// move does not change, so that check survives and every check above it does
/// not.
/// </summary>
/// <remarks>
/// The reader verifies Fletcher-4 on every block it traverses, so a payload
/// that reads back at all is a chain of checks that holds all the way from the
/// uberblock down. That is what makes these tests worth something: they do not
/// inspect the checksums, they rely on the reader refusing anything wrong.
/// </remarks>
[TestFixture]
public class ZfsPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Pool(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_zfs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 4; ++k) {
        var data = Payload(k, 30000 + k * 12000);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new ZfsFormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndThePoolsSize(DefragMode mode) {
    using var image = Pool(out var files);
    var size = image.Length;

    image.Position = 0;
    new ZfsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a pool keeps its size");

    // Every block on the way to a payload has its Fletcher-4 verified as it is
    // read, so this passing means the whole chain was taken again correctly.
    image.Position = 0;
    using var reader = new ZfsReader(image, leaveOpen: true);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(e => e.Name == name);
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the pool");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_ActuallyMovesTheBlocks() {
    using var image = Pool(out _);
    var descriptor = new ZfsFormatDescriptor();

    image.Position = 0;
    var before = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();
    Assert.That(before, Is.Not.Empty, "the probe pool must have data blocks to move");

    image.Position = 0;
    descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var after = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset).OrderBy(o => o).ToList();

    Assert.That(after, Is.Not.EqualTo(before), "packing against the tail must move something");
    Assert.That(after.Max(), Is.GreaterThan(before.Max()), "and it must move towards the tail");
  }

  [Test]
  public void ExtentMap_KeepsFilesAndStructureApart() {
    using var image = Pool(out var files);
    image.Position = 0;
    var extents = new ZfsFormatDescriptor().EnumerateExtents(image).ToList();

    Assert.That(extents, Is.Not.Empty, "a pool that describes nothing reads as entirely free");
    foreach (var name in files.Keys)
      Assert.That(extents.Any(e => e.FileName == name), $"{name}'s blocks must be claimed");

    var used = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    var structure = extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved).ToList();
    foreach (var file in used)
      foreach (var block in structure)
        Assert.That(file.Offset < block.Offset + block.Length && block.Offset < file.Offset + file.Length,
          Is.False, "a file and the pool's own structure may not claim the same bytes");
  }
}
