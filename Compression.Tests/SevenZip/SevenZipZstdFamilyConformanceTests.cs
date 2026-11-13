using System.Buffers.Binary;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// The 7-Zip-zstd coders inside 7z — Zstandard, Brotli, LZ4, LZ5 and Lizard — both ways against 7-Zip-zstd
/// (mcmilk/7-Zip-zstd, <see cref="SevenZipOracle.ZstdToolPath"/>): it tests and extracts what we
/// write byte for byte, and we extract what it writes, solid and non-solid, plain and with AES
/// (data and headers).
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SevenZipZstdFamilyConformanceTests {
  private const string Password = "S3cret!";
  private string _root = null!;
  private SortedDictionary<string, byte[]?> _tree = null!;

  [OneTimeSetUp]
  public void BuildTree() {
    this._root = Path.Combine(Path.GetTempPath(), $"cwb_7zzs_{Guid.NewGuid():N}");
    var source = Path.Combine(this._root, "src");
    this._tree = SevenZipOracle.WriteStandardTree(source);
    // Large enough for several Brotli and LZ4 chunks and two Zstandard frames.
    var big = SevenZipOracle.Text(SevenZipZstdFamilyCoders.ZstdFrameSize + 1_000_003, 11);
    this._tree["big.txt"] = big;
    File.WriteAllBytes(Path.Combine(source, "big.txt"), big);
  }

  [OneTimeTearDown]
  public void Cleanup() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  private static readonly SevenZipCodec[] Codecs = [SevenZipCodec.Zstd, SevenZipCodec.Brotli, SevenZipCodec.Lz4, SevenZipCodec.Lz5, SevenZipCodec.Lizard];

  private static string MethodName(SevenZipCodec codec) => codec switch {
    SevenZipCodec.Zstd => "zstd",
    SevenZipCodec.Brotli => "brotli",
    SevenZipCodec.Lz4 => "lz4",
    SevenZipCodec.Lz5 => "lz5",
    _ => "lizard",
  };

  public static IEnumerable<TestCaseData> Profiles() {
    foreach (var codec in Codecs)
      foreach (var solid in new[] { true, false })
        foreach (var crypto in new[] { "plain", "aes", "aes-headers" })
          yield return new TestCaseData(codec, solid, crypto);
  }

  [TestCaseSource(nameof(Profiles))]
  [Category("EndToEnd")]
  public void Given_OurArchive_When_7ZipZstdTestsAndExtracts_Then_TheTreeMatches(SevenZipCodec codec, bool solid, string crypto) {
    SevenZipOracle.RequireZstd();
    var name = $"w-{codec}-{(solid ? "solid" : "files")}-{crypto}";
    var archive = Path.Combine(this._root, name + ".7z");
    var password = crypto == "plain" ? null : Password;
    using (var file = File.Create(archive))
    using (var writer = new SevenZipWriter(file, codec, password: password, encryptHeaders: crypto == "aes-headers")) {
      foreach (var (path, data) in this._tree)
        if (data == null)
          writer.AddDirectory(path);
        else
          writer.AddEntry(new SevenZipEntry { Name = path }, data);
      writer.Finish(1, solid ? 0 : 1);
    }

    var pw = password == null ? "-p-" : $"-p{password}";
    SevenZipOracle.RunZstdOk(this._root, "t", pw, archive);
    var output = Path.Combine(this._root, name + ".out");
    SevenZipOracle.RunZstdOk(this._root, "x", "-y", pw, $"-o{output}", archive);
    SevenZipOracle.AssertSameTree(this._tree, SevenZipOracle.ReadTree(output), name);
  }

  public static IEnumerable<TestCaseData> TheirProfiles() {
    foreach (var codec in Codecs)
      foreach (var solid in new[] { true, false })
        foreach (var crypto in new[] { "plain", "aes", "aes-headers" })
          foreach (var threads in new[] { 1, 4 })
            yield return new TestCaseData(codec, solid, crypto, threads);
  }

  [TestCaseSource(nameof(TheirProfiles))]
  [Category("EndToEnd")]
  public void Given_7ZipZstdArchive_When_WeExtract_Then_TheTreeMatches(SevenZipCodec codec, bool solid, string crypto, int threads) {
    SevenZipOracle.RequireZstd();
    var name = $"r-{codec}-{(solid ? "solid" : "files")}-{crypto}-mt{threads}";
    var archive = Path.Combine(this._root, name + ".7z");
    var args = new List<string> { "a", "-t7z", $"-m0={MethodName(codec)}", $"-ms={(solid ? "on" : "off")}", $"-mmt={threads}" };
    if (crypto != "plain")
      args.Add($"-p{Password}");
    if (crypto == "aes-headers")
      args.Add("-mhe=on");
    args.Add(archive);
    args.Add(Path.Combine(this._root, "src", "*"));
    SevenZipOracle.RunZstdOk(this._root, [.. args]);

    using var reader = new SevenZipReader(File.OpenRead(archive), password: crypto == "plain" ? null : Password);
    var extracted = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
    for (var i = 0; i < reader.Entries.Count; ++i) {
      var entry = reader.Entries[i];
      extracted[entry.Name.Replace('\\', '/')] = entry.IsDirectory ? null : reader.Extract(i);
    }
    SevenZipOracle.AssertSameTree(this._tree, extracted, name);
    Assert.That(reader.Entries.Where(e => !e.IsDirectory && e.Size > 0).Select(e => e.Method).Distinct(),
      Is.EqualTo(new[] { codec switch { SevenZipCodec.Zstd => "Zstd", SevenZipCodec.Brotli => "Brotli", SevenZipCodec.Lz4 => "LZ4", SevenZipCodec.Lz5 => "LZ5", _ => "Lizard" } }));
  }

  [Category("EndToEnd")]
  [TestCase(SevenZipCodec.Zstd, 1)]
  [TestCase(SevenZipCodec.Zstd, 3)]
  [TestCase(SevenZipCodec.Zstd, 9)]
  [TestCase(SevenZipCodec.Brotli, 1)]
  [TestCase(SevenZipCodec.Brotli, 9)]
  [TestCase(SevenZipCodec.Lz4, 1)]
  [TestCase(SevenZipCodec.Lz4, 9)]
  [TestCase(SevenZipCodec.Lz5, 1)]
  [TestCase(SevenZipCodec.Lz5, 15)]
  [TestCase(SevenZipCodec.Lizard, 10)]
  [TestCase(SevenZipCodec.Lizard, 29)]
  [TestCase(SevenZipCodec.Lizard, 49)]
  public void Given_TheSameLevel_When_BothWrite_Then_7ZipZstdDescribesTheCodersAlike(SevenZipCodec codec, int level) {
    SevenZipOracle.RequireZstd();
    var data = this._tree["text.txt"]!;
    var ours = Path.Combine(this._root, $"lvl-ours-{codec}-{level}.7z");
    using (var file = File.Create(ours))
    using (var writer = new SevenZipWriter(file, codec, level: level)) {
      writer.AddEntry(new SevenZipEntry { Name = "text.txt" }, data);
      writer.Finish();
    }
    var theirs = Path.Combine(this._root, $"lvl-theirs-{codec}-{level}.7z");
    SevenZipOracle.RunZstdOk(this._root, "a", "-t7z", $"-m0={MethodName(codec)}", $"-mx={level}", "-mmt=1", theirs, Path.Combine(this._root, "src", "text.txt"));

    static string Method(string listing) => listing.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("Method = ", StringComparison.Ordinal));
    Assert.That(Method(SevenZipOracle.RunZstdOk(this._root, "l", "-slt", ours)), Is.EqualTo(Method(SevenZipOracle.RunZstdOk(this._root, "l", "-slt", theirs))));
  }

  // ── Without the oracle ─────────────────────────────────────────────

  [TestCase(SevenZipCodec.Zstd, 3, new byte[] { 1, 5, 3, 0, 0 })]
  [TestCase(SevenZipCodec.Zstd, 0, new byte[] { 1, 5, 1, 0, 0 })]
  [TestCase(SevenZipCodec.Zstd, 22, new byte[] { 1, 5, 22, 0, 0 })]
  [TestCase(SevenZipCodec.Zstd, 23, new byte[] { 1, 5, 22, 0, 0 })]
  [TestCase(SevenZipCodec.Brotli, 3, new byte[] { 1, 0, 3 })]
  [TestCase(SevenZipCodec.Brotli, 0, new byte[] { 1, 0, 0 })]
  [TestCase(SevenZipCodec.Brotli, -1, new byte[] { 1, 0, 0 })]
  [TestCase(SevenZipCodec.Brotli, 12, new byte[] { 1, 0, 11 })]
  [TestCase(SevenZipCodec.Lz4, 3, new byte[] { 1, 10, 3, 0, 0 })]
  [TestCase(SevenZipCodec.Lz4, 0, new byte[] { 1, 10, 1, 0, 0 })]
  [TestCase(SevenZipCodec.Lz4, 13, new byte[] { 1, 10, 12, 0, 0 })]
  [TestCase(SevenZipCodec.Lz5, 3, new byte[] { 1, 5, 3, 0, 0 })]
  [TestCase(SevenZipCodec.Lz5, 0, new byte[] { 1, 5, 1, 0, 0 })]
  [TestCase(SevenZipCodec.Lz5, 16, new byte[] { 1, 5, 15, 0, 0 })]
  [TestCase(SevenZipCodec.Lizard, 3, new byte[] { 2, 1, 10 })]
  [TestCase(SevenZipCodec.Lizard, 49, new byte[] { 2, 1, 49 })]
  [TestCase(SevenZipCodec.Lizard, 50, new byte[] { 2, 1, 49 })]
  [Category("Unit")]
  public void Given_ALevel_When_PropertiesAreWritten_Then_TheyAreThe7ZipZstdBytes(SevenZipCodec codec, int level, byte[] expected)
    => Assert.That(SevenZipZstdFamilyCoders.Properties(codec, level), Is.EqualTo(expected));

  /// <summary>The chunk sizes and size hints 7-Zip-zstd 26.02 wrote for these input sizes.</summary>
  [TestCase(1, new[] { 1 })]
  [TestCase(65_535, new[] { 1 })]
  [TestCase(65_536, new[] { 2 })]
  [TestCase(131_073, new[] { 3 })]
  [TestCase(3 << 20, new[] { 48 })]
  [TestCase((3 << 20) + 1, new[] { 48, 1 })]
  [Category("Boundary")]
  public void Given_AnInputSize_When_BrotliIsEncoded_Then_TheChunkHeadersAre7ZipZstds(int size, int[] hints) {
    var data = SevenZipOracle.Text(size, size);
    using var packed = new MemoryStream();
    SevenZipZstdFamilyCoders.Encode(SevenZipCodec.Brotli, new MemoryStream(data), packed, 1);
    var bytes = packed.ToArray();
    var seen = new List<int>();
    for (var pos = 0; pos < bytes.Length;) {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos)), Is.EqualTo(0x184D2A50u));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4)), Is.EqualTo(8u));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 12)), Is.EqualTo(0x5242));
      seen.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 14)));
      pos += 16 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 8));
    }
    Assert.That(seen, Is.EqualTo(hints));
    Assert.That(SevenZipZstdFamilyCoders.DecodeBrotli(bytes), Is.EqualTo(data));
  }

  [Test]
  [Category("RoundTrip")]
  public void Given_AnySize_When_EncodedAndDecoded_Then_TheBytesComeBack(
      [ValueSource(nameof(Codecs))] SevenZipCodec codec,
      [Values(0, 1, 65_535, 65_536, 65_537, (4 << 20) - 1, 4 << 20, (4 << 20) + 1)] int size) {
    var data = SevenZipOracle.Text(size, 7);
    using var packed = new MemoryStream();
    SevenZipZstdFamilyCoders.Encode(codec, new MemoryStream(data), packed, 1);
    if (size == 0)
      Assert.That(packed.Length, Is.Zero, "an empty input writes nothing, as 7-Zip-zstd does");
    var decoded = codec switch {
      SevenZipCodec.Zstd => SevenZipZstdFamilyCoders.DecodeZstd(packed.ToArray()),
      SevenZipCodec.Brotli => SevenZipZstdFamilyCoders.DecodeBrotli(packed.ToArray()),
      SevenZipCodec.Lz4 => SevenZipZstdFamilyCoders.DecodeLz4(packed.ToArray()),
      SevenZipCodec.Lz5 => SevenZipZstdFamilyCoders.DecodeLz5(packed.ToArray()),
      _ => SevenZipZstdFamilyCoders.DecodeLizard(packed.ToArray()),
    };
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnLz4FrameWithAWrongContentChecksum_When_Decoded_Then_InvalidData() {
    using var packed = new MemoryStream();
    SevenZipZstdFamilyCoders.Encode(SevenZipCodec.Lz4, new MemoryStream(SevenZipOracle.Text(1000, 1)), packed, 1);
    var bytes = packed.ToArray();
    bytes[^1] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => SevenZipZstdFamilyCoders.DecodeLz4(bytes));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnLz4FrameWithoutItsEndMark_When_Decoded_Then_InvalidData() {
    using var packed = new MemoryStream();
    SevenZipZstdFamilyCoders.Encode(SevenZipCodec.Lz4, new MemoryStream(SevenZipOracle.Text(1000, 1)), packed, 1);
    var bytes = packed.ToArray()[..^8];
    Assert.Throws<InvalidDataException>(() => SevenZipZstdFamilyCoders.DecodeLz4(bytes));
  }
}
