using Compression.Registry;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

/// <summary>
/// The writer's output read back by our reader, and the primitives it is built from pinned to the
/// bytes dar writes. Acceptance by dar itself is in <see cref="DarExternalConformanceTests"/>.
/// </summary>
[TestFixture]
public class DarWriterTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() => this._tmp = DarFixtures.TempDirectory("writer");

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  internal static readonly string[] Methods = ["stored", "gzip", "bzip2", "xz", "zstd", "lz4"];

  internal static List<ArchiveInputInfo> SampleInputs() {
    var inputs = new List<ArchiveInputInfo> { new("dir", "dir", IsDirectory: true), new("emptydir", "emptydir", IsDirectory: true) };
    inputs.AddRange(DarFixtures.TreeFiles.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)));
    return inputs;
  }

  private static byte[] Create(IReadOnlyList<ArchiveInputInfo> inputs, string method) {
    using var ms = new MemoryStream();
    new DarFormatDescriptor().Create(ms, inputs, new FormatCreateOptions { MethodName = method });
    return ms.ToArray();
  }

  private string Extract(byte[] archive) {
    var outDir = Path.Combine(this._tmp, Guid.NewGuid().ToString("N"));
    using var ms = new MemoryStream(archive);
    new DarFormatDescriptor().Extract(ms, outDir, null, null);
    return outDir;
  }

  [TestCaseSource(nameof(Methods))]
  public void GivenAFileTree_WhenWrittenAndReadBack_ThenEveryFileIsByteIdentical(string method) {
    var outDir = this.Extract(Create(SampleInputs(), method));
    foreach (var (path, content) in DarFixtures.TreeFiles)
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, path)), Is.EqualTo(content), path);
    Assert.That(Directory.Exists(Path.Combine(outDir, "emptydir")), Is.True);
  }

  [TestCaseSource(nameof(Methods))]
  public void GivenAFileTree_WhenWritten_ThenTheListingHasEveryNameAndSize(string method) {
    using var ms = new MemoryStream(Create(SampleInputs(), method));
    var entries = new DarFormatDescriptor().List(ms, null).ToDictionary(e => e.Name, StringComparer.Ordinal);
    foreach (var (path, content) in DarFixtures.TreeFiles)
      Assert.That(entries[path].OriginalSize, Is.EqualTo(content.Length), path);
    // implicit parents are created
    Assert.That(entries["dir/deeper"].IsDirectory, Is.True);
    Assert.That(entries["emptydir"].IsDirectory, Is.True);
  }

  [TestCase("gzip")]
  [TestCase("xz")]
  [TestCase("zstd")]
  public void GivenCompressibleData_WhenCompressed_ThenItIsStoredSmaller(string method) {
    using var ms = new MemoryStream(Create(SampleInputs(), method));
    var text = new DarFormatDescriptor().List(ms, null).Single(e => e.Name == "text.txt");
    Assert.That(text.CompressedSize, Is.LessThan(text.OriginalSize / 4));
  }

  [Test]
  public void GivenIncompressibleData_WhenCompressed_ThenItIsStoredAsIs() {
    using var ms = new MemoryStream(Create(SampleInputs(), "gzip"));
    var noise = new DarFormatDescriptor().List(ms, null).Single(e => e.Name == "noise.bin");
    Assert.That(noise.Method, Is.EqualTo("Stored"));
    Assert.That(noise.CompressedSize, Is.EqualTo(600));
  }

  [Test]
  public void GivenNoInputs_WhenWritten_ThenTheArchiveListsNothing() {
    var archive = Create([], "stored");
    using var ms = new MemoryStream(archive);
    Assert.That(new DarFormatDescriptor().List(ms, null), Is.Empty);
  }

  // One byte either side of the 246660-byte block dar's lz4 framing uses.
  [TestCase(246659)]
  [TestCase(246660)]
  [TestCase(246661)]
  [TestCase(2 * 246660)]
  public void GivenAFileAtTheLz4BlockBoundary_WhenWrittenWithLz4_ThenItRoundTrips(int size) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i)
      data[i] = (byte)(i % 97);
    var outDir = this.Extract(Create([ArchiveInputInfo.InMemory("blocks.bin", data)], "lz4"));
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "blocks.bin")), Is.EqualTo(data));
  }

  [Test]
  public void GivenANameOf255Bytes_WhenWritten_ThenItRoundTrips() {
    var name = new string('x', 251) + ".txt";
    var outDir = this.Extract(Create([ArchiveInputInfo.InMemory("d/" + name, "z"u8.ToArray())], "stored"));
    Assert.That(File.ReadAllText(Path.Combine(outDir, "d", name)), Is.EqualTo("z"));
  }

  [Test]
  public void GivenDataHoldingDarsEscapePrefix_WhenWritten_ThenItIsStoredVerbatim() {
    // No tape marks are written, so nothing in the data needs escaping.
    var data = DarFixtures.TreeFiles["escape.bin"];
    var archive = Create([ArchiveInputInfo.InMemory("e.bin", data)], "stored");
    Assert.That(archive.AsSpan().IndexOf(data), Is.GreaterThan(0));
    Assert.That(File.ReadAllBytes(Path.Combine(this.Extract(archive), "e.bin")), Is.EqualTo(data));
  }

  [TestCase("../evil.txt")]
  [TestCase("a/./b.txt")]
  [Category("Exceptional")]
  public void GivenANonRelativeName_WhenWritten_ThenItIsRejected(string name)
    => Assert.Throws<ArgumentException>(() => Create([ArchiveInputInfo.InMemory(name, [1])], "stored"));

  [Test, Category("Exceptional")]
  public void GivenAnUnknownMethod_WhenWritten_ThenItIsRejected()
    => Assert.Throws<NotSupportedException>(() => Create(SampleInputs(), "rar"));

  [Test]
  public void GivenAWrittenArchive_WhenTheSliceHeaderIsParsed_ThenItIsASingleLastSliceCarryingTheDataName() {
    var archive = Create(SampleInputs(), "stored");
    using var ms = new MemoryStream(archive);
    var h = DarSliceSet.ReadHeader(ms);
    Assert.That(h.IsValid, Is.True, h.Problem);
    Assert.That(h.Flag, Is.EqualTo('T'));
    Assert.That(h.Trailer, Is.EqualTo('T'));
    Assert.That(h.DataName, Is.EqualTo(h.InternalName));
    Assert.That(h.SliceSize, Is.Null);
  }

  // ── Primitives, pinned to bytes dar wrote ──────────────────────────────────

  [TestCase(0UL, "8000000000")]
  [TestCase(10UL, "800000000A")]
  [TestCase(0xFFFFFFFFUL, "80FFFFFFFF")]
  [TestCase(0x100000000UL, "400000000100000000")]
  [TestCase(ulong.MaxValue, "40FFFFFFFFFFFFFFFF")]
  public void GivenAValue_WhenWrittenAsInfinint_ThenItHasDarsShortestFormAndReadsBack(ulong value, string hex) {
    using var ms = new MemoryStream();
    DarPrimitives.WriteInfinint(ms, value);
    Assert.That(Convert.ToHexString(ms.ToArray()), Is.EqualTo(hex));
    var pos = 0;
    Assert.That(DarPrimitives.ReadInfinint(ms.ToArray(), ref pos), Is.EqualTo(value));
    Assert.That(pos, Is.EqualTo(ms.Length));
  }

  // Terminators measured in dar output: catalogue at 30 and version trailer at 473.
  [TestCase(30UL, "800000001E000000C0")]
  [TestCase(473UL, "80000001D9000000C0")]
  [TestCase(0x100000000UL, "400000000100000000000000E0")]
  public void GivenAPosition_WhenWrittenAsTerminator_ThenItMatchesDarAndReadsBackwards(ulong position, string hex) {
    using var ms = new MemoryStream();
    ms.Write([1, 2, 3]);
    DarPrimitives.WriteTerminator(ms, position);
    Assert.That(Convert.ToHexString(ms.ToArray()[3..]), Is.EqualTo(hex));
    Assert.That(DarPrimitives.ReadTerminator(ms, ms.Length, out var start), Is.EqualTo(position));
    Assert.That(start, Is.EqualTo(3));
  }

  [Test]
  public void GivenTheHeadersDarWritesWithoutTapeMarks_WhenWritten_ThenTheBytesAreIdentical() {
    // Measured in a `dar -at` archive: the version header at the start and the trailer recording
    // initial offset 17.
    using var head = new MemoryStream();
    new DarVersionHeader(11, 3, 'n', "N/A", 0, 0, 0).Write(head);
    // edition "0;3\0" (11.3), 'n', "N/A\0", flags, [initial offset], checksum width 2 + ring
    Assert.That(Convert.ToHexString(head.ToArray()), Is.EqualTo("303B3300" + "6E" + "4E2F4100" + "00" + "8000000002" + "4234"));
    using var tail = new MemoryStream();
    new DarVersionHeader(11, 3, 'n', "N/A", DarArchive.FlagInitialOffset, 17, 0).Write(tail);
    Assert.That(Convert.ToHexString(tail.ToArray()), Is.EqualTo("303B3300" + "6E" + "4E2F4100" + "08" + "8000000011" + "8000000002" + "D33C"));
  }

  [Test]
  public void GivenABlockCompressionHeader_WhenRoundTripped_ThenTheTwoByteFlagFieldIsKept() {
    using var ms = new MemoryStream();
    new DarVersionHeader(11, 3, 'z', "N/A", DarArchive.FlagCompressionBlockSize | DarArchive.FlagTapeMarks | DarArchive.FlagInitialOffset, 23, 16384).Write(ms);
    var parsed = DarVersionHeader.Parse(ms.ToArray(), out var length);
    Assert.That(length, Is.EqualTo(ms.Length));
    Assert.That(parsed.BlockSize, Is.EqualTo(16384));
    Assert.That(parsed.InitialOffset, Is.EqualTo(23));
    Assert.That(parsed.HasTapeMarks, Is.True);
  }

  [TestCase(0UL, 1)]
  [TestCase(1UL, 4)]
  [TestCase(1UL << 30, 4)]
  [TestCase((1UL << 30) + 1, 8)]
  public void GivenAFileSize_WhenTheChecksumWidthIsChosen_ThenItFollowsDarsRule(ulong size, int width)
    => Assert.That(DarPrimitives.DataChecksumWidth(size), Is.EqualTo(width));

  [Test]
  public void GivenHelloDar_WhenChecksummed_ThenItMatchesTheValueDarRecorded()
    => Assert.That(Convert.ToHexString(DarPrimitives.Checksum("hello dar\n"u8, 4)), Is.EqualTo("754F080D"));

  [Test]
  public void GivenEscapedData_WhenUnescaped_ThenTheXIsDroppedAndARealMarkStopsTheRead() {
    byte[] escaped = [1, 0xAD, 0xFD, 0xEA, 0x77, 0x21, (byte)'X', 2, 0xAD, 0xFD, 0xEA, 0x77, 0x21, (byte)'R', 9, 9];
    using var ms = new MemoryStream(escaped);
    Assert.That(DarPrimitives.ReadUnescaped(ms, 0, -1, escaped.Length), Is.EqualTo(new byte[] { 1, 0xAD, 0xFD, 0xEA, 0x77, 0x21, 2 }));
  }

  [Test]
  public void GivenSparseData_WhenExpanded_ThenHolesBecomeZerosAndEscapedPrefixesStay() {
    byte[] sparse = [7, 0xAE, 0xFD, 0xEA, 0x77, 0x21, (byte)'F', 0x80, 0, 0, 0, 3, 0xAE, 0xFD, 0xEA, 0x77, 0x21, (byte)'X', 8];
    Assert.That(DarPrimitives.Unsparse(sparse, 10), Is.EqualTo(new byte[] { 7, 0, 0, 0, 0xAE, 0xFD, 0xEA, 0x77, 0x21, 8 }));
  }

  [Test, Category("Exceptional")]
  public void GivenAHoleLongerThanTheFile_WhenExpanded_ThenItIsRejected() {
    byte[] sparse = [0xAE, 0xFD, 0xEA, 0x77, 0x21, (byte)'F', 0x80, 0, 0, 0, 50];
    Assert.Throws<InvalidDataException>(() => DarPrimitives.Unsparse(sparse, 10));
  }
}
