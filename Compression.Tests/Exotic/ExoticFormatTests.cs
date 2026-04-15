#pragma warning disable CS1591

using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Exotic;

/// <summary>
/// Corpus-based validation for exotic (Amiga / C64 / retro game / Mac) formats.
/// Generates a small archive via our own writer, then exercises the reader path
/// end-to-end through <see cref="ArchiveOperations"/> to confirm the full detect
/// → list → extract pipeline works against self-generated corpus bytes.
/// </summary>
[TestFixture]
[Category("ExoticFormats")]
public class ExoticFormatTests {

  [OneTimeSetUp]
  public void OneTimeSetup() => FormatRegistration.EnsureInitialized();

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_exotic_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Parameter source ───────────────────────────────────────────────────

  /// <summary>Registered exotic formats we can both create and read. Excludes
  /// formats without writers and ZIP-family variants (covered elsewhere).
  /// A non-null <c>descriptorId</c> means the format shares an extension with
  /// another format (WAD2 vs WAD on <c>.wad</c>) and must be exercised through
  /// the descriptor directly rather than through extension-based detection.
  /// </summary>
  public static IEnumerable<TestCaseData> ExoticFormats() {
    yield return new TestCaseData("D64",      ".d64",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateD64()),      (IArchiveFormatOperations?)null).SetName("D64");
    yield return new TestCaseData("T64",      ".t64",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateT64()),      (IArchiveFormatOperations?)null).SetName("T64");
    yield return new TestCaseData("Adf",      ".adf",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateAdf()),      (IArchiveFormatOperations?)null).SetName("ADF");
    yield return new TestCaseData("Hfs",      ".hfs",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateHfs()),      (IArchiveFormatOperations?)null).SetName("HFS");
    yield return new TestCaseData("Iso",      ".iso",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateIso()),      (IArchiveFormatOperations?)null).SetName("ISO9660");
    yield return new TestCaseData("Pak",      ".pak",  (Func<byte[]>)(() => ExoticFormatCorpus.CreatePak()),      (IArchiveFormatOperations?)null).SetName("PAK (Quake)");
    yield return new TestCaseData("Wad",      ".wad",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateWad()),      (IArchiveFormatOperations?)null).SetName("WAD (Doom)");
    yield return new TestCaseData("Wad2",     ".wad",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateWad2()),     (IArchiveFormatOperations?)new FileFormat.Wad2.Wad2FormatDescriptor()).SetName("WAD2/WAD3 (Quake/HL)");
    yield return new TestCaseData("Grp",      ".grp",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateGrp()),      (IArchiveFormatOperations?)null).SetName("GRP (BUILD)");
    yield return new TestCaseData("Hog",      ".hog",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateHog()),      (IArchiveFormatOperations?)null).SetName("HOG (Descent)");
    yield return new TestCaseData("Big",      ".big",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateBig()),      (IArchiveFormatOperations?)null).SetName("BIG (EA)");
    yield return new TestCaseData("GodotPck", ".pck",  (Func<byte[]>)(() => ExoticFormatCorpus.CreateGodotPck()), (IArchiveFormatOperations?)null).SetName("Godot PCK");
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Test, TestCaseSource(nameof(ExoticFormats))]
  public void Corpus_OurWriter_OurReader_RoundTrip(string formatId, string extension,
      Func<byte[]> generate, IArchiveFormatOperations? ambiguousOverride) {
    // Create corpus bytes and drop them on disk under the right extension
    // so FormatDetector.DetectByExtension resolves to the expected format.
    var archiveBytes = generate();
    Assert.That(archiveBytes, Is.Not.Null.And.Not.Empty,
      $"[{formatId}] corpus generator returned no bytes");

    var archivePath = Path.Combine(_tmpDir, $"corpus{extension}");
    File.WriteAllBytes(archivePath, archiveBytes);

    if (ambiguousOverride == null) {
      // Common path: FormatDetector → ArchiveOperations.List/Extract.
      var detected = FormatDetector.Detect(archivePath);
      Assert.That(detected.ToString(), Is.EqualTo(formatId),
        $"[{formatId}] FormatDetector resolved to {detected} instead");

      var entries = ArchiveOperations.List(archivePath, null)
        .Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.GreaterThanOrEqualTo(ExoticFormatCorpus.DefaultEntries.Length),
        $"[{formatId}] expected {ExoticFormatCorpus.DefaultEntries.Length} entries, got {entries.Count}");

      var outDir = Path.Combine(_tmpDir, "out");
      Directory.CreateDirectory(outDir);
      ArchiveOperations.Extract(archivePath, outDir, null, null);

      AssertPayloadsExtracted(outDir, formatId);
    }
    else {
      // Extension-ambiguous formats (WAD vs WAD2 both use .wad): bypass the
      // detector and exercise the descriptor directly.
      using var ms = new MemoryStream(archiveBytes);
      var entries = ambiguousOverride.List(ms, null)
        .Where(e => !e.IsDirectory).ToList();
      Assert.That(entries, Has.Count.GreaterThanOrEqualTo(ExoticFormatCorpus.DefaultEntries.Length),
        $"[{formatId}] expected {ExoticFormatCorpus.DefaultEntries.Length} entries, got {entries.Count}");

      ms.Position = 0;
      var outDir = Path.Combine(_tmpDir, "out");
      Directory.CreateDirectory(outDir);
      ambiguousOverride.Extract(ms, outDir, null, null);

      AssertPayloadsExtracted(outDir, formatId);
    }
  }

  private static void AssertPayloadsExtracted(string outDir, string formatId) {
    var extractedFiles = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
    Assert.That(extractedFiles, Is.Not.Empty, $"[{formatId}] extraction produced no files");

    // Every input payload must appear verbatim in some extracted file.
    // Name collation varies wildly (uppercased, truncated, PETSCII-mapped,
    // extension-appended) so match by content instead of filename.
    var extractedBytes = extractedFiles.Select(File.ReadAllBytes).ToList();
    foreach (var expected in ExoticFormatCorpus.DefaultEntries) {
      var found = extractedBytes.Any(b => ContainsSequence(b, expected.Data) ||
                                          b.AsSpan().SequenceEqual(expected.Data));
      Assert.That(found, Is.True,
        $"[{formatId}] payload for entry '{expected.Name}' not found in any extracted file");
    }
  }

  /// <summary>Some formats wrap the payload (e.g. C64 2-byte load address prefix)
  /// so a full-file equality check would fail. Accept any file whose contents
  /// contain the expected payload as a contiguous sub-sequence.</summary>
  private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    if (needle.Length == 0) return true;
    if (haystack.Length < needle.Length) return false;
    for (var i = 0; i <= haystack.Length - needle.Length; i++)
      if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
        return true;
    return false;
  }
}
