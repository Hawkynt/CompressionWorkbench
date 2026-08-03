#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Adfs;

namespace Compression.Tests.Adfs;

/// <summary>
/// ADFS lays a disc out again by moving what is out of place, on both of the
/// maps it can carry.
/// </summary>
/// <remarks>
/// The two maps are told apart by what a move has to rewrite. On the old map a
/// file is one contiguous run and its directory entry says which sector it
/// starts at, so the entry is rewritten and the free-region list settled at the
/// end. On the new map the directory says only which fragment a file is: where
/// that fragment's identifier sits in the zone bitmap is the whole of what
/// records its position, so the bitmap is written again and the directory is
/// left alone.
/// </remarks>
[TestFixture]
public class AdfsPlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  /// <summary>A disc of five files with two of them removed, leaving holes.</summary>
  private static MemoryStream Volume(bool newMap, out Dictionary<string, byte[]> kept) {
    var image = new MemoryStream();
    byte[] built;
    if (newMap) {
      var writer = new AdfsNewMapWriter();
      for (var k = 0; k < 5; ++k) writer.AddFile($"F{k}", Payload(k, 3072 + k * 512));
      built = writer.Build();
    } else {
      var writer = new AdfsWriter();
      for (var k = 0; k < 5; ++k) writer.AddFile($"F{k}", Payload(k, 3072 + k * 512));
      built = writer.Build();
    }

    image.Write(built, 0, built.Length);
    image.Position = 0;
    new AdfsFormatDescriptor().Remove(image, ["F1", "F3"]);

    kept = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var k in new[] { 0, 2, 4 }) kept[$"F{k}"] = Payload(k, 3072 + k * 512);
    return image;
  }

  [Test, Category("RoundTrip")]
  [TestCase(false, DefragMode.ConsolidateAtStart)]
  [TestCase(false, DefragMode.ConsolidateAtEnd)]
  [TestCase(false, DefragMode.FillHolesLazy)]
  [TestCase(true, DefragMode.ConsolidateAtStart)]
  [TestCase(true, DefragMode.ConsolidateAtEnd)]
  [TestCase(true, DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayloadAndTheDiscsSize(bool newMap, DefragMode mode) {
    using var image = Volume(newMap, out var kept);
    var size = image.Length;

    image.Position = 0;
    new AdfsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });

    Assert.That(image.Length, Is.EqualTo(size), "a disc keeps its size");

    image.Position = 0;
    using var reader = new AdfsReader(image);
    foreach (var (name, data) in kept) {
      var entry = reader.Entries.FirstOrDefault(
        e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the directory");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  [TestCase(false)]
  [TestCase(true)]
  public void Defragment_LeavesTheDiscStillWritable(bool newMap) {
    using var image = Volume(newMap, out var kept);
    image.Position = 0;
    new AdfsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    // Adding goes through the free map, so it only works if the map the pass
    // left behind says the truth about what is free.
    var added = Payload(9, 2048);
    var path = Path.Combine(Path.GetTempPath(), "cwb_adfs_" + Guid.NewGuid().ToString("N")[..8]);
    File.WriteAllBytes(path, added);
    try {
      image.Position = 0;
      new AdfsFormatDescriptor().Add(image, [new ArchiveInputInfo(path, "ADDED", false)]);
    } finally {
      File.Delete(path);
    }

    image.Position = 0;
    using var reader = new AdfsReader(image);
    var entry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name == "ADDED");
    Assert.That(entry, Is.Not.Null, "the added file must be readable");
    Assert.That(reader.Extract(entry!), Is.EqualTo(added));

    foreach (var (name, data) in kept) {
      var kept_ = reader.Entries.FirstOrDefault(
        e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(kept_, Is.Not.Null, $"{name} must have survived the add");
      Assert.That(reader.Extract(kept_!), Is.EqualTo(data));
    }
  }

  [Test]
  public void NewMap_FragmentsTileTheDiscWithoutOverlapping() {
    using var image = Volume(newMap: true, out _);
    image.Position = 0;
    new AdfsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var extents = new AdfsFormatDescriptor().EnumerateExtents(image)
      .Where(e => e.Kind != DefragBlockKind.Free)
      .OrderBy(e => e.Offset)
      .ToList();

    Assert.That(extents, Is.Not.Empty, "the map must still describe the disc");
    for (var i = 1; i < extents.Count; ++i)
      Assert.That(extents[i].Offset, Is.GreaterThanOrEqualTo(extents[i - 1].Offset + extents[i - 1].Length),
        "no two fragments may claim the same sector");

    // A fragment is its identifier's bits plus a terminator, so it can never be
    // shorter than that however little it holds.
    const long shortestFragment = 14 * 1024;
    foreach (var extent in extents)
      Assert.That(extent.Length, Is.GreaterThanOrEqualTo(shortestFragment),
        "a fragment shorter than its own identifier cannot be written down");
  }
}
