using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// The reader against archives dar itself wrote (ReferenceVectors/README.md): every layer — slices,
/// tape marks, each compression, block mode, sparse data, hard links — must give back the source
/// tree byte for byte.
/// </summary>
[TestFixture]
public class DarReaderTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = DarFixtures.TempDirectory("reader");

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static readonly string[] TreeArchives = [
    "tree.1.dar", "tree-notape.1.dar", "tree-gzip.1.dar", "tree-bzip2.1.dar", "tree-xz.1.dar",
    "tree-zstd.1.dar", "tree-lz4.1.dar", "tree-lzo.1.dar", "tree-zstd-block.1.dar", "tree-gzip-block.1.dar",
  ];

  private static List<ArchiveEntryInfo> List(string vector) {
    using var ms = new MemoryStream(DarFixtures.Vector(vector));
    return new DarFormatDescriptor().List(ms, null);
  }

  private string ExtractVector(string vector, string[]? files = null) {
    var outDir = Path.Combine(this._tmp, Path.GetFileNameWithoutExtension(vector));
    using var ms = new MemoryStream(DarFixtures.Vector(vector));
    new DarFormatDescriptor().Extract(ms, outDir, null, files);
    return outDir;
  }

  private static void AssertTree(string outDir) {
    foreach (var (path, content) in DarFixtures.TreeFiles)
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, path)), Is.EqualTo(content), path);
    var extracted = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
      .Select(f => Path.GetRelativePath(outDir, f).Replace('\\', '/'))
      .Order(StringComparer.Ordinal);
    Assert.That(extracted, Is.EqualTo(DarFixtures.TreeFiles.Keys.Order(StringComparer.Ordinal)),
      "only regular files are materialised; the symlink and the fifo are listed, not written");
  }

  [TestCaseSource(nameof(TreeArchives))]
  public void GivenATreeArchiveWrittenByDar_WhenExtracted_ThenEveryFileMatchesTheSource(string vector)
    => AssertTree(this.ExtractVector(vector));

  [TestCaseSource(nameof(TreeArchives))]
  public void GivenATreeArchiveWrittenByDar_WhenListed_ThenNamesKindsAndSizesMatchTheSource(string vector) {
    var entries = List(vector).ToDictionary(e => e.Name, StringComparer.Ordinal);
    foreach (var (path, content) in DarFixtures.TreeFiles) {
      Assert.That(entries, Contains.Key(path));
      Assert.That(entries[path].OriginalSize, Is.EqualTo(content.Length), path);
      Assert.That(entries[path].IsDirectory, Is.False, path);
    }
    Assert.That(entries["dir"].IsDirectory, Is.True);
    Assert.That(entries["dir/deeper"].IsDirectory, Is.True);
    Assert.That(entries["link"].IsSymlink, Is.True);
    Assert.That(entries["link"].LinkTarget, Is.EqualTo("hello.txt"));
    Assert.That(entries["fifo"].Kind, Is.EqualTo("fifo"));
    Assert.That(entries["hello.txt"].LastModified, Is.EqualTo(DarFixtures.HelloModified));
    Assert.That(entries, Has.Count.EqualTo(DarFixtures.TreeFiles.Count + 4)); // + dir, dir/deeper, link, fifo
  }

  [TestCase("tree-gzip.1.dar", "gzip")]
  [TestCase("tree-bzip2.1.dar", "bzip2")]
  [TestCase("tree-xz.1.dar", "xz")]
  [TestCase("tree-zstd.1.dar", "zstd")]
  [TestCase("tree-lz4.1.dar", "lz4")]
  [TestCase("tree-lzo.1.dar", "lzo")]
  public void GivenACompressedArchive_WhenListed_ThenCompressibleFilesReportTheMethodAndSmallerStorage(string vector, string method) {
    var text = List(vector).Single(e => e.Name == "text.txt");
    Assert.That(text.Method, Is.EqualTo(method));
    Assert.That(text.CompressedSize, Is.LessThan(text.OriginalSize));
  }

  [Test]
  public void GivenAHoleInAFile_WhenListed_ThenTheStoredSizeIsTheHoleRecordNotTheZeros() {
    var sparse = List("tree.1.dar").Single(e => e.Name == "sparse.bin");
    Assert.That(sparse.OriginalSize, Is.EqualTo(70008));
    Assert.That(sparse.CompressedSize, Is.EqualTo(19)); // "head" + 6-byte mark + 5-byte infinint + "tail"
  }

  [Test]
  public void GivenAFilter_WhenExtracted_ThenOnlyTheNamedFileIsWritten() {
    var outDir = this.ExtractVector("tree-gzip.1.dar", ["dir/deeper/file.txt"]);
    Assert.That(Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName),
      Is.EqualTo(new[] { "file.txt" }));
  }

  [Test]
  public void GivenAThreeSliceSet_WhenAnySliceIsOpenedFromDisk_ThenTheSiblingsAreFoundAndTheTreeIsRead() {
    var paths = DarFixtures.Materialise(this._tmp, "multi.1.dar", "multi.2.dar", "multi.3.dar");
    foreach (var path in paths) {
      var outDir = Path.Combine(this._tmp, "out_" + Path.GetFileName(path));
      using var fs = File.OpenRead(path);
      new DarFormatDescriptor().Extract(fs, outDir, null, null);
      AssertTree(outDir);
    }
  }

  [Test]
  public void GivenAThreeSliceSetMissingTheMiddleSlice_WhenListed_ThenItFallsBackWithoutThrowing() {
    var paths = DarFixtures.Materialise(this._tmp, "multi.1.dar", "multi.3.dar");
    using var fs = File.OpenRead(paths[1]);
    var entries = new DarFormatDescriptor().List(fs, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.dar", "metadata.ini" }));
  }

  [Test]
  public void GivenAnArchiveOfAnEmptyDirectory_WhenListed_ThenItHasNoEntries() {
    Assert.That(List("empty.1.dar"), Is.Empty);
    var outDir = this.ExtractVector("empty.1.dar");
    Assert.That(Directory.EnumerateFileSystemEntries(outDir), Is.Empty);
  }

  [Test]
  public void GivenAnEncryptedArchive_WhenListed_ThenItIsRefusedHonestly() {
    Assert.That(List("encrypted.1.dar").Select(e => e.Name), Is.EqualTo(new[] { "FULL.dar", "metadata.ini" }));
    var outDir = this.ExtractVector("encrypted.1.dar", ["metadata.ini"]);
    var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
    Assert.That(meta, Does.Contain("archive_problem=The DAR archive is encrypted."));
    Assert.That(meta, Does.Contain("parse_status=partial"));
  }

  [Test]
  public void GivenDar286Output_WhenRead_ThenBothFilesComeBack() {
    var outDir = this.ExtractVector("single.1.dar");
    Assert.That(File.ReadAllText(Path.Combine(outDir, "hello.txt")), Is.EqualTo("hello dar\n"));
    Assert.That(new FileInfo(Path.Combine(outDir, "noise.bin")).Length, Is.EqualTo(3000));
  }

  [Test]
  public void GivenADar286SliceSet_WhenTheLastSliceIsOpenedFromDisk_ThenItMatchesTheSingleSliceArchive() {
    var paths = DarFixtures.Materialise(this._tmp, "sliced.1.dar", "sliced.2.dar", "sliced.3.dar");
    var outDir = Path.Combine(this._tmp, "sliced");
    using (var fs = File.OpenRead(paths[2]))
      new DarFormatDescriptor().Extract(fs, outDir, null, null);
    var single = this.ExtractVector("single.1.dar");
    foreach (var name in new[] { "hello.txt", "noise.bin" })
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, name)), Is.EqualTo(File.ReadAllBytes(Path.Combine(single, name))), name);
  }

  // ── Corruption ─────────────────────────────────────────────────────────────

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);

  [Test, Category("Exceptional")]
  public void GivenAFlippedByteInFileData_WhenExtracted_ThenTheChecksumMismatchIsReported() {
    var image = DarFixtures.Vector("tree-notape.1.dar");
    var at = IndexOf(image, "hello dar\n"u8);
    Assert.That(at, Is.GreaterThan(0));
    image[at] ^= 0x20;
    using var ms = new MemoryStream(image);
    var ex = Assert.Throws<InvalidDataException>(() => new DarFormatDescriptor().Extract(ms, this._tmp, null, ["hello.txt"]));
    Assert.That(ex!.Message, Does.Contain("checksum"));
  }

  [Test, Category("Exceptional")]
  public void GivenAFlippedByteInTheCatalogue_WhenListed_ThenItFallsBackWithoutThrowing() {
    var image = DarFixtures.Vector("tree-notape.1.dar");
    var at = IndexOf(image, "deeper"u8.ToArray().Append((byte)0).ToArray());
    Assert.That(at, Is.GreaterThan(0));
    image[at] ^= 0x01;
    using var ms = new MemoryStream(image);
    var entries = new DarFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.dar", "metadata.ini" }));
  }

  [TestCase(1)]
  [TestCase(40)]
  [TestCase(400)]
  [Category("Exceptional")]
  public void GivenATruncatedArchive_WhenListed_ThenItFallsBackWithoutThrowing(int cut) {
    var image = DarFixtures.Vector("tree-gzip.1.dar");
    using var ms = new MemoryStream(image[..^cut]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new DarFormatDescriptor().List(ms, null));
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.dar", "metadata.ini" }));
  }
}
