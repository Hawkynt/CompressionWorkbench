using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// The FAT type a volume declares must be the one its own cluster count implies.
/// </summary>
/// <remarks>
/// <para>A FAT volume does not record its type anywhere a reader trusts. Every
/// reader — fsck.fat, the Linux driver, Windows — counts the data clusters and
/// calls the volume FAT12 below 4,085 and FAT16 below 65,525. So the count is
/// the type, and a BPB describing a 16-bit table over a heap of 3,937 clusters
/// is not a volume with an unusual header: it is a volume every reader decodes
/// twelve bits at a time, walking chains that were written sixteen.</para>
///
/// <para>The writer derived the type once, from a provisional count taken at one
/// sector per cluster, and then let the FAT16 branch move the volume to
/// four-sector clusters and a 512-entry root — which on a volume of a few
/// megabytes drops the count back under FAT16's own floor. Files read back short
/// and interleaved, and only a defragmentation in carve mode reached the path,
/// because every other mode's planner succeeded and never rebuilt.</para>
/// </remarks>
[TestFixture]
public class FatDeclaredTypeMatchesGeometryTests {

  /// <summary>
  /// Sector counts spanning the window where the two disagreed. At one sector
  /// per cluster a volume above 4,118 sectors counts as FAT16; at four it falls
  /// back under 4,085 until about 16,405 sectors — so everything between was
  /// written declaring a type of which it had the wrong number of clusters.
  /// </summary>
  private static readonly int[] SectorCounts = [
    2880,    // 1.44 MB floppy — below the window, must be untouched
    4096, 4118, 4200, 5000, 8000, 12000, 15816, 16404,
    16405,   // first count that is genuinely FAT16 at four sectors per cluster
    20000, 40000, 65536,
  ];

  [TestCaseSource(nameof(SectorCounts)), Category("Regression")]
  public void AutoSizedVolume_DeclaresTheTypeItsClusterCountImplies(int totalSectors) {
    var writer = new FatWriter();
    // A little content, so the volume is a real one rather than an empty shell.
    for (var i = 0; i < 4; ++i) {
      var data = new byte[600 + i * 400];
      for (var j = 0; j < data.Length; ++j) data[j] = (byte)(j * 31 + i * 7);
      writer.AddFile($"F{i}.BIN", data);
    }

    using var image = new MemoryStream();
    writer.BuildTo(image, totalSectors: totalSectors);

    var bpb = image.ToArray();
    int U16(int at) => bpb[at] | (bpb[at + 1] << 8);
    var bytesPerSector = U16(11);
    var sectorsPerCluster = bpb[13];
    var reserved = U16(14);
    var fatCount = bpb[16];
    var rootEntries = U16(17);
    var fatSize = U16(22);
    var total = U16(19) != 0 ? U16(19)
      : bpb[32] | (bpb[33] << 8) | (bpb[34] << 16) | (bpb[35] << 24);
    if (fatSize == 0) fatSize = bpb[36] | (bpb[37] << 8) | (bpb[38] << 16) | (bpb[39] << 24);

    Assert.That(sectorsPerCluster, Is.GreaterThan(0), "a volume with no cluster size is unreadable");

    var rootDirSectors = (rootEntries * 32 + bytesPerSector - 1) / bytesPerSector;
    var firstDataSector = reserved + fatCount * fatSize + rootDirSectors;
    var clusters = (total - firstDataSector) / sectorsPerCluster;
    var implied = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;

    // The BPB's own type string is advisory, but when it is present it must not
    // contradict the count either — it is what a human and some tools read.
    var stamped = System.Text.Encoding.ASCII.GetString(bpb, implied == 32 ? 82 : 54, 8).Trim();

    // How many entries the declared table can actually name. A table too short
    // for the heap is the other half of the same fault: clusters past its end
    // have no entry, so whatever lands there is lost.
    var fatBits = implied == 12 ? 12 : implied == 16 ? 16 : 32;
    var nameable = (long)fatSize * bytesPerSector * 8 / fatBits - 2;

    Assert.Multiple(() => {
      Assert.That(stamped, Is.EqualTo($"FAT{implied}"),
        $"{totalSectors} sectors: the volume has {clusters} clusters, which every reader "
        + $"calls FAT{implied}, but the BPB stamps '{stamped}'");
      Assert.That(nameable, Is.GreaterThanOrEqualTo(clusters),
        $"{totalSectors} sectors: the FAT names {nameable} clusters but the heap has {clusters}");
    });
  }

  /// <summary>
  /// And the files have to survive it. A mismatched type does not fail loudly —
  /// the chains are simply read at the wrong width, so files come back short or
  /// holding one another's bytes.
  /// </summary>
  [TestCaseSource(nameof(SectorCounts)), Category("Regression")]
  public void AutoSizedVolume_ReadsBackEveryFile(int totalSectors) {
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var writer = new FatWriter();

    // Enough content to run past the first few clusters, so a chain read at the
    // wrong width has somewhere to go wrong.
    var budget = Math.Max(64L * 1024, (long)totalSectors * 512 / 3);
    var each = (int)Math.Max(2048, budget / 12);
    for (var i = 0; i < 12; ++i) {
      var data = new byte[each + i * 97];
      for (var j = 0; j < data.Length; ++j) data[j] = (byte)(j * 31 + i * 7 + (j >> 11));
      expected[$"F{i:D2}.BIN"] = data;
      writer.AddFile($"F{i:D2}.BIN", data);
    }

    using var image = new MemoryStream();
    writer.BuildTo(image, totalSectors: totalSectors);

    image.Position = 0;
    var reader = new FatReader(image);
    foreach (var (name, want) in expected) {
      var entry = reader.Entries.FirstOrDefault(e =>
        !e.IsDirectory && string.Equals(Path.GetFileName(e.Name), name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{totalSectors} sectors: '{name}' is not in the volume");
      Assert.That(reader.Extract(entry!), Is.EqualTo(want).AsCollection,
        $"{totalSectors} sectors: '{name}' did not read back as written");
    }
  }
}
