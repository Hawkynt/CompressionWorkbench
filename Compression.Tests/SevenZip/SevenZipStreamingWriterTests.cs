using Compression.Core.Dictionary.Lzma;
using Compression.Lib;
using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

/// <summary>
/// The streaming 7z writer: files added as streams are read only while their solid block is
/// encoded, through the coders, with bounded memory — and the result is the archive the
/// in-memory path writes, readable here and by 7-Zip.
/// </summary>
[TestFixture]
public sealed class SevenZipStreamingWriterTests {
  private string _root = null!;

  [SetUp]
  public void Setup() {
    this._root = Path.Combine(Path.GetTempPath(), $"cwb_7zstream_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._root);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(70_000)]
  [TestCase(5_000_000)]
  [Category("HappyPath")]
  public void Given_AnyInput_When_Lzma2EncodesItAsAStream_Then_TheBytesEqualTheInMemoryEncoding(int size) {
    var data = new byte[size];
    var random = new Random(size);
    for (var i = 0; i < size; ++i)
      data[i] = (byte)(random.Next(4) == 0 ? random.Next() : i / 1000);

    var encoder = new Lzma2Encoder(1 << 16);
    using var whole = new MemoryStream();
    encoder.Encode(whole, data);
    using var streamed = new MemoryStream();
    encoder.Encode(streamed, new MemoryStream(data), data.Length);

    Assert.That(streamed.ToArray(), Is.EqualTo(whole.ToArray()));
  }

  public static IEnumerable<TestCaseData> Profiles() {
    foreach (var codec in new[] { SevenZipCodec.Lzma2, SevenZipCodec.Lzma, SevenZipCodec.PPMd, SevenZipCodec.Deflate, SevenZipCodec.BZip2, SevenZipCodec.Zstd, SevenZipCodec.Brotli, SevenZipCodec.Lz4, SevenZipCodec.Copy })
      foreach (var filter in new[] { SevenZipFilter.None, SevenZipFilter.BcjX86, SevenZipFilter.Bcj2, SevenZipFilter.Delta, SevenZipFilter.Swap4 })
        foreach (var encrypt in new[] { false, true })
          yield return new TestCaseData(codec, filter, encrypt).SetName($"Given_StreamedEntries_{codec}_{filter}_{(encrypt ? "aes" : "plain")}_When_ReadBack_Then_TheyMatch");
  }

  private static (string Name, byte[] Data)[] Files() => [
    ("a/x86.bin", SevenZipOracle.X86Like(300_001, 1)),
    ("b.txt", SevenZipOracle.Text(150_000, 2)),
    ("noise.bin", SevenZipOracle.Noise(90_003, 3)),
  ];

  private static byte[] WriteStreamed(SevenZipCodec codec, SevenZipFilter filter, bool encrypt) {
    using var archive = new MemoryStream();
    using (var writer = new SevenZipWriter(archive, codec, leaveOpen: true, filter: filter, deltaDistance: 3, password: encrypt ? "pw" : null)) {
      writer.AddDirectory("a");
      foreach (var (name, data) in Files())
        writer.AddEntry(new SevenZipEntry { Name = name }, () => new MemoryStream(data, writable: false), data.Length);
      writer.Finish();
    }
    return archive.ToArray();
  }

  [TestCaseSource(nameof(Profiles))]
  [Category("RoundTrip")]
  public void Given_StreamedEntries_When_ReadBack_Then_TheyMatch(SevenZipCodec codec, SevenZipFilter filter, bool encrypt) {
    var bytes = WriteStreamed(codec, filter, encrypt);

    using var reader = new SevenZipReader(new MemoryStream(bytes), password: encrypt ? "pw" : null);
    foreach (var (name, data) in Files()) {
      var index = reader.Entries.ToList().FindIndex(e => e.Name == name);
      Assert.That(reader.Entries[index].Crc, Is.EqualTo(Compression.Core.Checksums.Crc32.Compute(data)));
      Assert.That(reader.Extract(index), Is.EqualTo(data), name);
    }
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AStreamShorterThanDeclared_When_Written_Then_ItIsRefused() {
    using var archive = new MemoryStream();
    var writer = new SevenZipWriter(archive, SevenZipCodec.Lzma2, leaveOpen: true);
    writer.AddEntry(new SevenZipEntry { Name = "short.bin" }, () => new MemoryStream(new byte[10]), 20);
    Assert.That(() => writer.Finish(), Throws.InstanceOf<IOException>());
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AStreamedBlockWithAnInMemoryOnlyCodec_When_Written_Then_ItIsBufferedAndStillCorrect() {
    // LZMA (not LZMA2) has no streaming encoder: a small streamed entry is read into memory.
    using var archive = new MemoryStream();
    var data = SevenZipOracle.Text(50_000, 9);
    using (var writer = new SevenZipWriter(archive, SevenZipCodec.Lzma, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "t.txt" }, () => new MemoryStream(data), data.Length);
      writer.Finish();
    }
    archive.Position = 0;
    using var reader = new SevenZipReader(archive);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [TestCaseSource(nameof(Profiles))]
  [Category("EndToEnd")]
  public void Given_StreamedEntries_When_7ZipTestsAndExtracts_Then_TheyMatch(SevenZipCodec codec, SevenZipFilter filter, bool encrypt) {
    var tool = SevenZipOracle.RequireFor(codec);
    var path = Path.Combine(this._root, "s.7z");
    File.WriteAllBytes(path, WriteStreamed(codec, filter, encrypt));
    var pw = encrypt ? "-ppw" : "-p-";
    SevenZipOracle.RunToolOk(tool, this._root, "t", pw, path);
    var output = Path.Combine(this._root, "out");
    SevenZipOracle.RunToolOk(tool, this._root, "x", "-y", pw, $"-o{output}", path);
    foreach (var (name, data) in Files())
      Assert.That(File.ReadAllBytes(Path.Combine(output, name)), Is.EqualTo(data), name);
  }

  [Test]
  [Explicit("Writes a 4.5 GiB file, archives it and has 7-Zip test and extract it; run on demand.")]
  [Category("Performance")]
  [TestCase("lzma2")]
  [TestCase("lzma")]
  [TestCase("ppmd")]
  [TestCase("lzma2", "bcj2")]
  [TestCase("zstd")]
  [TestCase("brotli")]
  [TestCase("lz4")]
  public void Given_AFileLargerThan4GiB_When_ArchivedThroughTheLibrary_Then_7ZipTestsAndExtractsItWithBoundedMemory(string method, string? filter = null) {
    var tool = SevenZipOracle.RequireFor(MethodSpec.Parse(method).Resolve7z());
    var source = Path.Combine(this._root, "big.bin");
    const long size = (9L << 29) + 4321;
    var chunk = new byte[1 << 20];
    for (var i = 0; i < chunk.Length; i += 512)
      chunk[i] = (byte)(i >> 9);
    if (filter == "bcj2")
      // A near CALL every 61 bytes, so BCJ2's call stream grows to hundreds of megabytes.
      for (var i = 7; i + 5 < chunk.Length; i += 61) {
        chunk[i] = 0xE8;
        BitConverter.TryWriteBytes(chunk.AsSpan(i + 1), i % 40000 - 20000);
      }
    using (var file = File.Create(source))
      for (long written = 0; written < size; written += chunk.Length) {
        chunk[0] = (byte)(written >> 20); // no two megabytes alike
        file.Write(chunk, 0, (int)Math.Min(chunk.Length, size - written));
      }

    var archive = Path.Combine(this._root, "big.7z");
    var before = GC.GetTotalMemory(forceFullCollection: true);
    var peak = 0L;
    using var sampler = new Timer(_ => peak = Math.Max(peak, GC.GetTotalMemory(false)), null, 0, 200);
    if (filter == "bcj2") {
      // The library picks no BCJ2 for a plain file; the writer is told to, one solid block.
      using var file = File.Create(archive);
      using var writer = new SevenZipWriter(file, SevenZipCodec.Lzma2, filter: SevenZipFilter.Bcj2);
      writer.AddEntry(new SevenZipEntry { Name = "big.bin" }, () => File.OpenRead(source), size);
      writer.Finish();
    } else
      ArchiveOperations.Create(archive, ArchiveInput.Resolve([source]), new CompressionOptions { Method = MethodSpec.Parse(method) });
    sampler.Dispose();
    TestContext.Out.WriteLine($"{method}{(filter == null ? "" : "+" + filter)}: peak managed memory {(peak - before) >> 20} MiB above the start, archive {new FileInfo(archive).Length} bytes");
    Assert.That(peak - before, Is.LessThan(768L << 20), "the writer must not hold the file in memory");

    SevenZipOracle.RunToolOk(tool, this._root, "t", archive);
    var output = Path.Combine(this._root, "out");
    SevenZipOracle.RunToolOk(tool, this._root, "x", "-y", $"-o{output}", archive);
    var extracted = Path.Combine(output, "big.bin");
    Assert.That(new FileInfo(extracted).Length, Is.EqualTo(size));
    using var a = File.OpenRead(source);
    using var b = File.OpenRead(extracted);
    Assert.That(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)), Is.EqualTo(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a))));
  }
}
