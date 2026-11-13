using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Compressed blocks: pools written with each compression a version 28 pool can hold, then
/// edited in place under the same property. The reader and the accounting probe check them
/// here; OpenZFS checks them in the external tests — <c>zdb -bcc</c> (which decompresses and
/// checksums every block) finds no leak, <c>zdb -r</c> copies files out, and the kernel imports,
/// scrubs and serves exactly the expected files.
/// </summary>
[TestFixture]
public sealed class ZfsCompressedWriteTests {
  private static readonly string[] Algorithms = ["lzjb", "gzip-1", "gzip", "gzip-9", "zle"];

  /// <summary>Text that compresses, zero runs (whole zero records become holes), random data that does not.</summary>
  private static Dictionary<string, byte[]> Corpus() {
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 20000).Select(i => $"line {i % 97} of a compressible file\n")));
    var sparse = new byte[400 * 1024];
    new Random(5).NextBytes(sparse.AsSpan(200 * 1024, 1000));
    return new(StringComparer.Ordinal) {
      ["text.txt"] = text,
      ["small.txt"] = Encoding.ASCII.GetBytes(new string('a', 3000)),
      ["zeros.bin"] = new byte[256 * 1024],
      ["sparse.bin"] = sparse,
      ["random.bin"] = ZfsWriterTests.Pattern(150_000, 9),
      ["dir/tiny.txt"] = "tiny\n"u8.ToArray(),
      ["empty.txt"] = [],
    };
  }

  private static byte[] Write(Dictionary<string, byte[]> files, string compression) {
    using var output = new MemoryStream();
    new ZfsFormatDescriptor().Create(output, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions { FormatSpecific = { ["PoolName"] = "cwbc", ["Compression"] = compression } });
    return output.ToArray();
  }

  /// <summary>Writes a pool with <paramref name="compression"/>, then adds and replaces files in it.</summary>
  private static (byte[] Image, Dictionary<string, string> Expected, byte[] Before) Build(string compression) {
    var files = Corpus();
    var before = Write(files, compression);
    var image = new MemoryStream();
    image.Write(before);
    var added = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 9000).Select(i => $"added {i % 13}\n")));
    new ZfsFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("added/more.txt", added), ArchiveInputInfo.InMemory("small.txt", new byte[5000])]);
    files["added/more.txt"] = added;
    files["small.txt"] = new byte[5000];
    return (image.ToArray(), files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal), before);
  }

  [TestCaseSource(nameof(Algorithms))]
  public void Given_ACompression_When_APoolIsWrittenAndEdited_Then_ItReadsBackAndAccountsForItsBlocks(string compression) {
    var (image, expected, before) = Build(compression);
    using var reader = new ZfsReader(new MemoryStream(image));
    Assert.That(reader.Warnings, Is.Empty);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (name, sha) in expected)
      Assert.That(ZfsOracle.Sha(reader.Extract(files[name])), Is.EqualTo(sha), name);
    var algorithm = ZfsCompression.ParseForVersion28(compression);
    Assert.That(reader.Statistics.Compressions, Does.Contain(algorithm), "some blocks are stored compressed");
    Assert.That(reader.Statistics.Holes, Is.GreaterThan(0), "zero records are holes");

    var start = ZfsAccountingProbe.Probe(before);
    var end = ZfsAccountingProbe.Probe(image);
    Assert.Multiple(() => {
      foreach (var (key, ledger) in start.Concat(end))
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: recorded space");
    });
  }

  [TestCase("lz4")]
  [TestCase("zstd")]
  public void Given_ACompressionThatNeedsAFeature_When_Written_Then_ItIsRefused(string compression) =>
    Assert.Throws<NotSupportedException>(() => Write(Corpus(), compression));

  [Test]
  public void Given_AnUnknownCompression_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => Write(Corpus(), "gzip-10"));

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  [TestCaseSource(nameof(Algorithms)), Category("Slow"), Category("ExternalTool")]
  public void Given_ACompressedPool_When_ZdbTraversesIt_Then_NothingLeaksAndFilesCopyOut(string compression) {
    ZfsOracle.RequireZdb();
    var (image, expected, _) = Build(compression);
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbc");
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbc");
      foreach (var name in new[] { "text.txt", "sparse.bin", "random.bin", "added/more.txt", "small.txt" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbc", name, expected[name]);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(Algorithms)), Category("Slow"), Category("ExternalTool")]
  public void Given_ACompressedPool_When_TheKernelImportsAndScrubsIt_Then_ItServesExactlyTheFiles(string compression) {
    ZfsOracle.RequireAppliance();
    var (image, expected, _) = Build(compression);
    var (dir, wsl) = ZfsOracle.Stage(image, "cwbc");
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbc"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}
