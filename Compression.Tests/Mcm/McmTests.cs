using System.Reflection;
using Compression.Core.Entropy.ContextMixing.Mcm;
using Compression.Registry;
using Compression.Tests.CodecProof;
using FileFormat.Mcm;

namespace Compression.Tests.Mcm;

/// <summary>The inputs of the archives in <c>Mcm/Vectors</c> (see its README).</summary>
internal static class McmVectors {
  public static byte[] Archive(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("McmVectors." + name)
      ?? throw new FileNotFoundException(name);
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
  }

  public static byte[] Text => CodecProofData.Text(20000);
  public static byte[] Mixed => CodecProofData.Mixed(20000);
  public static byte[] Random => CodecProofData.Random(30000);

  /// <summary>Stereo 16-bit PCM: two integer triangle waves.</summary>
  public static byte[] Wave(int frames = 12000) {
    var data = new byte[frames * 4];
    for (var i = 0; i < frames; ++i) {
      var left = (short)((i * 97 % 4000) * 6 - 12000);
      var right = (short)(9000 - Math.Abs(i * 61 % 3600 - 1800) * 10);
      data[i * 4] = (byte)left;
      data[i * 4 + 1] = (byte)(left >> 8);
      data[i * 4 + 2] = (byte)right;
      data[i * 4 + 3] = (byte)(right >> 8);
    }
    using var s = new MemoryStream();
    using var w = new BinaryWriter(s);
    w.Write("RIFF"u8);
    w.Write(36 + data.Length);
    w.Write("WAVEfmt "u8);
    w.Write(16);
    w.Write((short)1);
    w.Write((short)2);
    w.Write(44100);
    w.Write(44100 * 4);
    w.Write((short)4);
    w.Write((short)16);
    w.Write("data"u8);
    w.Write(data.Length);
    w.Write(data);
    w.Flush();
    return s.ToArray();
  }

  /// <summary>The directory <c>tree</c> as mcm was handed it.</summary>
  public static List<McmInput> Tree() => [
    new("tree", [], IsDirectory: true),
    new("tree/sub", [], IsDirectory: true),
    new("tree/a.txt", CodecProofData.Text(3000)),
    new("tree/b.bin", CodecProofData.Mixed(5000)),
    new("tree/empty.dat", []),
    new("tree/sub/c.cpp", CodecProofData.Text(2000)),
  ];
}

[TestFixture]
[Category("ThirdPartyProof")]
public class McmTests {
  private static Dictionary<string, byte[]> Extract(byte[] archive) {
    var index = McmArchive.ReadIndex(archive);
    var contents = McmArchive.Extract(archive, index);
    return index.Files.Select((f, i) => (f, i)).Where(p => !p.f.IsDirectory).ToDictionary(p => p.f.Name, p => contents[p.i]);
  }

  [TestCase("text-t0.mcm")]
  [TestCase("text-m2.mcm")]
  [TestCase("text-h1.mcm")]
  [TestCase("text-s0.mcm")]
  [TestCase("text-store.mcm")]
  public void Given_AnMcmTextArchive_When_Extracted_Then_TheTextComesBack(string name) {
    var files = Extract(McmVectors.Archive(name));
    Assert.That(files["text.txt"], Is.EqualTo(McmVectors.Text));
  }

  [Test]
  public void Given_AnMcmBinaryArchive_When_Extracted_Then_TheBytesComeBack()
    => Assert.That(Extract(McmVectors.Archive("mixed-f0.mcm"))["mixed.bin"], Is.EqualTo(McmVectors.Mixed));

  [Test]
  public void Given_AnArchiveThatMcmItselfCannotExtract_When_WeExtractIt_Then_TheRandomBytesComeBack()
    => Assert.That(Extract(McmVectors.Archive("random-t0.mcm"))["random.bin"], Is.EqualTo(McmVectors.Random));

  [Test]
  public void Given_AnMcmWav16Archive_When_WeExtractIt_Then_TheWaveComesBack()
    => Assert.That(Extract(McmVectors.Archive("tone-f0.mcm"))["tone.wav"], Is.EqualTo(McmVectors.Wave()));

  [Test]
  public void Given_AnMcmDirectoryArchive_When_Listed_Then_DirectoriesComeFirstAndFilesInMcmOrder() {
    var index = McmArchive.ReadIndex(McmVectors.Archive("tree-m0.mcm"));
    Assert.That(index.Files.Select(f => f.Name), Is.EqualTo(new[] {
      "tree", "tree/sub", "tree/b.bin", "tree/sub/c.cpp", "tree/empty.dat", "tree/a.txt",
    }));
    Assert.That(index.Files.Select(f => f.Attributes), Is.EqualTo(new byte[] { 5, 5, 14, 14, 14, 14 }));
    var files = Extract(McmVectors.Archive("tree-m0.mcm"));
    foreach (var input in McmVectors.Tree().Where(i => !i.IsDirectory))
      Assert.That(files[input.Name], Is.EqualTo(input.Data), input.Name);
  }

  [TestCase("text-t0.mcm", (int)McmCompressor.Turbo, 0)]
  [TestCase("text-m2.mcm", (int)McmCompressor.Mid, 2)]
  [TestCase("text-h1.mcm", (int)McmCompressor.High, 1)]
  [TestCase("text-s0.mcm", (int)McmCompressor.Simple, 0)]
  [TestCase("text-store.mcm", (int)McmCompressor.Store, 6)]
  public void Given_TheSameText_When_WeWriteAnArchive_Then_ItIsMcmsByteForByte(string name, int compressor, int memory) {
    var ours = McmArchiveWriter.Write([new("text.txt", McmVectors.Text)], (McmCompressor)compressor, memory);
    Assert.That(ours, Is.EqualTo(McmVectors.Archive(name)));
  }

  [Test]
  public void Given_TheSameBinary_When_WeWriteAnArchive_Then_ItIsMcmsByteForByte() {
    var ours = McmArchiveWriter.Write([new("mixed.bin", McmVectors.Mixed)], McmCompressor.Fast, 0);
    Assert.That(ours, Is.EqualTo(McmVectors.Archive("mixed-f0.mcm")));
  }

  [Test]
  public void Given_TheSameDirectory_When_WeWriteAnArchive_Then_ItIsMcmsByteForByte() {
    var inputs = McmVectors.Tree();
    inputs.Reverse();
    var ours = McmArchiveWriter.Write(inputs, McmCompressor.Mid, 0);
    Assert.That(ours, Is.EqualTo(McmVectors.Archive("tree-m0.mcm")));
  }

  [Test]
  public void Given_DataThatTakesTheFastPath_When_WeWriteIt_Then_TheBlockAvoidsTheFastPathAndStillRoundTrips() {
    var ours = McmArchiveWriter.Write([new("random.bin", McmVectors.Random)], McmCompressor.Turbo, 0);
    var block = McmArchive.ReadIndex(ours).Blocks.Single();
    Assert.That(block.Profile == McmBlockProfile.Text || block.Compressor == McmCompressor.Store, Is.True);
    Assert.That(Extract(ours)["random.bin"], Is.EqualTo(McmVectors.Random));
  }

  [Test]
  public void Given_AWave_When_WeWriteIt_Then_NoWav16BlockIsWrittenAndItRoundTrips() {
    var ours = McmArchiveWriter.Write([new("tone.wav", McmVectors.Wave())], McmCompressor.Fast, 0);
    Assert.That(McmArchive.ReadIndex(ours).Blocks.Select(b => b.Compressor), Has.None.EqualTo(McmCompressor.Wave16));
    Assert.That(Extract(ours)["tone.wav"], Is.EqualTo(McmVectors.Wave()));
  }

  [Test]
  public void Given_McmsArchiveIndex_When_TheBuildingBlockDecodesAndReencodesIt_Then_BothDirectionsMatch() {
    var archive = McmVectors.Archive("text-t0.mcm");
    var index = McmArchive.ReadIndex(archive);
    var framed = archive[14..(index.PayloadStart)];
    var block = new McmBuildingBlock();
    var metadata = block.Decompress(framed);
    Assert.That(metadata[0], Is.EqualTo(1), "one solid block");
    Assert.That(block.Compress(metadata), Is.EqualTo(framed));
  }

  [Test]
  public void Given_TheDescriptor_When_ItCreatesListsAndExtracts_Then_EverythingRoundTrips() {
    var descriptor = new McmFormatDescriptor();
    var text = CodecProofData.Text(5000);
    var mixed = CodecProofData.Mixed(4000);
    using var archive = new MemoryStream();
    descriptor.Create(archive, [
      ArchiveInputInfo.InMemory("docs/readme.txt", text),
      ArchiveInputInfo.InMemory("bin/data.bin", mixed),
    ], new FormatCreateOptions("fast") { Level = 0 });
    var listing = descriptor.List(archive, null);
    Assert.That(listing.Select(e => (e.Name, e.OriginalSize)), Is.EquivalentTo(new[] { ("docs/readme.txt", 5000L), ("bin/data.bin", 4000L) }));
    var dir = Path.Combine(Path.GetTempPath(), "cwb-mcm-" + Guid.NewGuid().ToString("N"));
    try {
      descriptor.Extract(archive, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "docs", "readme.txt")), Is.EqualTo(text));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "bin", "data.bin")), Is.EqualTo(mixed));
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ATruncatedArchive_When_Extracted_Then_ItIsRejected() {
    var archive = McmVectors.Archive("text-t0.mcm");
    var cut = archive[..(archive.Length - 40)];
    Assert.That(() => McmArchive.Extract(cut, McmArchive.ReadIndex(cut)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void Given_AnotherVersion_When_Read_Then_ItIsRefused() {
    var archive = McmVectors.Archive("text-t0.mcm");
    archive[13] = 83;
    Assert.That(() => McmArchive.ReadIndex(archive), Throws.InstanceOf<NotSupportedException>());
  }

  [Test]
  public void Given_Garbage_When_Listed_Then_ListingFallsBackInsteadOfThrowing() {
    var garbage = "MCMARCHIVE\0\0\0T"u8.ToArray().Concat(Enumerable.Repeat((byte)0xFF, 64)).ToArray();
    var listing = new McmFormatDescriptor().List(new MemoryStream(garbage), null);
    Assert.That(listing.Select(e => e.Name), Is.EqualTo(new[] { "FULL.mcm", "metadata.ini" }));
  }

  [TestCase((byte)6, TestName = "Given_AMaxLevelBlock_When_Extracted_Then_ItIsRefused")]
  [TestCase((byte)9, TestName = "Given_AnUnknownCompressor_When_Extracted_Then_ItIsRejected")]
  public void Given_ABlockWeCannotProve_When_Extracted_Then_ItIsRefused(byte compressor) {
    var archive = PatchBlock(McmVectors.Archive("text-t0.mcm"), metadata => metadata[2] = compressor);
    Assert.That(() => McmArchive.Extract(archive, McmArchive.ReadIndex(archive)),
      compressor == 6 ? Throws.InstanceOf<NotSupportedException>() : Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void Given_MemoryLevelTen_When_Extracted_Then_ItIsRefused() {
    var archive = PatchBlock(McmVectors.Archive("text-t0.mcm"), metadata => metadata[1] = 10);
    Assert.That(() => McmArchive.Extract(archive, McmArchive.ReadIndex(archive)), Throws.InstanceOf<NotSupportedException>());
  }

  [Test]
  public void Given_MaxLevelOrMemoryTen_When_Writing_Then_ItIsRefused() {
    Assert.That(() => McmArchiveWriter.Write([new("a", [1])], McmCompressor.Max, 0), Throws.InstanceOf<NotSupportedException>());
    Assert.That(() => McmArchiveWriter.Write([new("a", [1])], McmCompressor.Mid, 10), Throws.InstanceOf<NotSupportedException>());
  }

  /// <summary>Re-encodes an archive's index after <paramref name="patch"/> changed its decoded bytes.</summary>
  private static byte[] PatchBlock(byte[] archive, Action<byte[]> patch) {
    var index = McmArchive.ReadIndex(archive);
    var block = new McmBuildingBlock();
    var metadata = block.Decompress(archive[14..index.PayloadStart]);
    patch(metadata);
    return [.. archive[..14], .. block.Compress(metadata), .. archive[index.PayloadStart..]];
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(7)]
  [TestCase(40000)]
  public void Given_X86Code_When_FilteredAndUnfiltered_Then_TheBytesComeBack(int size) {
    var data = new byte[size];
    var state = 0x13579BDFu;
    for (var i = 0; i < size; ++i) {
      state = state * 1103515245 + 12345;
      data[i] = (i % 9) switch {
        0 => 0xE8,
        4 => (byte)((state >> 24) & 1) == 0 ? (byte)0x00 : (byte)0xFF,
        5 => 0x0F,
        6 => (byte)(0x80 | (state >> 28)),
        _ => (byte)(state >> 16),
      };
    }
    var encoded = McmX86Filter.Encode(data);
    Assert.That(McmX86Filter.Decode(encoded, size), Is.EqualTo(data));
  }

  [Test]
  public void Given_TextWithCapitalsAndEscapes_When_DictionaryFiltered_Then_TheBytesComeBack() {
    var text = "The THE the Quick quick QUICK \u0003\u0004\u0006 café brown Brown BROWN zebra"u8.ToArray();
    var builder = new McmDictionaryBuilder();
    var corpus = Enumerable.Repeat(text, 20).SelectMany(t => t).Append((byte)' ').ToArray();
    foreach (var b in corpus)
      builder.Add(b);
    var dictionary = builder.Build();
    var encoded = McmDictionaryFilter.Encode(corpus, dictionary);
    Assert.That(encoded.Length, Is.LessThan(corpus.Length));
    Assert.That(McmDictionaryFilter.Decode(encoded, corpus.Length), Is.EqualTo(corpus));
  }

  [Test]
  public void Given_TheBuildingBlock_When_RoundTripping_Then_EmptyTinyTextAndRandomComeBack() {
    var block = new McmBuildingBlock();
    foreach (var data in new[] { Array.Empty<byte>(), new byte[] { 0x5A }, CodecProofData.Text(3000), CodecProofData.Random(2000) })
      Assert.That(block.Decompress(block.Compress(data)), Is.EqualTo(data));
  }

  [Test]
  public void Given_ATruncatedBuildingBlockStream_When_Decompressed_Then_ItIsRejected() {
    var block = new McmBuildingBlock();
    var packed = block.Compress(CodecProofData.Text(3000));
    Assert.That(() => block.Decompress(packed[..(packed.Length / 2)]), Throws.InstanceOf<InvalidDataException>());
  }

  [TestCase(3, McmModelProfile.Binary, 0, true)]
  [TestCase(4, McmModelProfile.Text, 1, false)]
  [TestCase(10, McmModelProfile.Binary, 0, false)]
  [TestCase(6, McmModelProfile.Simple, 0, true)]
  public void Given_EachCoderConfiguration_When_RoundTripping_Then_TheBytesComeBack(int inputs, McmModelProfile profile, int memory, bool lzp) {
    var data = CodecProofData.Mixed(30000);
    var coded = new McmContextMixer(inputs, profile, memory, lzp).Encode(data);
    var decoded = new McmContextMixer(inputs, profile, memory, lzp).Decode(coded, 0, coded.Length, data.Length, out var consumed);
    Assert.That(decoded, Is.EqualTo(data));
    Assert.That(consumed, Is.EqualTo(coded.Length));
  }

  [Test]
  public void Given_AnOddWav16Block_When_Decoded_Then_ItIsRefused()
    => Assert.That(() => McmWave16Coder.Decode(new byte[16], 0, 16, 3, out _), Throws.InstanceOf<InvalidDataException>());

  [Test]
  public void Given_Wave16_When_RoundTripping_Then_TheSamplesComeBack() {
    var wave = McmVectors.Wave(9000)[44..];
    var coded = McmWave16Coder.Encode(wave);
    Assert.That(McmWave16Coder.Decode(coded, 0, coded.Length, wave.Length, out _), Is.EqualTo(wave));
  }
}
