using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Uharc;

namespace Compression.Tests.Uharc;

/// <summary>
/// UHARC 0.6 against archives UHARC 0.6b itself wrote (see ReferenceVectors/README.md): every
/// extracted file must match the SHA-256 of the input it came from, on top of the CRC-32 the
/// reader checks itself.
/// </summary>
[TestFixture]
public class UharcTests {

  private static readonly DateTime Stamp = new(2005, 10, 1, 11, 0, 0);

  internal static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"UharcVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded UHARC vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<(string Path, long Size, string Sha256)> Manifest(string set)
    => Encoding.UTF8.GetString(Vector("manifest.tsv"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split('\t'))
      .Where(p => p[0] == set)
      .Select(p => (p[1], long.Parse(p[2]), p[3]))
      .ToList();

  private static UharcReader Open(string name, string? password = null)
    => new(new MemoryStream(Vector(name)), password);

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  // ── Extraction against the inputs ──────────────────────────────────────

  [TestCase("basic-m0.uha", "basic", null, true)]
  [TestCase("basic-m1.uha", "basic", null, true)]
  [TestCase("basic-m2.uha", "basic", null, true)]
  [TestCase("basic-m3.uha", "basic", null, true)]
  [TestCase("basic-mz.uha", "basic", null, true)]
  [TestCase("basic-m2-pw.uha", "basic", "secret", true)]
  [TestCase("reset-m2.uha", "reset", null, true)]
  [TestCase("reset-mz.uha", "reset", null, true)]
  [TestCase("mm-m2.uha", "mm", null, true)]
  [TestCase("mm-pair-m2.uha", "mm", null, false)]
  [TestCase("mm-mz.uha", "mm", null, false)]
  [TestCase("exe-m2.uha", "exe", null, true)]
  [TestCase("exe-mz.uha", "exe", null, true)]
  [Category("HappyPath"), Category("ThemVsUs")]
  public void GivenArchiveUharcWrote_WhenExtracted_ThenEveryFileMatchesItsInput(string archive, string set, string? password, bool wholeSet) {
    var reader = Open(archive, password);
    var expected = Manifest(set).Where(m => m.Sha256 != "-").ToDictionary(m => m.Path);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Is.Not.Empty);
    if (wholeSet)
      Assert.That(files.Select(e => e.FileName).Order(), Is.EqualTo(expected.Keys.Order()));
    foreach (var entry in files) {
      Assert.That(expected.TryGetValue(entry.FileName, out var input), Is.True, $"{entry.FileName} is not an input of set '{set}'");
      var data = reader.Extract(entry);
      Assert.Multiple(() => {
        Assert.That(data.LongLength, Is.EqualTo(input.Size), entry.FileName);
        Assert.That(entry.Size, Is.EqualTo(input.Size), entry.FileName);
        Assert.That(Sha256(data), Is.EqualTo(input.Sha256), entry.FileName);
      });
    }
  }

  [Test, Category("HappyPath")]
  public void GivenBasicArchive_WhenListed_ThenNamesSizesTimesAndDirectoriesMatchTheInputs() {
    var reader = Open("basic-m2.uha");
    var files = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.FileName).Order().ToList();
    var directories = reader.Entries.Where(e => e.IsDirectory).Select(e => e.FileName).Order().ToList();
    Assert.Multiple(() => {
      Assert.That(files, Is.EqualTo(Manifest("basic").Where(m => m.Sha256 != "-").Select(m => m.Path).Order().ToList()));
      Assert.That(directories, Is.EqualTo(new[] { "emptydir", "sub" }));
      Assert.That(reader.Entries.Where(e => !e.IsDirectory).Select(e => e.LastModified), Is.All.EqualTo(Stamp));
      Assert.That(reader.Entries.Single(e => e.FileName == "hello.txt").Crc32, Is.EqualTo(0x7ED2DBFFu));
    });
  }

  [TestCase("basic-m0.uha", "Store", 0)]
  [TestCase("basic-m1.uha", "ALZ:1", 16384)]
  [TestCase("basic-m2.uha", "ALZ:2", 16384)]
  [TestCase("basic-m3.uha", "ALZ:3", 16384)]
  [TestCase("basic-mz.uha", "LZP", 16384)]
  [TestCase("basic-mx.uha", "PPM", 16384)]
  [TestCase("basic-mr.uha", "RLE", 0)]
  [TestCase("basic-mw.uha", "LZ78", 0)]
  [TestCase("reset-m2.uha", "ALZ:2", 262144)]
  [Category("HappyPath")]
  public void GivenEachMode_WhenOpened_ThenModeAndDictionaryAreReported(string archive, string mode, int dictionary) {
    var reader = Open(archive);
    Assert.Multiple(() => {
      Assert.That(reader.MethodName, Is.EqualTo(mode));
      Assert.That(reader.DictionarySize, Is.EqualTo(dictionary));
    });
  }

  [Test, Category("Boundary")]
  public void GivenTinyAndEmptyFiles_WhenExtracted_ThenSizesZeroOneAndTwoSurvive() {
    var reader = Open("reset-m2.uha");
    Assert.Multiple(() => {
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "f11.txt")), Is.Empty);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "f10.txt")), Has.Length.EqualTo(1));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "f12.txt")), Has.Length.EqualTo(2));
    });
  }

  // ── Honest refusals ────────────────────────────────────────────────────

  [TestCase("basic-mx.uha")]
  [TestCase("basic-mr.uha")]
  [TestCase("basic-mw.uha")]
  [Category("ErrorHandling")]
  public void GivenUnsupportedMode_WhenExtracted_ThenItIsRefusedButTheDirectoryLists(string archive) {
    var reader = Open(archive);
    Assert.Multiple(() => {
      Assert.That(reader.CanDecodeMethod, Is.False);
      Assert.That(reader.Entries.Count(e => !e.IsDirectory), Is.EqualTo(6));
      Assert.That(() => reader.Extract(reader.Entries.Single(e => e.FileName == "text.txt")), Throws.TypeOf<NotSupportedException>());
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "empty.txt")), Is.Empty);
    });
  }

  [Test, Category("ErrorHandling")]
  public void GivenPasswordArchive_WhenOpenedWithoutPassword_ThenAPasswordIsRequested()
    => Assert.That(() => Open("basic-m2-pw.uha"), Throws.TypeOf<InvalidOperationException>());

  [Test, Category("ErrorHandling")]
  public void GivenPasswordArchive_WhenOpenedWithTheWrongPassword_ThenItIsRejected()
    => Assert.That(() => Open("basic-m2-pw.uha", "Secret"), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("HappyPath")]
  public void GivenPasswordArchive_WhenOpenedWithThePassword_ThenItReportsEncryption()
    => Assert.That(Open("basic-m2-pw.uha", "secret").IsEncrypted, Is.True);

  // ── Damaged input ──────────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void GivenScrambledHeaderByteFlipped_WhenOpened_ThenTheHeaderCheckFails() {
    var data = Vector("basic-m2.uha");
    data[13] ^= 0x01;
    Assert.That(() => new UharcReader(new MemoryStream(data)), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("ErrorHandling")]
  public void GivenTruncatedArchive_WhenOpened_ThenItIsRejected() {
    var data = Vector("basic-m2.uha");
    Assert.That(() => new UharcReader(new MemoryStream(data[..(data.Length - 20)])), Throws.TypeOf<InvalidDataException>());
  }

  [TestCase(0)]
  [TestCase(3)]
  [TestCase(38)]
  [Category("Boundary")]
  public void GivenInputShorterThanAHeader_WhenOpened_ThenItIsRejected(int length) {
    var data = Vector("basic-m2.uha");
    Assert.That(() => new UharcReader(new MemoryStream(data[..length])), Throws.TypeOf<InvalidDataException>());
  }

  [TestCase("basic-m2.uha")]
  [TestCase("basic-mz.uha")]
  [Category("ErrorHandling")]
  public void GivenCorruptedPackedStream_WhenExtracted_ThenNoWrongBytesAreReturned(string archive) {
    var data = Vector(archive);
    for (var i = 60; i < 200; i += 7)
      data[i] ^= 0x5A;
    var reader = new UharcReader(new MemoryStream(data));
    Assert.That(() => {
      foreach (var entry in reader.Entries.Where(e => e.Size > 0))
        reader.Extract(entry);
    }, Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ErrorHandling")]
  public void GivenOlderUharcVersion_WhenOpened_ThenTheVersionIsNamed() {
    var data = Vector("basic-m2.uha");
    data[3] = 2;
    Assert.That(() => new UharcReader(new MemoryStream(data)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("version 2"));
  }

  // ── Descriptor ─────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenUharcBytes_WhenDetectedByMagic_ThenTheyAreUharc() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Vector("basic-m2.uha")).ToString(), Is.EqualTo("Uharc"));
  }

  [Test, Category("HappyPath")]
  public void GivenArchive_WhenExtractedThroughTheDescriptor_ThenFilesAndTheEmptyDirectoryAppear() {
    var output = Path.Combine(Path.GetTempPath(), "cwb-uharc-" + Guid.NewGuid().ToString("N"));
    try {
      new UharcFormatDescriptor().Extract(new MemoryStream(Vector("basic-m2.uha")), output, null, null);
      Assert.Multiple(() => {
        Assert.That(File.ReadAllBytes(Path.Combine(output, "sub", "nested.txt")), Is.EqualTo("nested file\r\n"u8.ToArray()));
        Assert.That(Directory.Exists(Path.Combine(output, "emptydir")), Is.True);
        Assert.That(new FileInfo(Path.Combine(output, "empty.txt")).Length, Is.Zero);
      });
    } finally {
      try { Directory.Delete(output, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("HappyPath")]
  public void GivenPpmArchive_WhenListedThroughTheDescriptor_ThenTheDirectoryShowsWithItsMode() {
    var entries = new UharcFormatDescriptor().List(new MemoryStream(Vector("basic-mx.uha")), null);
    Assert.Multiple(() => {
      Assert.That(entries.Count(e => !e.IsDirectory), Is.EqualTo(6));
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Method), Is.All.EqualTo("PPM"));
    });
  }

  [Test, Category("ErrorHandling")]
  public void GivenOlderVersion_WhenListedThroughTheDescriptor_ThenTheRawFileIsListedInsteadOfThrowing() {
    var data = Vector("basic-m2.uha");
    data[3] = 4;
    var entries = new UharcFormatDescriptor().List(new MemoryStream(data), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.uha", "metadata.ini" }));
  }

  [Test, Category("ErrorHandling")]
  public void GivenWrongPassword_WhenListedThroughTheDescriptor_ThenItIsReportedRatherThanHidden()
    => Assert.That(() => new UharcFormatDescriptor().List(new MemoryStream(Vector("basic-m2-pw.uha")), "Secret"),
      Throws.TypeOf<InvalidDataException>().With.Message.Contains("password"));

  // ── Self-extractor ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenArchiveBehindAPeStub_WhenOpened_ThenTheArchiveAfterTheLastSectionIsRead() {
    var archive = Vector("basic-m2.uha");
    var stub = MinimalPe(sectionRawOffset: 0x200, sectionRawSize: 0x400);
    var reader = new UharcReader(new MemoryStream([.. stub, .. archive]));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "hello.txt")), Is.EqualTo("Hello, UHARC!\r\n"u8.ToArray()));
  }

  /// <summary>An MZ/PE header with one section; the image ends where that section's raw data ends.</summary>
  internal static byte[] MinimalPe(int sectionRawOffset, int sectionRawSize) {
    var image = new byte[sectionRawOffset + sectionRawSize];
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    const int pe = 0x80;
    BitConverter.TryWriteBytes(image.AsSpan(0x3C), pe);
    image[pe] = (byte)'P';
    image[pe + 1] = (byte)'E';
    BitConverter.TryWriteBytes(image.AsSpan(pe + 6), (ushort)1);
    BitConverter.TryWriteBytes(image.AsSpan(pe + 20), (ushort)0xE0);
    var section = pe + 24 + 0xE0;
    BitConverter.TryWriteBytes(image.AsSpan(section + 16), sectionRawSize);
    BitConverter.TryWriteBytes(image.AsSpan(section + 20), sectionRawOffset);
    return image;
  }

  // ── Building blocks ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void GivenTableCipher_WhenAppliedTwice_ThenTheInputReturns() {
    var data = Encoding.ASCII.GetBytes("any table bytes at all, of any length 0123456789");
    var copy = data.ToArray();
    UharcCipher.Apply(copy, UharcCipher.DefaultPassword);
    Assert.That(copy, Is.Not.EqualTo(data));
    UharcCipher.Apply(copy, UharcCipher.DefaultPassword);
    Assert.That(copy, Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void GivenExecutableBelowOneKibibyte_WhenFiltered_ThenItIsLeftAlone() {
    var data = new byte[0x3FF];
    data[0] = (byte)'M';
    data[1] = (byte)'Z';
    for (var i = 0x40; i < data.Length - 5; i += 5)
      data[i] = 0xE8;
    data[0x41] = 0x10;
    var copy = data.ToArray();
    UharcExecutableFilter.Restore(copy);
    Assert.That(copy, Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void GivenNonExecutable_WhenFiltered_ThenItIsLeftAlone() {
    var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 3 == 0 ? 0xE8 : i)).ToArray();
    var copy = data.ToArray();
    UharcExecutableFilter.Restore(copy);
    Assert.That(copy, Is.EqualTo(data));
  }

  [TestCase(8, 1024)]
  [TestCase(23, 33554432)]
  [TestCase(40, 1024)]
  [TestCase(56, 1024)]
  [TestCase(77, 32768)]
  [TestCase(87, 33554432)]
  [TestCase(0, 0)]
  [TestCase(29, 32768)]
  [Category("Spec")]
  public void GivenMethodByte_WhenDecoded_ThenTheDictionaryIsTwoToTheBandOffsetPlusTen(int method, int expected)
    => Assert.That(UharcMethods.DictionarySize(method), Is.EqualTo(expected));
}
