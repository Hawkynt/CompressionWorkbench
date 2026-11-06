using Compression.Registry;
using FileFormat.Uharc;

namespace Compression.Tests.Uharc;

/// <summary>
/// The UHARC 0.6 writer and its LZP encoder against our reader; <see cref="UharcExternalConformanceTests"/>
/// hands the same archives to uharc.exe.
/// </summary>
[TestFixture]
public class UharcWriterTests {

  internal static IReadOnlyList<ArchiveInputInfo> SampleInputs() {
    var random = new Random(1997);
    var noise = new byte[5000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i % 37} of the sample\r\n")));
    var stamp = new DateTimeOffset(2005, 10, 1, 10, 0, 0, TimeSpan.Zero);
    ArchiveEntryMetadata Meta() => new(LastWriteTimeUtc: stamp);
    return [
      ArchiveInputInfo.InMemory("readme.txt", text, Meta()),
      ArchiveInputInfo.InMemory("empty.dat", [], Meta()),
      ArchiveInputInfo.InMemory("noise.bin", noise, Meta()),
      ArchiveInputInfo.InMemory("sub/deeper/one.bin", [0x42], Meta()),
      ArchiveInputInfo.InMemory("sub/zeros.bin", new byte[70000], Meta()),
      new("emptydir", "emptydir", IsDirectory: true),
    ];
  }

  internal static byte[] Create(string? method, IReadOnlyList<ArchiveInputInfo>? inputs = null) {
    using var output = new MemoryStream();
    new UharcFormatDescriptor().Create(output, inputs ?? SampleInputs(), new FormatCreateOptions(method));
    return output.ToArray();
  }

  [TestCase(null, "LZP")]
  [TestCase("lzp", "LZP")]
  [TestCase("store", "Store")]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenFilesAndDirectories_WhenWrittenAndRead_ThenEverythingComesBack(string? method, string mode) {
    var inputs = SampleInputs();
    var reader = new UharcReader(new MemoryStream(Create(method, inputs)));
    Assert.That(reader.MethodName, Is.EqualTo(mode));
    foreach (var input in inputs.Where(i => !i.IsDirectory)) {
      var entry = reader.Entries.Single(e => e.FileName == input.ArchiveName);
      Assert.Multiple(() => {
        Assert.That(reader.Extract(entry), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
        Assert.That(entry.LastModified, Is.EqualTo(new DateTime(2005, 10, 1, 10, 0, 0, DateTimeKind.Utc).ToLocalTime()));
      });
    }

    Assert.That(reader.Entries.Where(e => e.IsDirectory).Select(e => e.FileName).Order(), Is.EqualTo(new[] { "emptydir", "sub", "sub/deeper" }));
  }

  [Test, Category("Boundary")]
  public void GivenNoInputs_WhenWritten_ThenAnEmptyArchiveReadsBack() {
    var reader = new UharcReader(new MemoryStream(Create(null, [])));
    Assert.That(reader.Entries, Is.Empty);
  }

  [TestCase("ppm")]
  [TestCase("alz")]
  [Category("ErrorHandling")]
  public void GivenAMethodTheWriterDoesNotProduce_WhenWritten_ThenItIsRefused(string method)
    => Assert.That(() => Create(method), Throws.TypeOf<NotSupportedException>());

  [Test, Category("ErrorHandling")]
  public void GivenAPassword_WhenWritten_ThenItIsRefused() {
    using var output = new MemoryStream();
    Assert.That(() => new UharcFormatDescriptor().Create(output, SampleInputs(), new FormatCreateOptions { Password = "secret" }),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test, Category("ErrorHandling")]
  public void GivenANameLongerThan255Bytes_WhenWritten_ThenItIsRefused()
    => Assert.That(() => Create(null, [ArchiveInputInfo.InMemory(new string('n', 256), [1])]), Throws.TypeOf<NotSupportedException>());

  // ── The LZP encoder against the decoder ────────────────────────────────

  private static IEnumerable<TestCaseData> LzpInputs() {
    var random = new Random(6);
    var noise = new byte[40000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"the {i % 11} quick brown fox {i % 7}\r\n")));
    yield return new TestCaseData(Array.Empty<byte>(), 17).SetName("Lzp_Empty");
    yield return new TestCaseData(new byte[] { 0x99 }, 17).SetName("Lzp_OneByte");
    yield return new TestCaseData(new byte[100000], 17).SetName("Lzp_LongRun");
    yield return new TestCaseData(noise, 17).SetName("Lzp_Noise");
    yield return new TestCaseData(text, 17).SetName("Lzp_Text");
    yield return new TestCaseData(text, 8).SetName("Lzp_TextIn1KiBWindow");
    yield return new TestCaseData(text.Concat(noise).Concat(text).ToArray(), 9).SetName("Lzp_MixedIn2KiBWindow");
  }

  [TestCaseSource(nameof(LzpInputs))]
  [Category("RoundTrip")]
  public void GivenData_WhenLzpEncodedAndDecoded_ThenItIsUnchanged(byte[] data, int method) {
    var packed = UharcLzpEncoder.Encode(data, method, []);
    Assert.That(UharcLzpDecoder.Decode(packed, 0, packed.Length, method, data.Length, []), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void GivenFileBoundaries_WhenLzpEncodedAndDecoded_ThenTheModelResetsLineUp() {
    var files = Enumerable.Range(0, 12).Select(i => System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat($"file {i} record\n", 2000 + i * 300)))).ToList();
    var data = files.SelectMany(f => f).ToArray();
    var ends = new List<long>();
    long end = 0;
    foreach (var f in files)
      ends.Add(end += f.Length);
    var packed = UharcLzpEncoder.Encode(data, 20, ends);
    Assert.That(UharcLzpDecoder.Decode(packed, 0, packed.Length, 20, data.Length, ends), Is.EqualTo(data));
  }

  [TestCase(0, 8)]
  [TestCase(1024, 8)]
  [TestCase(1025, 9)]
  [TestCase(33554432, 23)]
  [TestCase(40000000, 23)]
  [Category("Boundary")]
  public void GivenAStreamSize_WhenTheDictionaryIsChosen_ThenItIsTheSmallestThatHoldsIt(long size, int method)
    => Assert.That(UharcWriter.LzpMethodFor(size), Is.EqualTo(method));
}
