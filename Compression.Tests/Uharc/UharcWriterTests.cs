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
  [TestCase("alz", "ALZ:2")]
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
  [TestCase("lzma")]
  [Category("ErrorHandling")]
  public void GivenAMethodTheWriterDoesNotProduce_WhenWritten_ThenItIsRefused(string method)
    => Assert.That(() => Create(method), Throws.TypeOf<NotSupportedException>());

  internal static byte[] CreateWith(FormatCreateOptions options, IReadOnlyList<ArchiveInputInfo>? inputs = null) {
    using var output = new MemoryStream();
    new UharcFormatDescriptor().Create(output, inputs ?? SampleInputs(), options);
    return output.ToArray();
  }

  [TestCase(null)]
  [TestCase("store")]
  [TestCase("rle")]
  [TestCase("lz78")]
  [TestCase("alz")]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenAPassword_WhenWrittenAndReadWithIt_ThenEverythingComesBack(string? method) {
    var inputs = SampleInputs();
    var reader = new UharcReader(new MemoryStream(CreateWith(new FormatCreateOptions(method) { Password = "secret" }, inputs)), "secret");
    Assert.That(reader.IsEncrypted, Is.True);
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == input.ArchiveName)), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
  }

  [Test, Category("ErrorHandling")]
  public void GivenAPasswordArchive_WhenOpenedWithoutOrWithAnotherPassword_ThenItIsRejected() {
    var archive = CreateWith(new FormatCreateOptions { Password = "secret" });
    Assert.Multiple(() => {
      Assert.That(() => new UharcReader(new MemoryStream(archive)), Throws.TypeOf<InvalidOperationException>());
      Assert.That(() => new UharcReader(new MemoryStream(archive), "Secret"), Throws.InstanceOf<InvalidDataException>());
    });
  }

  [TestCase("pässword")]
  [TestCase("tab\tbed")]
  [Category("ErrorHandling")]
  public void GivenAPasswordOutsidePrintableAscii_WhenWritten_ThenItIsRefused(string password)
    => Assert.That(() => CreateWith(new FormatCreateOptions { Password = password }), Throws.TypeOf<NotSupportedException>());

  [TestCase(255, false)]
  [TestCase(256, true)]
  [Category("Boundary")]
  public void GivenALongPassword_WhenWritten_ThenItIsTakenUpTo255Characters(int length, bool refused) {
    var password = new string('p', length);
    if (refused)
      Assert.That(() => CreateWith(new FormatCreateOptions { Password = password }), Throws.TypeOf<NotSupportedException>());
    else
      Assert.That(new UharcReader(new MemoryStream(CreateWith(new FormatCreateOptions { Password = password })), password).Entries, Is.Not.Empty);
  }

  [Test, Category("HappyPath")]
  public void GivenAnSfxStub_WhenWritten_ThenTheArchiveFollowsTheStubAndReadsBack() {
    var stub = UharcTests.MinimalPe(0x200, 0x400);
    var path = Path.Combine(Path.GetTempPath(), "cwb-uharc-stub-" + Guid.NewGuid().ToString("N") + ".exe");
    File.WriteAllBytes(path, stub);
    try {
      var inputs = SampleInputs();
      var sfx = CreateWith(new FormatCreateOptions { FormatSpecific = { [UharcFormatDescriptor.SfxStubOption] = path } }, inputs);
      Assert.That(sfx.AsSpan(0, stub.Length).ToArray(), Is.EqualTo(stub));
      Assert.That(sfx.AsSpan(stub.Length, 4).ToArray(), Is.EqualTo("UHA\x06"u8.ToArray()));
      var reader = new UharcReader(new MemoryStream(sfx));
      var input = inputs.First(i => i.ArchiveName == "readme.txt");
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "readme.txt")), Is.EqualTo(input.InMemoryContent));
    } finally {
      File.Delete(path);
    }
  }

  [Test, Category("ErrorHandling")]
  public void GivenAStubThatIsNoPeImage_WhenWritten_ThenItIsRefused() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-uharc-stub-" + Guid.NewGuid().ToString("N") + ".exe");
    File.WriteAllBytes(path, [.. UharcTests.MinimalPe(0x200, 0x400), 0x00]);
    try {
      Assert.That(() => CreateWith(new FormatCreateOptions { FormatSpecific = { [UharcFormatDescriptor.SfxStubOption] = path } }),
        Throws.TypeOf<NotSupportedException>());
    } finally {
      File.Delete(path);
    }
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

  // ── The ALZ encoder against the decoder ───────────────────────────────

  private static IEnumerable<TestCaseData> AlzInputs() {
    var random = new Random(9);
    var noise = new byte[50000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"the {i % 13} quick brown fox {i * 7 % 101}\r\n")));
    var runs = Enumerable.Range(0, 400).SelectMany(i => Enumerable.Repeat((byte)(i % 5), i % 600)).ToArray();
    yield return new TestCaseData(new byte[] { 0x41 }, 56).SetName("Alz_OneByte");
    yield return new TestCaseData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8.ToArray(), 56).SetName("Alz_RunAfterOneByte");
    yield return new TestCaseData(text, 63).SetName("Alz_Text");
    yield return new TestCaseData(text, 56).SetName("Alz_TextIn1KiBWindow");
    yield return new TestCaseData(runs, 60).SetName("Alz_LongRuns");
    yield return new TestCaseData(noise.Concat(text).Concat(noise).ToArray(), 57).SetName("Alz_MixedIn2KiBWindow");
    yield return new TestCaseData(text.Concat(noise).Concat(text).ToArray(), 76).SetName("Alz_Mode3Band");
  }

  [TestCaseSource(nameof(AlzInputs))]
  [Category("RoundTrip")]
  public void GivenData_WhenAlzEncodedAndDecoded_ThenItIsUnchanged(byte[] data, int method) {
    var packed = UharcAlzEncoder.Encode(data, method, []);
    Assert.That(UharcAlzDecoder.Decode(packed, 0, packed.Length, method, data.Length, []), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void GivenManyFiles_WhenAlzEncodedAndDecoded_ThenTheModelResetsLineUp() {
    var files = Enumerable.Range(0, 30).Select(i => System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat($"file {i} record {i * i}\n", 600 + i * 97)))).ToList();
    var data = files.SelectMany(f => f).ToArray();
    var ends = new List<long>();
    long end = 0;
    foreach (var f in files)
      ends.Add(end += f.Length);
    var packed = UharcAlzEncoder.Encode(data, 66, ends);
    Assert.That(UharcAlzDecoder.Decode(packed, 0, packed.Length, 66, data.Length, ends), Is.EqualTo(data));
  }

  [TestCase(100000L, 0L, 15)]
  [TestCase(100000L, 1024L, 8)]
  [TestCase(100000L, 1500L, 9)]
  [TestCase(100000L, 1L << 30, 15)]
  [TestCase(500L, 65536L, 8)]
  [Category("Boundary")]
  public void GivenADictionaryLimit_WhenTheWindowIsChosen_ThenItNeverExceedsTheLimitRoundedUp(long size, long limit, int method)
    => Assert.That(UharcWriter.LzpMethodFor(size, limit), Is.EqualTo(method));

  [TestCase(0, 8)]
  [TestCase(1024, 8)]
  [TestCase(1025, 9)]
  [TestCase(33554432, 23)]
  [TestCase(40000000, 23)]
  [Category("Boundary")]
  public void GivenAStreamSize_WhenTheDictionaryIsChosen_ThenItIsTheSmallestThatHoldsIt(long size, int method)
    => Assert.That(UharcWriter.LzpMethodFor(size), Is.EqualTo(method));
}
