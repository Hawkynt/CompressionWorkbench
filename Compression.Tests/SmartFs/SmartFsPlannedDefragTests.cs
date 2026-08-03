#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.SmartFs;

namespace Compression.Tests.SmartFs;

/// <summary>
/// SmartFS lays a volume out again by moving sectors, which is what the format
/// is built for: a sector is named by exactly one field, and its own header
/// says which logical sector it holds.
/// </summary>
[TestFixture]
public class SmartFsPlannedDefragTests {

  private const int SectorSize = 1024;

  /// <summary>First sector a file may occupy; the ones below it are the format's own.</summary>
  private const int FirstDataSector = 4;

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var writer = new SmartFsWriter();
    files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    for (var k = 0; k < 5; ++k) {
      var data = Payload(k, 1500 + k * 700);
      writer.AddFile($"F{k}.BIN", data);
      files[$"F{k}.BIN"] = data;
    }

    var image = new MemoryStream();
    var built = writer.Build();
    image.Write(built, 0, built.Length);
    return image;
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_KeepsEveryPayload(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new SmartFsFormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a volume keeps its size");

    image.Position = 0;
    var reader = new SmartFsReader(image);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(
        e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the directory");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_PutsEachFilesSectorsInOrderAndInOnePiece() {
    using var image = Volume(out var files);
    image.Position = 0;
    new SmartFsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    image.Position = 0;
    var byOwner = new SmartFsFormatDescriptor().EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .GroupBy(e => e.FileName, StringComparer.OrdinalIgnoreCase);

    foreach (var owner in byOwner) {
      var offsets = owner.Select(e => e.Offset).OrderBy(o => o).ToList();
      for (var i = 1; i < offsets.Count; ++i)
        Assert.That(offsets[i], Is.EqualTo(offsets[i - 1] + SectorSize),
          $"{owner.Key} should read in one sweep after a consolidating pass");
    }

    Assert.That(byOwner.Count(), Is.EqualTo(files.Count));
  }

  [Test]
  public void Defragment_LeavesEverySectorHoldingItsOwnLogicalNumber() {
    using var image = Volume(out _);
    image.Position = 0;
    new SmartFsFormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    var raw = image.ToArray();
    image.Position = 0;
    var live = new SmartFsFormatDescriptor().EnumerateExtents(image).ToList();

    // The format's own sectors below the first data one are reserved whether or
    // not anything has been written into them.
    foreach (var extent in live.Where(e => e.Kind == DefragBlockKind.Used)) {
      var sector = (int)(extent.Offset / SectorSize);
      var logical = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(sector * SectorSize));
      Assert.That(logical, Is.EqualTo((ushort)sector),
        "a volume that has not been wear-levelled keeps logical and physical the same, " +
        "and the reader depends on it");
    }

    // Sectors nothing claims read as erased flash, not as the file that used
    // to be there — two sectors claiming one logical number is a volume a
    // driver would try to recover.
    var claimed = live.Select(e => (int)(e.Offset / SectorSize)).ToHashSet();
    for (var sector = FirstDataSector; sector < raw.Length / SectorSize; ++sector) {
      if (claimed.Contains(sector)) continue;
      Assert.That(raw.AsSpan(sector * SectorSize, SectorSize).ToArray(), Is.All.EqualTo((byte)0xFF),
        $"sector {sector} holds nothing and should read as erased");
    }
  }
}
