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
  [TestCase("ppm", "PPM")]
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

  private static IEnumerable<TestCaseData> NoFiles() {
    yield return new TestCaseData("6", (IReadOnlyList<ArchiveInputInfo>)[]).SetName("NoFiles_v6_Nothing");
    yield return new TestCaseData("6", (IReadOnlyList<ArchiveInputInfo>)[new("dir", "dir", IsDirectory: true)]).SetName("NoFiles_v6_OnlyADirectory");
    yield return new TestCaseData("4", (IReadOnlyList<ArchiveInputInfo>)[]).SetName("NoFiles_v4_Nothing");
    yield return new TestCaseData("3", (IReadOnlyList<ArchiveInputInfo>)[]).SetName("NoFiles_v3_Nothing");
    yield return new TestCaseData("2", (IReadOnlyList<ArchiveInputInfo>)[]).SetName("NoFiles_v2_Nothing");
  }

  /// <summary>UHARC.EXE, UHARC04.EXE and uharc_u.exe all stop on an archive of no files, so none is written.</summary>
  [TestCaseSource(nameof(NoFiles))]
  [Category("Boundary"), Category("Exceptional")]
  public void GivenNoFiles_WhenWritten_ThenTheArchiveIsRefused(string version, IReadOnlyList<ArchiveInputInfo> inputs)
    => Assert.That(() => CreateWith(new FormatCreateOptions { FormatSpecific = { [UharcFormatDescriptor.VersionOption] = version } }, inputs),
      Throws.TypeOf<NotSupportedException>());

  [Test, Category("Boundary")]
  public void GivenOneEmptyFile_WhenWritten_ThenItReadsBack() {
    var reader = new UharcReader(new MemoryStream(Create(null, [ArchiveInputInfo.InMemory("empty.dat", [])])));
    Assert.That(reader.Entries.Single().Size, Is.Zero);
  }

  [TestCase("deflate")]
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
  [TestCase("ppm")]
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

  // ── UHARC 0.4 archives ─────────────────────────────────────────────────

  private static FormatCreateOptions Version4(string? method, string? password = null) => new(method) {
    Password = password,
    FormatSpecific = { [UharcFormatDescriptor.VersionOption] = "4" },
  };

  [TestCase("store", "Store", null)]
  [TestCase("rle", "RLE", null)]
  [TestCase("lz78", "LZW", null)]
  [TestCase(null, "LZP", null)]
  [TestCase("lzp", "LZP", "secret")]
  [TestCase("alz", "ALZ:2", null)]
  [TestCase("alz", "ALZ:2", "secret")]
  [TestCase("ppm", "PPM", null)]
  [TestCase("ppm", "PPM", "secret")]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenFilesAndDirectories_WhenWrittenAsVersion04AndRead_ThenEverythingComesBack(string? method, string mode, string? password) {
    var inputs = SampleInputs();
    var archive = CreateWith(Version4(method, password), inputs);
    Assert.That(archive[3], Is.EqualTo(4));
    var reader = new UharcReader(new MemoryStream(archive), password);
    Assert.Multiple(() => {
      Assert.That(reader.MethodName, Is.EqualTo(mode));
      Assert.That(reader.IsEncrypted, Is.EqualTo(password != null));
    });
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == input.ArchiveName)), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
    Assert.That(reader.Entries.Where(e => e.IsDirectory).Select(e => e.FileName).Order(), Is.EqualTo(new[] { "emptydir", "sub", "sub/deeper" }));
  }

  [TestCase(24)]
  [TestCase(33)]
  [TestCase(39)]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion04PpmEncodedAndDecoded_ThenItIsUnchanged(int method) {
    var data = MixedData(method);
    var ends = new long[] { data.Length / 3, data.Length };
    var packed = Uharc04PpmEncoder.Encode(data, method, ends);
    Assert.That(Uharc04PpmDecoder.Decode(packed, 0, packed.Length, method, data.Length, ends), Is.EqualTo(data));
  }

  [TestCase(new byte[0])]
  [TestCase(new byte[] { 0x42 })]
  [TestCase(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
  [Category("Boundary")]
  public void GivenTinyInputs_WhenVersion04PpmEncodedAndDecoded_ThenTheyAreUnchanged(byte[] data) {
    long[] ends = [data.Length];
    var packed = Uharc04PpmEncoder.Encode(data, 24, ends);
    Assert.That(Uharc04PpmDecoder.Decode(packed, 0, packed.Length, 24, data.Length, ends), Is.EqualTo(data));
  }

  // ── UHARC 0.3 archives ─────────────────────────────────────────────────

  private static FormatCreateOptions Version3(string? method, string? password = null, long dictionary = 0) => new(method) {
    Password = password,
    DictSize = dictionary,
    FormatSpecific = { [UharcFormatDescriptor.VersionOption] = "3" },
  };

  [TestCase("store", "Store", null, 0L)]
  [TestCase("rle", "RLE", null, 0L)]
  [TestCase("lz78", "LZW", null, 0L)]
  [TestCase(null, "LZP", null, 0L)]
  [TestCase("lzp", "LZP", "secret", 1024L)]
  [TestCase("alz", "ALZ:2", null, 0L)]
  [TestCase("alz", "ALZ:2", "secret", 1024L)]
  [TestCase("ppm", "PPM", null, 0L)]
  [TestCase("ppm", "PPM", "secret", 1024L)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenFilesAndDirectories_WhenWrittenAsVersion03AndRead_ThenEverythingComesBack(string? method, string mode, string? password, long dictionary) {
    var inputs = SampleInputs();
    var archive = CreateWith(Version3(method, password, dictionary), inputs);
    Assert.That(archive[3], Is.EqualTo(3));
    var reader = new UharcReader(new MemoryStream(archive), password);
    Assert.That(reader.MethodName, Is.EqualTo(mode));
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == input.ArchiveName)), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
    Assert.That(reader.Entries.Where(e => e.IsDirectory).Select(e => e.FileName).Order(), Is.EqualTo(new[] { "emptydir", "sub", "sub/deeper" }));
  }

  [TestCase(22)]
  [TestCase(28)]
  [TestCase(35)]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion03PpmEncodedAndDecoded_ThenItIsUnchanged(int method) {
    var data = MixedData(method);
    var ends = new long[] { data.Length / 3, data.Length };
    var packed = Uharc03PpmEncoder.Encode(data, method, ends);
    Assert.That(Uharc03PpmDecoder.Decode(packed, 0, packed.Length, method, data.Length, ends), Is.EqualTo(data));
  }

  [TestCase(new byte[0])]
  [TestCase(new byte[] { 0x42 })]
  [TestCase(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
  [Category("Boundary")]
  public void GivenTinyInputs_WhenVersion03PpmEncodedAndDecoded_ThenTheyAreUnchanged(byte[] data) {
    long[] ends = [data.Length];
    var packed = Uharc03PpmEncoder.Encode(data, 22, ends);
    Assert.That(Uharc03PpmDecoder.Decode(packed, 0, packed.Length, 22, data.Length, ends), Is.EqualTo(data));
  }

  [TestCase(8)]
  [TestCase(17)]
  [TestCase(21)]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion03LzpEncodedAndDecoded_ThenItIsUnchanged(int method) {
    var data = MixedData(method);
    var packed = Uharc03LzpEncoder.Encode(data, method, []);
    Assert.That(Uharc03LzpDecoder.Decode(packed, 0, packed.Length, method, data.Length, []), Is.EqualTo(data));
  }

  [TestCase(36)]
  [TestCase(50)]
  [TestCase(77)]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion03AlzEncodedAndDecoded_ThenItIsUnchanged(int method) {
    var data = MixedData(method);
    var packed = Uharc03AlzEncoder.Encode(data, method, []);
    Assert.That(Uharc03AlzDecoder.Decode(packed, 0, packed.Length, method, data.Length), Is.EqualTo(data));
  }

  [TestCase(36)]
  [TestCase(50)]
  [Category("RoundTrip")]
  public void GivenMediaBetweenText_WhenVersion03AlzEncodedInTheirGroups_ThenTheStreamIsUnchanged(int method) {
    // Text, BMP, every WAV kind, an empty file, text again: groups 3, 5, 2, 2, 4 and back to 0.
    byte[][] files = [
      MixedData(1)[..3000], Bitmap24(60, 40), Wave(3000, 1, 8), Wave(2000, 2, 8), Wave(2000, 1, 16), Wave(1500), [],
      MixedData(2)[..3000],
    ];
    var data = files.SelectMany(f => f).ToArray();
    var ends = new List<long>();
    long end = 0;
    foreach (var f in files)
      ends.Add(end += f.Length);
    var packed = Uharc03AlzEncoder.Encode(data, method, ends);
    Assert.That(Uharc03AlzDecoder.Decode(packed, 0, packed.Length, method, data.Length), Is.EqualTo(data));
  }

  [TestCase(3, 0, new[] { 3, 9 })]
  [TestCase(5, 0, new[] { 5, 9 })]
  [TestCase(7, 0, new[] { 2, 9 })]
  [TestCase(8, 0, new[] { 2, 8 })]
  [TestCase(9, 0, new[] { 4 })]
  [TestCase(0, 4, new[] { 0, 9 })]
  [TestCase(0, 0, new int[0])]
  [TestCase(8, 2, new int[0])]
  [Category("HappyPath"), Category("Boundary")]
  public void GivenAMediaClass_WhenTheVersion03AlzSwitchesAreChosen_ThenTheGroupAndStepFollowIt(int mediaClass, int group, int[] switches)
    => Assert.That(Uharc03AlzEncoder.SwitchesFor(mediaClass, group), Is.EqualTo(switches));

  [TestCase(new byte[0])]
  [TestCase(new byte[] { 0x42 })]
  [TestCase(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
  [Category("Boundary")]
  public void GivenTinyInputs_WhenVersion03LzpEncodedAndDecoded_ThenTheyAreUnchanged(byte[] data) {
    var packed = Uharc03LzpEncoder.Encode(data, 8, []);
    Assert.That(Uharc03LzpDecoder.Decode(packed, 0, packed.Length, 8, data.Length, []), Is.EqualTo(data));
  }

  // ── UHARC 0.2 archives ─────────────────────────────────────────────────

  private static FormatCreateOptions Version2(string? method, long dictionary = 0) => new(method) {
    DictSize = dictionary,
    FormatSpecific = { [UharcFormatDescriptor.VersionOption] = "2" },
  };

  [TestCase("store", "Store", 0L)]
  [TestCase("rle", "RLE", 0L)]
  [TestCase("lz78", "LZW", 0L)]
  [TestCase("alz", "m2", 0L)]
  [TestCase("alz", "m2", 1024L)]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenFilesAndDirectories_WhenWrittenAsVersion02AndRead_ThenEverythingComesBackInUpperCase(string method, string mode, long dictionary) {
    var inputs = SampleInputs();
    var archive = CreateWith(Version2(method, dictionary), inputs);
    Assert.That(archive[3], Is.EqualTo(2));
    var reader = new UharcReader(new MemoryStream(archive));
    Assert.That(reader.MethodName, Is.EqualTo(mode));
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == input.ArchiveName.ToUpperInvariant())), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
    Assert.That(reader.Entries.Where(e => e.IsDirectory).Select(e => e.FileName).Order(), Is.EqualTo(new[] { "EMPTYDIR", "SUB", "SUB/DEEPER" }));
  }

  [TestCase("lzp")]
  [TestCase("ppm")]
  [Category("ErrorHandling")]
  public void GivenAMethodVersion02CannotWrite_WhenWritten_ThenItIsRefused(string method)
    => Assert.That(() => CreateWith(Version2(method)), Throws.TypeOf<NotSupportedException>());

  [TestCase("LONGFILENAME.TXT")]
  [TestCase("NAME.HTML")]
  [TestCase("TWO.DOTS.TXT")]
  [TestCase("SPACE NAME.TXT")]
  [TestCase("LONGDIRECTORY/A.TXT")]
  [Category("ErrorHandling")]
  public void GivenANameThatIsNoDos83Name_WhenWrittenAsVersion02_ThenItIsRefused(string name)
    => Assert.That(() => CreateWith(Version2("alz"), [ArchiveInputInfo.InMemory(name, [1, 2, 3])]), Throws.TypeOf<NotSupportedException>());

  [Test, Category("ErrorHandling")]
  public void GivenAPassword_WhenWrittenAsVersion02_ThenItIsRefused()
    => Assert.That(() => CreateWith(new FormatCreateOptions("alz") { Password = "secret", FormatSpecific = { [UharcFormatDescriptor.VersionOption] = "2" } }),
      Throws.TypeOf<NotSupportedException>());

  private static IEnumerable<TestCaseData> LegacyExecutables() {
    var random = new Random(97);
    for (var size = 0x3FF; size <= 0x4400; size += 0x1001) {
      var image = new byte[size];
      random.NextBytes(image);
      image[0] = (byte)'M';
      image[1] = (byte)'Z';
      for (var i = 2; i < size - 5; i += random.Next(2, 9)) {
        image[i] = random.Next(2) == 0 ? (byte)0xE8 : (byte)0xE9;
        var operand = random.Next(6) switch {
          0 => -i,
          1 => 0,
          2 => size - i + random.Next(0, Math.Max(1, i)),
          3 => random.Next(-i, size - i),
          4 => int.MinValue + random.Next(1000),
          _ => random.Next(),
        };
        BitConverter.TryWriteBytes(image.AsSpan(i + 1), operand);
      }

      yield return new TestCaseData(image).SetName($"Legacy_{size:X}");
    }
  }

  [TestCaseSource(nameof(LegacyExecutables))]
  [Category("RoundTrip")]
  public void GivenAnExecutable_WhenTheVersion02FilterIsAppliedAndRestored_ThenItIsUnchanged(byte[] image) {
    var filtered = image.ToArray();
    UharcExecutableFilter.ApplyLegacy(filtered);
    UharcExecutableFilter.RestoreLegacy(filtered);
    Assert.That(filtered, Is.EqualTo(image));
  }

  [TestCase(8)]
  [TestCase(23)]
  [TestCase(43)]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion02MainCoderEncodedAndDecoded_ThenItIsUnchanged(int method) {
    var data = MixedData(method);
    var packed = Uharc02Encoder.Encode(data, method);
    Assert.That(Uharc02Decoder.Decode(packed, 0, packed.Length, method, data.Length), Is.EqualTo(data));
  }

  private static byte[] MixedData(int seed) {
    var random = new Random(seed);
    var noise = new byte[30000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"the {i % 13} quick brown fox {i * 7 % 101}\r\n")));
    var runs = Enumerable.Range(0, 300).SelectMany(i => Enumerable.Repeat((byte)(i % 5), i % 600)).ToArray();
    return text.Concat(noise).Concat(runs).Concat(new byte[20000]).Concat(text).ToArray();
  }

  [TestCase("1")]
  [TestCase("5")]
  [Category("ErrorHandling")]
  public void GivenAnUnwrittenVersion_WhenWritten_ThenItIsRefused(string version)
    => Assert.That(() => CreateWith(new FormatCreateOptions { FormatSpecific = { [UharcFormatDescriptor.VersionOption] = version } }),
      Throws.TypeOf<NotSupportedException>());

  private static IEnumerable<TestCaseData> LzwInputs() {
    var random = new Random(11);
    var noise = new byte[80000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 9000).Select(i => $"{i * 7919 % 100003:x} ")));
    yield return new TestCaseData(new byte[] { 0x42 }, 7).SetName("Lzw_OneByte");
    yield return new TestCaseData("ABABABABABABABABAB"u8.ToArray(), 2).SetName("Lzw_Repeats");
    yield return new TestCaseData(new byte[200000], 2).SetName("Lzw_LongZeroRunRestarts");
    yield return new TestCaseData(text, 2).SetName("Lzw_TextRestartsOften");
    yield return new TestCaseData(text.Concat(noise).ToArray(), 7).SetName("Lzw_Mixed");
  }

  [TestCaseSource(nameof(LzwInputs))]
  [Category("RoundTrip")]
  public void GivenData_WhenLzwEncodedAndDecoded_ThenItIsUnchanged(byte[] data, int method) {
    var packed = Uharc02LzwEncoder.Encode(data, method);
    Assert.That(Uharc02LzwDecoder.Decode(packed, 0, packed.Length, method, data.Length), Is.EqualTo(data));
  }

  [TestCase(56)]
  [TestCase(63)]
  [TestCase(76)]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion04AlzEncodedAndDecoded_ThenItIsUnchanged(int method) {
    var random = new Random(method);
    var noise = new byte[30000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"the {i % 13} quick brown fox {i * 7 % 101}\r\n")));
    var runs = Enumerable.Range(0, 300).SelectMany(i => Enumerable.Repeat((byte)(i % 5), i % 600)).ToArray();
    var data = text.Concat(noise).Concat(runs).Concat(text).ToArray();
    var ends = new List<long> { text.Length, text.Length + noise.Length, data.Length - text.Length, data.Length };
    var packed = Uharc04AlzEncoder.Encode(data, method, ends);
    Assert.That(Uharc04AlzDecoder.Decode(packed, 0, packed.Length, method, data.Length, ends), Is.EqualTo(data));
  }

  [TestCase(2, 0)]
  [TestCase(13, 11)]
  [TestCase(14, 12)]
  [TestCase(16, 13)]
  [TestCase(254, 22)]
  [TestCase(255, 23)]
  [Category("Boundary")]
  public void GivenACopyLength_WhenItsVersion04SymbolIsChosen_ThenTheSymbolCoversIt(int length, int symbol) {
    Assert.Multiple(() => {
      Assert.That(Uharc04AlzModel<UharcEncoding>.LengthSymbol(length), Is.EqualTo(symbol));
      Assert.That(Uharc04AlzModel<UharcEncoding>.LengthInSymbol(length, symbol), Is.True);
    });
  }

  [TestCase(17)]
  [TestCase(8)]
  [TestCase(23)]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion04LzpEncodedAndDecoded_ThenItIsUnchanged(int method) {
    var random = new Random(method);
    var noise = new byte[30000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"the {i % 13} quick brown fox {i * 7 % 101}\r\n")));
    var data = text.Concat(noise).Concat(new byte[70000]).Concat(text).ToArray();
    var packed = Uharc04LzpEncoder.Encode(data, method, []);
    Assert.That(Uharc04LzpDecoder.Decode(packed, 0, packed.Length, method, data.Length, []), Is.EqualTo(data));
  }

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

  /// <summary>Streams measured against UHARC 0.2's extractor under DOSBox (see docs, "UHARC 0.2", RLE).</summary>
  [TestCase("EE0541", "4141414141")]
  [TestCase("ED41", "414141")]
  [TestCase("EE00AD41", "ED414141")]
  [TestCase("EE03414243", "EEEE414243")]
  [Category("HappyPath")]
  public void GivenAVersion02RleStream_WhenDecoded_ThenItGivesWhatUharc02Gave(string stream, string expected) {
    var packed = Convert.FromHexString(stream);
    var want = Convert.FromHexString(expected);
    Assert.That(UharcRleDecoder.Decode(packed, 0, packed.Length, want.Length, byteFirst: true), Is.EqualTo(want));
  }

  [Test, Category("HappyPath")]
  public void GivenTheVersion02LongRun_WhenCodedBothWays_ThenTheByteComesBeforeTheCount() {
    var data = Enumerable.Repeat((byte)0x41, 272).Concat(new byte[] { 0x42, 0x43 }).ToArray();
    Assert.Multiple(() => {
      Assert.That(UharcRleEncoder.Encode(data, byteFirst: true), Is.EqualTo(Convert.FromHexString("EEFF4110014243")));
      Assert.That(UharcRleDecoder.Decode(Convert.FromHexString("EEFF4110014243"), 0, 7, data.Length, byteFirst: true), Is.EqualTo(data));
      Assert.That(UharcRleEncoder.Encode(data), Is.EqualTo(Convert.FromHexString("EEFF1001414243")));
    });
  }

  [TestCaseSource(nameof(RleInputs))]
  [Category("RoundTrip")]
  public void GivenData_WhenVersion02RleEncodedAndDecoded_ThenItIsUnchanged(byte[] data) {
    var packed = UharcRleEncoder.Encode(data, byteFirst: true);
    Assert.That(UharcRleDecoder.Decode(packed, 0, packed.Length, data.Length, byteFirst: true), Is.EqualTo(data));
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

  // ── The PPM encoder against the decoder ───────────────────────────────

  private static IEnumerable<TestCaseData> PpmInputs() {
    var random = new Random(10);
    var noise = new byte[40000];
    random.NextBytes(noise);
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(i => $"the {i % 13} quick brown fox {i * 7 % 101}\r\n")));
    var wave = Enumerable.Range(0, 20000).SelectMany(i => BitConverter.GetBytes((short)(Math.Sin(i * 0.02) * 12000))).ToArray();
    var records = Enumerable.Range(0, 3000).SelectMany(i => new byte[] { 0x03, 0x02, (byte)i, 0, (byte)(i * 7), 0xFE, 0xFF, 0x45, 0, 0 }).ToArray();
    yield return new TestCaseData(new byte[] { 0x41 }, 24).SetName("Ppm_OneByte");
    yield return new TestCaseData(new byte[] { 0x03, 0x02, 0xFF, 0x03, 0xFF, 0x45 }, 24).SetName("Ppm_SlotRepeat");
    yield return new TestCaseData(new byte[70000], 31).SetName("Ppm_LongRun");
    yield return new TestCaseData(text, 31).SetName("Ppm_Text");
    yield return new TestCaseData(text, 24).SetName("Ppm_TextIn1KiBWindow");
    yield return new TestCaseData(noise, 30).SetName("Ppm_Noise");
    yield return new TestCaseData(wave, 31).SetName("Ppm_Wave");
    yield return new TestCaseData(records, 29).SetName("Ppm_Records");
  }

  [TestCaseSource(nameof(PpmInputs))]
  [Category("RoundTrip")]
  public void GivenData_WhenPpmEncodedAndDecoded_ThenItIsUnchanged(byte[] data, int method) {
    var packed = UharcPpmEncoder.Encode(data, method, []);
    Assert.That(UharcPpmDecoder.Decode(packed, 0, packed.Length, method, data.Length, []), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void GivenManyFiles_WhenPpmEncodedAndDecoded_ThenTheModelResetsLineUp() {
    var files = Enumerable.Range(0, 30).Select(i => System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat($"file {i} record {i * i}\n", 400 + i * 97)))).ToList();
    var data = files.SelectMany(f => f).ToArray();
    var ends = new List<long>();
    long end = 0;
    foreach (var f in files)
      ends.Add(end += f.Length);
    var packed = UharcPpmEncoder.Encode(data, 34, ends);
    Assert.That(UharcPpmDecoder.Decode(packed, 0, packed.Length, 34, data.Length, ends), Is.EqualTo(data));
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

  // ── Filters ────────────────────────────────────────────────────────────

  /// <summary>A one-bit page of the fax size with a drawn diagonal and frame.</summary>
  internal static byte[] FaxPage() {
    var page = new byte[UharcFaxFilter.PageSize];
    for (var row = 0; row < 2376; ++row) {
      page[row * 216] = 0x80;
      page[row * 216 + (row % 1728) / 8] |= (byte)(0x80 >> (row % 8));
    }

    page.AsSpan(0, 216).Fill(0xFF);
    return page;
  }

  /// <summary>A PCM WAV file of a slow sine (16-bit stereo unless told otherwise), header included.</summary>
  internal static byte[] Wave(int frames, int channels = 2, int bits = 16) {
    var block = channels * bits / 8;
    var data = new byte[44 + frames * block];
    "RIFF"u8.CopyTo(data);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
    "WAVEfmt "u8.CopyTo(data.AsSpan(8));
    data[16] = 16;
    data[20] = 1;
    data[22] = (byte)channels;
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 22050);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 22050 * block);
    data[32] = (byte)block;
    data[34] = (byte)bits;
    "data"u8.CopyTo(data.AsSpan(36));
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), frames * block);
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < channels; ++c) {
        var value = Math.Sin(i / (20.0 + 7 * c)) * 0.4;
        var at = 44 + i * block + c * bits / 8;
        if (bits == 8)
          data[at] = (byte)(128 + value * 250);
        else
          System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(at), (short)(value * 30000));
      }

    return data;
  }

  /// <summary>An uncompressed 24-bit BMP of smooth gradients.</summary>
  internal static byte[] Bitmap24(int width, int height, int bits = 24) {
    var row = (width * bits / 8 + 3) & ~3;
    var data = new byte[54 + row * height];
    data[0] = (byte)'B';
    data[1] = (byte)'M';
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
    data[10] = 54;
    data[14] = 40;
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height);
    data[26] = 1;
    data[28] = (byte)bits;
    for (var y = 0; y < height; ++y)
      for (var x = 0; x < width * bits / 8; ++x)
        data[54 + y * row + x] = (byte)(x * 2 + y + (x % 3) * 40);
    return data;
  }

  private static IEnumerable<TestCaseData> Executables()
    => ExecutableImages().Select((image, k) => new TestCaseData(image).SetName($"ExecutableFilter_{k}_{image.Length:X}"));

  /// <summary>MZ, ELF and relocation-table images dense with E8/E9 operands of every kind the filter tells apart.</summary>
  internal static IEnumerable<byte[]> ExecutableImages() {
    var random = new Random(2026);
    foreach (var (kind, size) in new[] { ("pe", 0x5000), ("pe", 0x40000), ("mz-relocations", 0x3000), ("elf", 0x2000), ("tight-lows", 0x8000) }) {
      var image = new byte[size];
      random.NextBytes(image);
      switch (kind) {
        case "elf":
          image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F'; image[0x12] = 3; image[0x13] = 0;
          break;
        case "mz-relocations":
          image[0] = (byte)'M'; image[1] = (byte)'Z';
          System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6), 40);
          System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x18), 0x40);
          break;
        default:
          image[0] = (byte)'M'; image[1] = (byte)'Z';
          System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(6), 3);
          break;
      }

      for (var i = 0x200; i < size - 20; i += random.Next(2, 12)) {
        image[i] = random.Next(2) == 0 ? (byte)0xE8 : (byte)0xE9;
        var operand = random.Next(7) switch {
          0 => random.Next(-i, size - i),
          1 => -i,
          2 => 0,
          3 => size - i + random.Next(0, i),
          // Far targets with a nontrivial high half, low halves repeating and in the index range.
          4 => (random.Next(2, 0x7000) << 16) | random.Next(0x8000, 0x8080),
          5 => (random.Next(2, 0x7000) << 16) | ((0x4000 - i) & 0xFFFF),
          _ => random.Next(),
        };
        if (kind == "tight-lows")
          operand = (random.Next(2, 0x7000) << 16) | random.Next(0x7FF0, 0x8090);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(i + 1), operand);
      }

      yield return image;
    }
  }

  [TestCaseSource(nameof(Executables))]
  [Category("RoundTrip")]
  public void GivenAnExecutable_WhenTheFilterIsAppliedAndRestored_ThenItIsUnchanged(byte[] image) {
    var filtered = image.ToArray();
    Assert.That(UharcExecutableFilter.Apply(filtered, out var encodable), Is.True);
    Assert.Multiple(() => {
      Assert.That(encodable, Is.True);
      Assert.That(filtered, Is.Not.EqualTo(image));
    });
    UharcExecutableFilter.Restore(filtered);
    Assert.That(filtered, Is.EqualTo(image));
  }

  [TestCaseSource(nameof(Executables))]
  [Category("RoundTrip")]
  public void GivenAnExecutable_WhenThePpmFilterWithTheOperandSwapIsAppliedAndRestored_ThenItIsUnchanged(byte[] image) {
    var filtered = image.ToArray();
    Assert.That(UharcExecutableFilter.Apply(filtered, out var encodable, swapOperandBytes: true), Is.True);
    Assert.That(encodable, Is.True);
    UharcExecutableFilter.Restore(filtered, swapOperandBytes: true);
    Assert.That(filtered, Is.EqualTo(image));
  }

  [Test, Category("Boundary")]
  public void GivenOperandsTheFilterLeavesAlone_WhenTheSwappingFilterIsApplied_ThenOnlyThoseWhoseSwapIsATargetAreSwapped() {
    // Beyond 64 KiB the high half 0xFFFE is trivial: such an operand is otherwise left as it is.
    // 0xFFFE00FF swaps to 0xFFFF00FE, a target 0xFF02 bytes back, so it is stored swapped (the
    // restore swaps it back); 0xFFFE1234 swaps to no target and stays as it is.
    var image = new byte[0x10100];
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    image[0x10010] = 0xE8;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10011), 0xFFFE_00FF);
    image[0x10030] = 0xE9;
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10031), 0xFFFE_1234);
    var filtered = image.ToArray();
    UharcExecutableFilter.Apply(filtered, out var encodable, swapOperandBytes: true);
    Assert.Multiple(() => {
      Assert.That(encodable, Is.True);
      Assert.That(filtered.AsSpan(0x10011, 4).ToArray(), Is.EqualTo(new byte[] { 0xFE, 0x00, 0xFF, 0xFF }));
      Assert.That(filtered.AsSpan(0x10031, 4).ToArray(), Is.EqualTo(image.AsSpan(0x10031, 4).ToArray()));
    });
    UharcExecutableFilter.Restore(filtered, swapOperandBytes: true);
    Assert.That(filtered, Is.EqualTo(image));
  }

  [TestCase(1023)]
  [TestCase(0)]
  [Category("Boundary")]
  public void GivenAnExecutableTooShortForTheFilter_WhenApplied_ThenItIsLeftAlone(int length) {
    var image = new byte[length];
    if (length > 1)
      image[0] = (byte)'M';
    if (length > 1)
      image[1] = (byte)'Z';
    var filtered = image.ToArray();
    Assert.Multiple(() => {
      Assert.That(UharcExecutableFilter.Apply(filtered, out var encodable), Is.False);
      Assert.That(encodable, Is.True);
      Assert.That(filtered, Is.EqualTo(image));
    });
  }

  [TestCase("lzp", "6")]
  [TestCase("alz", "6")]
  [TestCase("lz78", "6")]
  [TestCase("ppm", "6")]
  [TestCase("ppm", "4")]
  [TestCase("alz", "3")]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenExecutablesAmongOtherFiles_WhenWritten_ThenEveryFileComesBack(string method, string version) {
    var inputs = SampleInputs().Concat(ExecutableImages().Select((image, k) => ArchiveInputInfo.InMemory($"prog{k}.exe", image))).ToList();
    var reader = new UharcReader(new MemoryStream(CreateWith(new FormatCreateOptions(method) { FormatSpecific = { [UharcFormatDescriptor.VersionOption] = version } }, inputs)));
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == input.ArchiveName)), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
  }

  private static IEnumerable<TestCaseData> MediaFiles() {
    yield return new TestCaseData(Bitmap24(200, 150), 3).SetName("MediaClass_Bmp24");
    yield return new TestCaseData(Bitmap24(200, 150, bits: 8), 0).SetName("MediaClass_Bmp8IsNone");
    yield return new TestCaseData(Wave(40000, 1, 8), 5).SetName("MediaClass_Wave8Mono");
    yield return new TestCaseData(Wave(30000, 2, 8), 7).SetName("MediaClass_Wave8Stereo");
    yield return new TestCaseData(Wave(30000, 1, 16), 8).SetName("MediaClass_Wave16Mono");
    yield return new TestCaseData(Wave(20000), 9).SetName("MediaClass_Wave16Stereo");
    yield return new TestCaseData(Wave(20000, 4, 16), 0).SetName("MediaClass_Wave4ChannelsIsNone");
    yield return new TestCaseData(Wave(10)[..43], 0).SetName("MediaClass_ShortWaveIsNone");
    yield return new TestCaseData("plain text, not media"u8.ToArray(), 0).SetName("MediaClass_TextIsNone");
  }

  [TestCaseSource(nameof(MediaFiles))]
  [Category("HappyPath"), Category("Boundary")]
  public void GivenAFile_WhenItsMediaClassIsChosen_ThenItMatchesItsFormat(byte[] file, int selector)
    => Assert.That(UharcMediaClass.Of(file), Is.EqualTo(selector));

  [TestCase(0, 100, 9)]
  [TestCase(90, 130, 0)]
  [TestCase(100, 130, 0)]
  [TestCase(50, 140, 9)]
  [TestCase(50, 50, 0)]
  [Category("Boundary")]
  public void GivenABlock_WhenItsMediaClassIsChosen_ThenTheFileCoveringHalfOrMoreDecides(long start, long end, int selector) {
    var stream = new byte[140];
    Wave(20000).AsSpan(0, 100).CopyTo(stream);
    Assert.That(UharcMediaClass.ForBlock(stream, [100, 140], start, end), Is.EqualTo(selector));
  }

  [TestCase("alz", "6")]
  [TestCase("ppm", "6")]
  [TestCase("alz", "4")]
  [TestCase("ppm", "4")]
  [TestCase("ppm", "3")]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenMediaFiles_WhenWrittenWithTheirClasses_ThenEveryFileComesBack(string method, string version) {
    foreach (var data in MediaFiles().Select(c => (byte[])c.Arguments[0]!)) {
      var inputs = new[] { ArchiveInputInfo.InMemory("media.bin", data) };
      var reader = new UharcReader(new MemoryStream(CreateWith(new FormatCreateOptions(method) { FormatSpecific = { [UharcFormatDescriptor.VersionOption] = version } }, inputs)));
      Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(data));
    }
  }

  [Test, Category("RoundTrip")]
  public void GivenAFaxPage_WhenTheFilterIsAppliedAndRestored_ThenItIsUnchanged() {
    var page = FaxPage();
    var filtered = page.ToArray();
    UharcFaxFilter.Apply(filtered);
    Assert.That(filtered, Is.Not.EqualTo(page));
    UharcFaxFilter.Restore(filtered);
    Assert.That(filtered, Is.EqualTo(page));
  }

  [TestCase(1023, false)]
  [TestCase(1024, true)]
  [TestCase(20000, true)]
  [Category("RoundTrip"), Category("Boundary")]
  public void GivenAWave_WhenTheMultimediaFilterIsAppliedAndRestored_ThenItIsUnchanged(int length, bool recognised) {
    var wave = Wave(20000)[..length];
    var filtered = wave.ToArray();
    Assert.That(UharcMultimediaFilter.Apply(filtered), Is.EqualTo(recognised));
    Assert.That(filtered.AsSpan().SequenceEqual(wave), Is.EqualTo(!recognised));
    UharcMultimediaFilter.Restore(filtered);
    Assert.That(filtered, Is.EqualTo(wave));
  }

  [TestCase("store", "6")]
  [TestCase("lzp", "6")]
  [TestCase("store", "4")]
  [TestCase("lzp", "3")]
  [Category("HappyPath")]
  public void GivenAFaxPage_WhenWritten_ThenItIsStoredAsBandsAndExtractedAsAPage(string method, string version) {
    var page = FaxPage();
    var inputs = new[] { ArchiveInputInfo.InMemory("page.fax", page), ArchiveInputInfo.InMemory("note.txt", "a page\r\n"u8.ToArray()) };
    var archive = CreateWith(new FormatCreateOptions(method) { FormatSpecific = { [UharcFormatDescriptor.VersionOption] = version } }, inputs);
    if (method == "store")
      Assert.That(archive.AsSpan().IndexOf(page.AsSpan(0, 432)), Is.LessThan(0));
    var reader = new UharcReader(new MemoryStream(archive));
    Assert.Multiple(() => {
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "page.fax")), Is.EqualTo(page));
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == "note.txt")), Is.EqualTo("a page\r\n"u8.ToArray()));
    });
  }

  [TestCase("lzp", "6")]
  [TestCase("lz78", "6")]
  [TestCase("lzp", "4")]
  [TestCase("lz78", "3")]
  [TestCase("alz", "6")]
  [Category("HappyPath"), Category("RoundTrip")]
  public void GivenAWaveAmongOtherFiles_WhenWritten_ThenEveryFileComesBack(string method, string version) {
    var wave = Wave(30000);
    var inputs = SampleInputs().Append(ArchiveInputInfo.InMemory("tone.wav", wave)).ToList();
    var reader = new UharcReader(new MemoryStream(CreateWith(new FormatCreateOptions(method) { FormatSpecific = { [UharcFormatDescriptor.VersionOption] = version } }, inputs)));
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      Assert.That(reader.Extract(reader.Entries.Single(e => e.FileName == input.ArchiveName)), Is.EqualTo(input.InMemoryContent), input.ArchiveName);
  }
}
