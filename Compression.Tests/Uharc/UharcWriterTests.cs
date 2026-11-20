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
  [TestCase("rle", "RLE")]
  [TestCase("lz78", "LZ78")]
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

  // ── The RLE and LZ78 encoders against the decoders ─────────────────────

  private static IEnumerable<TestCaseData> RleInputs() {
    var random = new Random(7);
    var noise = new byte[3000];
    random.NextBytes(noise);
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Rle_Empty");
    yield return new TestCaseData(new byte[] { 0xED }).SetName("Rle_TripleMarker");
    yield return new TestCaseData(new byte[] { 0xEE, 0xEE }).SetName("Rle_TwoRunMarkers");
    yield return new TestCaseData(new byte[] { 0xEE, 0xEE, 0xEE }).SetName("Rle_RunMarkerTriple");
    yield return new TestCaseData(Enumerable.Repeat((byte)0xED, 4).ToArray()).SetName("Rle_TripleMarkerRun");
    yield return new TestCaseData(Enumerable.Range(0, 64).SelectMany(i => new byte[] { 0xED, 0xEE, 0xAD, 0xAE, 0x6D, 0x6E, 0x2D, 0x2E, (byte)i }).ToArray()).SetName("Rle_EveryMarkerGeneration");
    foreach (var length in new[] { 2, 3, 4, 254, 255, 256, 65535, 65536, 140000 })
      yield return new TestCaseData(Enumerable.Repeat((byte)'A', length).ToArray()).SetName($"Rle_Run{length}");
    yield return new TestCaseData(noise).SetName("Rle_Noise");
  }

  [TestCaseSource(nameof(RleInputs))]
  [Category("RoundTrip")]
  public void GivenData_WhenRleEncodedAndDecoded_ThenItIsUnchanged(byte[] data) {
    var packed = UharcRleEncoder.Encode(data);
    Assert.That(UharcRleDecoder.Decode(packed, 0, packed.Length, data.Length), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void GivenTheMeasuredMarkerInput_WhenRleEncoded_ThenTheStreamIsUharcs()
    => Assert.That(UharcRleEncoder.Encode([0x78, 0xEE, 0xAE, 0xAD, 0x41, 0x41, 0x41, 0x79]),
      Is.EqualTo(new byte[] { 0x78, 0xEE, 0x01, 0xAE, 0x01, 0xAD, 0x6D, 0x41, 0x79 }));

  private static IEnumerable<TestCaseData> Lz78Inputs() {
    var random = new Random(8);
    var noise = new byte[60000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 6000).Select(i => $"the {i % 13} quick brown fox {i * 7 % 101}\r\n")));
    yield return new TestCaseData(Array.Empty<byte>(), 7).SetName("Lz78_Empty");
    yield return new TestCaseData(new byte[] { 0x41 }, 7).SetName("Lz78_OneByte");
    yield return new TestCaseData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8.ToArray(), 7).SetName("Lz78_RepeatedByte");
    yield return new TestCaseData(text, 7).SetName("Lz78_Text");
    yield return new TestCaseData(text, 2).SetName("Lz78_TextPrunedOften");
    yield return new TestCaseData(noise, 2).SetName("Lz78_NoisePrunedOften");
    yield return new TestCaseData(noise.Concat(text).Concat(new byte[50000]).ToArray(), 3).SetName("Lz78_Mixed");
  }

  [TestCaseSource(nameof(Lz78Inputs))]
  [Category("RoundTrip")]
  public void GivenData_WhenLz78EncodedAndDecoded_ThenItIsUnchanged(byte[] data, int method) {
    var packed = UharcLz78Encoder.Encode(data, method);
    Assert.That(UharcLz78Decoder.Decode(packed, 0, packed.Length, method, data.Length), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void GivenDistinctBytes_WhenLz78Encoded_ThenEachTakesItsOwnByte()
    => Assert.That(UharcLz78Encoder.Encode("ABCDEFGH"u8, 7), Is.EqualTo("ABCDEFGH"u8.ToArray()));

  [TestCase(0, 8)]
  [TestCase(1024, 8)]
  [TestCase(1025, 9)]
  [TestCase(33554432, 23)]
  [TestCase(40000000, 23)]
  [Category("Boundary")]
  public void GivenAStreamSize_WhenTheDictionaryIsChosen_ThenItIsTheSmallestThatHoldsIt(long size, int method)
    => Assert.That(UharcWriter.LzpMethodFor(size), Is.EqualTo(method));
}
