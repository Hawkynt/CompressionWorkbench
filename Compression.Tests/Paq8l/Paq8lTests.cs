using System.Formats.Tar;
using System.IO.Compression;
using Compression.Registry;
using FileFormat.Paq8l;

namespace Compression.Tests.Paq8l;

/// <summary>
/// paq8l against archives paq8l itself wrote (ReferenceVectors/README.md): ours must decode them
/// to the inputs and, from the same inputs, write them byte for byte.
/// </summary>
[TestFixture]
public sealed class Paq8lTests {

  private static byte[] Resource(string name) {
    using var s = typeof(Paq8lTests).Assembly.GetManifestResourceStream("Paq8lVectors." + name)
      ?? throw new FileNotFoundException(name + " is not embedded");
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly Lazy<Dictionary<string, byte[]>> Inputs = new(() => {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    using var gz = new GZipStream(new MemoryStream(Resource("inputs.tar.gz")), CompressionMode.Decompress);
    using var tar = new TarReader(gz);
    while (tar.GetNextEntry() is { } e) {
      if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
      using var ms = new MemoryStream();
      e.DataStream?.CopyTo(ms);
      files[e.Name.TrimStart('.', '/')] = ms.ToArray();
    }
    return files;
  });

  private static readonly string[] Mixed = ["text.txt", "docs/notes.txt", "code.bin", "image.bmp", "photo.jpg", "one.txt", "empty.txt"];

  public static IEnumerable<TestCaseData> Vectors() {
    foreach (var level in new[] { 0, 1, 4, 5 })
      yield return new TestCaseData($"mixed-l{level}.paq8l", level, Mixed).SetName($"Mixed level {level}");
    yield return new TestCaseData("fax-l5.paq8l", 5, new[] { "fax.bin" }).SetName("Fax bitmap level 5");
    yield return new TestCaseData("records-l5.paq8l", 5, new[] { "records.bin" }).SetName("Records level 5");
  }

  [TestCaseSource(nameof(Vectors))]
  public void GivenAPaq8lArchive_WhenDecoded_ThenEveryFileMatchesItsInput(string archive, int level, string[] names) {
    var read = Paq8lArchive.Read(new MemoryStream(Resource(archive)));
    Assert.That(read.Select(r => r.Entry.Name), Is.EqualTo(names));
    foreach (var (entry, data) in read)
      Assert.That(data, Is.EqualTo(Inputs.Value[entry.Name]), entry.Name);
  }

  [TestCaseSource(nameof(Vectors))]
  public void GivenTheInputs_WhenWritten_ThenTheArchiveIsByteIdenticalToPaq8l(string archive, int level, string[] names) {
    using var ms = new MemoryStream();
    Paq8lArchive.Write(ms, [.. names.Select(n => (n, Inputs.Value[n]))], level);
    var expected = Resource(archive);
    var actual = ms.ToArray();
    Assert.That(actual.Length, Is.EqualTo(expected.Length), "archive size");
    Assert.That(actual, Is.EqualTo(expected));
  }

  [Test]
  public void GivenAnArchive_WhenListed_ThenNamesAndSizesComeFromTheHeader() {
    var list = new Paq8lFormatDescriptor().List(new MemoryStream(Resource("mixed-l5.paq8l")), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(Mixed));
    Assert.That(list.Select(e => e.OriginalSize), Is.EqualTo(Mixed.Select(n => (long)Inputs.Value[n].Length)));
  }

  [Test]
  public void GivenATruncatedArchive_WhenDecoded_ThenItIsRefused() {
    var bytes = Resource("records-l5.paq8l");
    Assert.Throws<InvalidDataException>(() => Paq8lArchive.Read(new MemoryStream(bytes[..^40])));
  }

  [TestCase("paq9a -5\r\n1\ta\r\n\x1A")]
  [TestCase("")]
  [TestCase("paq8l -5\r\nnot a size\r\n\x1A")]
  public void GivenABadHeader_WhenListed_ThenItIsRefused(string header) =>
    Assert.Throws<InvalidDataException>(() => Paq8lArchive.List(new MemoryStream(System.Text.Encoding.Latin1.GetBytes(header))));

  [Test]
  public void GivenAnOutOfRangeLevelDigit_WhenRead_ThenLevelFiveIsUsed() {
    var bytes = Resource("records-l5.paq8l");
    bytes[7] = (byte)'x';
    var (level, _, _) = Paq8lArchive.ReadHeader(new MemoryStream(bytes));
    Assert.That(level, Is.EqualTo(5));
    Assert.That(Paq8lArchive.Read(new MemoryStream(bytes)).Single().Data, Is.EqualTo(Inputs.Value["records.bin"]));
  }

  [TestCase(0)]
  [TestCase(2)]
  [TestCase(3)]
  public void GivenAnyLevel_WhenRoundTripped_ThenTheFilesComeBack(int level) {
    var files = new List<(string, byte[])> { ("a.txt", Inputs.Value["docs/notes.txt"]), ("b/c.bin", Inputs.Value["records.bin"][..3000]) };
    using var ms = new MemoryStream();
    Paq8lArchive.Write(ms, files, level);
    var read = Paq8lArchive.Read(new MemoryStream(ms.ToArray()));
    Assert.That(read.Select(r => r.Data), Is.EqualTo(files.Select(f => f.Item2)));
  }

  [Test]
  public void GivenNoFiles_WhenWritten_ThenItIsRefused() =>
    Assert.Throws<ArgumentException>(() => Paq8lArchive.Write(Stream.Null, [], 1));

  [TestCase("a\tb")]
  [TestCase("a\r\nb")]
  [TestCase("")]
  public void GivenAnUnstorableName_WhenWritten_ThenItIsRefused(string name) =>
    Assert.Throws<ArgumentException>(() => Paq8lArchive.Write(Stream.Null, [(name, [1])], 1));

  [Test]
  public void GivenTheDescriptor_WhenCreating_ThenTheLevelOptionIsUsed() {
    using var ms = new MemoryStream();
    new Paq8lFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("x/y.txt", "hello hello hello\n"u8.ToArray())],
      new FormatCreateOptions { Level = 2 });
    var bytes = ms.ToArray();
    Assert.That(System.Text.Encoding.Latin1.GetString(bytes, 0, 9), Is.EqualTo("paq8l -2\r"));
    Assert.That(Paq8lArchive.Read(new MemoryStream(bytes)).Single().Data, Is.EqualTo("hello hello hello\n"u8.ToArray()));
  }
}
