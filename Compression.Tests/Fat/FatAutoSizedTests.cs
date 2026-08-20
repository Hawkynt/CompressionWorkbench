using System.Text;

namespace Compression.Tests.Fat;

/// <summary>
/// FatWriter.BuildAutoSized must actually fit. Before the fix it had a hard
/// 1.44 MB floor which made "convert to FAT" always produce a 1.44 MB image
/// regardless of how little data the user wrote — defeating auto-size for the
/// Convert Archive flow.
/// </summary>
[TestFixture]
public class FatAutoSizedTests {

  /// <summary>
  /// A FAT12 volume with more clusters than a nine-sector table can name must
  /// still read back every byte.
  /// </summary>
  /// <remarks>
  /// Nine sectors is the 1.44 MB floppy convention and it names 3,070 clusters:
  /// 4,608 bytes at twelve bits an entry is 3,072, less the two the format
  /// reserves. Auto-sizing allows up to 4,084 clusters before moving to FAT16,
  /// so a volume could be given more clusters than its own table could name —
  /// the writer placed them, the table had no entries for them, and every file
  /// past the boundary came back as other files' bytes.
  ///
  /// <para>It took a few megabytes to reach and nothing looked: the fixture
  /// volumes elsewhere are kilobytes, where nine sectors is always enough. The
  /// sizes here are chosen to land past 3,070 clusters at the cluster size
  /// auto-sizing picks for them.</para>
  /// </remarks>
  [Test, Category("Regression")]
  [TestCase(4 * 1024 * 1024)]
  [TestCase(8 * 1024 * 1024)]
  [TestCase(16 * 1024 * 1024)]
  public void ClusterCountPastNineSectorTable_ReadsBackWholly(int totalBytes) {
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<Compression.Registry.ArchiveInputInfo>();

    // One large file plus a spread of smaller ones, so the volume has both a
    // long chain and many short ones.
    void Add(string name, int length, int seed) {
      var data = new byte[length];
      for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + seed * 7 + (i >> 11));
      expected[name] = data;
      inputs.Add(Compression.Registry.ArchiveInputInfo.InMemory(name, data));
    }

    Add("BIG00001.BIN", totalBytes / 2, 1);
    var rest = totalBytes - totalBytes / 2;
    var perFile = rest / 32;
    for (var i = 0; i < 32; ++i) {
      // A spread of sizes, including some far smaller than a cluster, because
      // what decides whether the bug bites is the cluster count the mix lands on.
      var len = (int)Math.Max(1, perFile + (i % 7) * 1024 - 3 * 1024);
      if (i % 11 == 0) len = 17 + i;
      Add($"F{i:D4}.BIN", len, i + 2);
    }

    // Through the descriptor, because that is the path a created volume takes
    // and it asks the writer for a geometry the writer's own defaults do not
    // choose — long filenames are on, which moves the cluster count.
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps("Fat");
    var built = new MemoryStream();
    ((Compression.Registry.IArchiveCreatable)ops!).Create(built, inputs,
      new Compression.Registry.FormatCreateOptions());
    var disk = built.ToArray();

    // The table has to be able to name every cluster the volume claims to hold.
    int bps = disk[11] | (disk[12] << 8);
    int spc = disk[13];
    int reserved = disk[14] | (disk[15] << 8);
    int fats = disk[16];
    int rootEnts = disk[17] | (disk[18] << 8);
    long totalSectors = disk[19] | (disk[20] << 8);
    if (totalSectors == 0)
      totalSectors = (uint)(disk[32] | (disk[33] << 8) | (disk[34] << 16) | (disk[35] << 24));
    long fatSectors = disk[22] | (disk[23] << 8);
    var rootSectors = (rootEnts * 32 + bps - 1) / bps;
    var dataClusters = (totalSectors - reserved - fats * fatSectors - rootSectors) / Math.Max(1, spc);
    var addressable = fatSectors * bps * 8 / 12 - 2;

    // The case only exists past the nine-sector table's 3,070 clusters. Say so
    // rather than let a volume that never got there report success.
    Assume.That(dataClusters, Is.GreaterThan(3070),
      $"this size lands on {dataClusters} clusters, which a nine-sector table can name; "
      + "the test is not exercising the case it is named for");

    Assert.That(dataClusters, Is.LessThanOrEqualTo(addressable),
      $"the volume claims {dataClusters} clusters but its FAT12 table can only name {addressable}");

    // And every file has to come back as itself.
    using var image = new MemoryStream(disk);
    var reader = new FileSystem.Fat.FatReader(image);
    foreach (var (name, want) in expected) {
      var entry = reader.Entries.FirstOrDefault(e =>
        string.Equals(Path.GetFileName(e.Name), name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{name} is missing from the volume");
      Assert.That(reader.Extract(entry!), Is.EqualTo(want), $"{name} did not read back byte for byte");
    }
  }

  [Test, Category("Regression")]
  public void TinyFileSet_ProducesSubMegabyteImage() {
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("hello.txt", Encoding.ASCII.GetBytes("hi"));
    w.AddFile("notes.md", Encoding.ASCII.GetBytes("just a few bytes"));
    var disk = w.BuildAutoSized();
    Assert.That(disk.Length, Is.LessThan(512 * 1024),
      $"3-byte+16-byte payload should NOT round up to 1.44 MB. Got {disk.Length} bytes.");
  }

  [Test, Category("Regression")]
  public void EmptyImage_StaysSmall() {
    var w = new FileSystem.Fat.FatWriter();
    var disk = w.BuildAutoSized();
    Assert.That(disk.Length, Is.LessThan(256 * 1024),
      $"No files should produce a tiny image. Got {disk.Length} bytes.");
  }

  [Test, Category("RoundTrip")]
  public void SmallImage_FilesStillRoundTrip() {
    var payload = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog");
    var w = new FileSystem.Fat.FatWriter();
    w.AddFile("test.txt", payload);
    var disk = w.BuildAutoSized();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Fat.FatReader(ms);
    var entry = r.Entries.First(e => !e.IsDirectory);
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }
}
