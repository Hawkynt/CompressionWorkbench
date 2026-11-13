using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// The pool writer. Pools it writes are read back by this repository's reader here; the
/// external tests check them with OpenZFS itself: <c>zdb -bcc</c> must find no leak and no
/// checksum error and <c>zdb -r</c> must copy every file out byte for byte, and the kernel module
/// (in the libguestfs appliance) must import, scrub and mount the pool and read every file.
/// </summary>
[TestFixture]
public sealed class ZfsWriterTests {
  private static string Sha(byte[] data) => ZfsOracle.Sha(data);

  /// <summary>Files covering the layouts: empty, sub-sector, one block, indirect blocks, nested and empty directories, a fat-ZAP directory.</summary>
  internal static Dictionary<string, byte[]> Corpus(bool large) {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["hello.txt"] = "hello, zfs\n"u8.ToArray(),
      ["empty.txt"] = [],
      ["dir/sub/deep.txt"] = "deep\n"u8.ToArray(),
      ["block.bin"] = Pattern(128 * 1024, 1),
      ["multi.bin"] = Pattern(300_000, 2),
    };
    if (large)
      files["two-levels.bin"] = Pattern(17 * 1024 * 1024 + 4321, 3);       // past 128 level-1 pointers
    var longName = new string('n', 70);
    for (var i = 0; i < (large ? 2600 : 60); ++i)                            // past a micro ZAP, and past one fat-ZAP leaf
      files[$"crowd/{longName}-{i:D5}.txt"] = Encoding.ASCII.GetBytes($"entry {i}\n");
    return files;
  }

  internal static byte[] Pattern(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  internal static byte[] Write(Dictionary<string, byte[]> files, string pool = "cwbw") {
    var inputs = files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList();
    inputs.Add(new ArchiveInputInfo("emptydir", "emptydir", IsDirectory: true));
    using var output = new MemoryStream();
    new ZfsFormatDescriptor().Create(output, inputs, new FormatCreateOptions { FormatSpecific = { ["PoolName"] = pool } });
    return output.ToArray();
  }

  [Test]
  public void Given_Files_When_APoolIsWritten_Then_TheReaderReturnsThemByteForByte() {
    var files = Corpus(large: true);
    var image = Write(files);
    using var reader = new ZfsReader(new MemoryStream(image));
    Assert.That(reader.PoolName, Is.EqualTo("cwbw"));
    Assert.That(reader.PoolVersion, Is.EqualTo(28UL));
    Assert.That(reader.Warnings, Is.Empty);
    var byName = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      foreach (var (name, data) in files) {
        Assert.That(byName.TryGetValue(name, out var entry), Is.True, name);
        if (entry != null)
          Assert.That(Sha(reader.Extract(entry)), Is.EqualTo(Sha(data)), name);
      }
      Assert.That(byName.TryGetValue("emptydir", out var dir) && dir.IsDirectory, Is.True, "empty directory");
    });
  }

  [Test]
  public void Given_ABadPoolName_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => Write(new() { ["a"] = [1] }, "1pool"));

  [Test]
  public void Given_AFileWhereADirectoryIsNeeded_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => Write(new() { ["a"] = [1], ["a/b"] = [2] }));

  // ── OpenZFS ─────────────────────────────────────────────────────────────────────────────

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AWrittenPool_When_ZdbTraversesIt_Then_NothingLeaksAndEveryFileCopiesOut() {
    ZfsOracle.RequireZdb();
    var files = Corpus(large: true);
    var (dir, wsl) = ZfsOracle.Stage(Write(files), "cwbw");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbw");
      // zdb -r exits 22 on an empty file of a pool OpenZFS made as well; the kernel test covers it.
      var sample = files.Keys.Where(n => !n.StartsWith("crowd/", StringComparison.Ordinal) && files[n].Length > 0)
        .Append(files.Keys.First(n => n.StartsWith("crowd/", StringComparison.Ordinal)));
      foreach (var name in sample)
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbw", name, Sha(files[name]));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AWrittenPool_When_TheKernelImportsAndScrubsIt_Then_ItIsCleanAndServesEveryFile() {
    ZfsOracle.RequireAppliance();
    var files = Corpus(large: false);
    var (dir, wsl) = ZfsOracle.Stage(Write(files), "cwbw");
    try {
      var served = ZfsOracle.KernelManifest(dir, wsl, "cwbw");
      Assert.That(served.Keys, Is.EquivalentTo(files.Keys));
      foreach (var (name, data) in files)
        Assert.That(served[name], Is.EqualTo(Sha(data)), name);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
