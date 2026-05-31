using System.Buffers.Binary;

namespace Compression.Tests.Fat;

/// <summary>
/// VFAT filename-mode options on the FAT writer: the default Auto mode emits
/// LFN slots only when a name needs them; non-VFAT mode (enableLfn = false)
/// emits pure 8.3 with no LFN slots; force-LFN mode emits an LFN slot set for
/// every entry — even names that already fit 8.3 — the way Windows always
/// records a long name alongside a generated 8.3 alias.
/// </summary>
[TestFixture]
public class FatFilenameModeTests {

  /// <summary>Counts the VFAT long-name slots (attribute 0x0F) in the fixed
  /// root directory of a FAT12/16 image, parsed straight from the BPB.</summary>
  private static int CountRootLfnSlots(byte[] disk) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(11));
    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(14));
    var numFats = disk[16];
    var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(17));
    var fatSz16 = BinaryPrimitives.ReadUInt16LittleEndian(disk.AsSpan(22));
    var rootOffset = (reserved + numFats * fatSz16) * bps;
    var count = 0;
    for (var i = 0; i < rootEntries; i++) {
      var off = rootOffset + i * 32;
      if (disk[off] == 0x00) break;            // end of directory
      if (disk[off] == 0xE5) continue;          // deleted
      if ((disk[off + 11] & 0x3F) == 0x0F) ++count;
    }
    return count;
  }

  [Test, Category("Spec")]
  public void Auto_PlainEightDotThreeName_EmitsNoLfnSlots() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("READ.TXT", "x"u8.ToArray());
    var disk = w.Build();
    Assert.That(CountRootLfnSlots(disk), Is.Zero, "auto mode: an 8.3 name needs no LFN slot");
  }

  [Test, Category("Spec")]
  public void ForceLfn_EmitsLfnSlots_EvenForEightDotThreeName() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("readme.txt", "hello"u8.ToArray());
    var disk = w.Build(forceLfn: true);

    Assert.That(CountRootLfnSlots(disk), Is.GreaterThan(0),
      "force-LFN: every entry gets a long-name slot even when it fits 8.3");

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var entry = r.Entries.Single(e => !e.IsDirectory);
    Assert.That(entry.Name, Is.EqualTo("readme.txt"), "the long name round-trips intact");
    Assert.That(r.Extract(entry), Is.EqualTo("hello"u8.ToArray()), "content intact");
  }

  [Test, Category("Spec")]
  public void NonVfat_EmitsNoLfnSlots_AndStoresOnlyShortName() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("A Long Mixed-Case Name.txt", "data"u8.ToArray());
    var disk = w.Build(enableLfn: false);

    Assert.That(CountRootLfnSlots(disk), Is.Zero, "non-VFAT: no LFN slots at all");

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var entry = r.Entries.Single(e => !e.IsDirectory);
    Assert.That(entry.Name, Is.Not.EqualTo("A Long Mixed-Case Name.txt"),
      "non-VFAT stores only a generated 8.3 short alias, not the long name");
    Assert.That(r.Extract(entry), Is.EqualTo("data"u8.ToArray()), "content intact");
  }
}
