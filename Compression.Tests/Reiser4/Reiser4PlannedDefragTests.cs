#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// Reiser4 lays a volume out again by moving blocks: a file starts where its
/// directory entry says and runs on from there, stepping over the allocator
/// bitmaps, so a move is the copy plus one field.
/// </summary>
/// <remarks>
/// The rule that makes it cheap is also what bounds it. Since the position is
/// implied, a file can only be put somewhere its blocks still read as one
/// sequence; the pass checks the layout against that before writing the
/// directory, and refuses rather than hand back a volume that reads as noise.
/// </remarks>
[TestFixture]
public class Reiser4PlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> kept) {
    var writer = new Reiser4Writer();
    var all = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var k = 0; k < 6; ++k) {
      var data = Payload(k, 12000 + k * 5000);
      writer.AddFile($"F{k}.BIN", data);
      all[$"F{k}.BIN"] = data;
    }

    var image = new MemoryStream();
    writer.Write(image);

    // Punch holes, then fill one of them: a volume worth defragmenting.
    var descriptor = (IArchiveModifiable)new Reiser4FormatDescriptor();
    image.Position = 0;
    descriptor.Remove(image, ["F1.BIN", "F3.BIN"]);
    all.Remove("F1.BIN");
    all.Remove("F3.BIN");

    kept = all;
    return image;
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheVolumesSize(DefragMode mode) {
    using var image = Volume(out var kept);
    var size = image.Length;

    image.Position = 0;
    new Reiser4FormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    image.Position = 0;
    using var reader = new Reiser4Reader(image, leaveOpen: true);
    foreach (var (name, data) in kept) {
      var entry = reader.Entries.FirstOrDefault(e => e.Name == name);
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the directory");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_LeavesEachFileWhereItsDirectoryEntrySaysItStarts() {
    using var image = Volume(out var kept);
    image.Position = 0;
    new Reiser4FormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    using var reader = new Reiser4Reader(image, leaveOpen: true);
    var descriptor = new Reiser4FormatDescriptor();
    image.Position = 0;
    var extents = descriptor.EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .ToLookup(e => e.FileName!, StringComparer.Ordinal);

    foreach (var name in kept.Keys) {
      var entry = reader.Entries.First(e => e.Name == name);
      var first = extents[name].Min(e => e.Offset);
      Assert.That((ulong)(first / Reiser4Writer.BlockSize), Is.EqualTo(entry.FirstBlock),
        $"{name}'s entry must name the block its bytes actually start at");
    }
  }

  [Test]
  public void Defragment_KeepsEveryReservedBlockWhereItIs() {
    using var image = Volume(out _);
    var before = image.ToArray();

    image.Position = 0;
    new Reiser4FormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    var after = image.ToArray();

    // The superblocks, the directory chain and the allocator bitmaps are what
    // the map calls reserved. A file written over any of them would take the
    // volume's account of itself with it, and the pass writes the directory
    // afterwards, so only its entries may differ.
    image.Position = 0;
    var reserved = new Reiser4FormatDescriptor().EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.MetadataReserved)
      .ToList();

    Assert.That(reserved, Is.Not.Empty, "the volume must reserve its own structures");
    foreach (var region in reserved) {
      var at = (int)region.Offset;
      var length = (int)Math.Min(region.Length, before.Length - region.Offset);
      var changed = 0;
      for (var i = 0; i < length; ++i)
        if (before[at + i] != after[at + i]) ++changed;

      // A file's first block is eight bytes of its directory entry, and there
      // are four files left on this volume.
      Assert.That(changed, Is.LessThanOrEqualTo(8 * 4),
        $"nothing but the directory's first-block fields may change in the reserved region at {region.Offset}");
    }
  }
}
