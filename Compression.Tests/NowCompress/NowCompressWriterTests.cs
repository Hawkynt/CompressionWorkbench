using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.NowCompress;

namespace Compression.Tests.NowCompress;

/// <summary>
/// The Now Compress writer. <c>ours.now</c> is this writer's output; native unar 1.10.7 extracted
/// every fork of it to the bytes recorded in <c>unar-ours.txt</c> (see ReferenceVectors/README.md).
/// </summary>
[TestFixture]
public class NowCompressWriterTests {
  private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);

  private static byte[] Vector(string name) {
    using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream($"NowCompressVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded Now Compress vector '{name}' is missing.");
    using var output = new MemoryStream();
    input.CopyTo(output);
    return output.ToArray();
  }

  private static byte[] Text(int length) {
    var sb = new StringBuilder();
    for (var i = 0; sb.Length < length; ++i)
      sb.Append($"Now Compress line {i % 53}, value {i * 7 % 101}, padding {new string('x', i % 9)}\r");
    return Encoding.ASCII.GetBytes(sb.ToString()[..length]);
  }

  private static byte[] Noise(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  internal static IReadOnlyList<NowCompressInput> OursInputs() => [
    new("Docs", IsDirectory: true, LastModified: Stamp),
    new("Docs/notes.txt", Data: Text(70000), LastModified: Stamp),
    new("Docs/Sub/noise.bin", Data: Noise(5000, 1), LastModified: Stamp),
    new("empty", LastModified: Stamp), // written last: no file with content may follow an empty one
    new("Both forks", Data: Text(1001), Resource: Noise(40000, 2), LastModified: Stamp),
    new("Resource only", Resource: Text(300), LastModified: Stamp),
    new("tiny", Data: "a"u8.ToArray(), LastModified: Stamp),
    new("Café ƒ™ ≈", Data: Text(3), LastModified: Stamp),
  ];

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  private static List<NowCompressEntry> RoundTrip(IEnumerable<NowCompressInput> inputs, bool compress = true) {
    using var ms = new MemoryStream();
    NowCompressWriter.Write(ms, inputs, compress);
    ms.Position = 0;
    Assert.That(NowCompressArchive.IsNowCompress(ms.ToArray()), Is.True);
    return NowCompressArchive.Read(ms);
  }

  // ── round trips ──────────────────────────────────────────────────────

  [TestCase(true)]
  [TestCase(false)]
  [Category("HappyPath")]
  public void Given_FilesAndFolders_When_WrittenAndRead_Then_ForksPathsAndTimesSurvive(bool compress) {
    var inputs = OursInputs();
    var entries = RoundTrip(inputs, compress);
    Assert.That(entries.Select(e => e.Path), Is.EqualTo(new[] {
      "Docs", "Docs/notes.txt", "Docs/Sub", "Docs/Sub/noise.bin", "Both forks", "Resource only", "tiny", "Café ƒ™ ≈", "empty",
    }));
    foreach (var input in inputs.Where(i => !i.IsDirectory)) {
      var e = entries.Single(x => x.Path == input.Path);
      Assert.That(e.Data, Is.EqualTo(input.Data ?? []), input.Path);
      Assert.That(e.Resource, Is.EqualTo(input.Resource ?? []), input.Path);
      Assert.That(e.LastModified, Is.EqualTo(Stamp), input.Path);
    }
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(0x7FFF)]
  [TestCase(0x8000)]
  [TestCase(0x8001)]
  [TestCase(0x10000)]
  [Category("Boundary")]
  public void Given_ForkAroundTheBlockSize_When_WrittenAndRead_Then_Identical(int length) {
    var text = Text(length);
    var noise = Noise(length, length);
    var entries = RoundTrip([new("t", Data: text, Resource: noise)]);
    Assert.That(entries.Single().Data, Is.EqualTo(text));
    Assert.That(entries.Single().Resource, Is.EqualTo(noise));
  }

  [Test, Category("Boundary")]
  public void Given_LongRunsAndFarRepeats_When_Packed_Then_LzssDecodesThemAndShrinks() {
    var data = new byte[0x8000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = i < 300 ? (byte)'z' : (byte)(i % 8191 < 4000 ? i * 31 : i);
    var packed = NowCompressWriter.Lzss(data);
    Assert.That(packed, Is.Not.Null);
    Assert.That(packed!.Length, Is.LessThan(data.Length / 2));
    Assert.That(RoundTrip([new("r", Data: data)]).Single().Data, Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_IncompressibleData_When_Written_Then_StoredBlocks() {
    var noise = Noise(5000, 9);
    Assert.That(NowCompressWriter.Lzss(noise), Is.Null);
    using var ms = new MemoryStream();
    NowCompressWriter.Write(ms, [new("n", Data: noise)]);
    var bytes = ms.ToArray();
    Assert.That(bytes.AsSpan().IndexOf(noise), Is.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void Given_Archive_When_Written_Then_HeaderAndRecordChecksumsHold() {
    using var ms = new MemoryStream();
    NowCompressWriter.Write(ms, OursInputs());
    var b = ms.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20)), Is.EqualTo(NowCompressWriter.ByteSum(b.AsSpan(0, 20))));
    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
    Assert.That(count, Is.EqualTo(9));
    for (var i = 0; i < count; ++i) {
      var record = b.AsSpan(24 + i * 110, 110);
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(record[106..]), Is.EqualTo(NowCompressWriter.ByteSum(record[..106])));
    }
  }

  // ── refusals ─────────────────────────────────────────────────────────

  [TestCase("a:b", TestName = "Name with a colon")]
  [TestCase("tab\there", TestName = "Name with a control character")]
  [TestCase("abcdefghijklmnopqrstuvwxyz012345", TestName = "Name of 32 characters")]
  [TestCase("日本", TestName = "Name outside Mac OS Roman")]
  [Category("Exception")]
  public void Given_UnrepresentableName_When_Written_Then_Refused(string name)
    => Assert.Throws<ArgumentException>(() => NowCompressWriter.Write(new MemoryStream(), [new(name, Data: [1])]));

  [Test, Category("Boundary")]
  public void Given_NameOf31Characters_When_Written_Then_Kept() {
    var name = new string('n', 31);
    Assert.That(RoundTrip([new(name, Data: [1])]).Single().Path, Is.EqualTo(name));
  }

  [Test, Category("Exception")]
  public void Given_SamePathTwice_When_Written_Then_Refused()
    => Assert.Throws<ArgumentException>(() => NowCompressWriter.Write(new MemoryStream(), [new("a", Data: [1]), new("A", Data: [2])]));

  [Test, Category("Exception")]
  public void Given_FileUsedAsFolder_When_Written_Then_Refused()
    => Assert.Throws<ArgumentException>(() => NowCompressWriter.Write(new MemoryStream(), [new("a", Data: [1]), new("a/b", Data: [2])]));

  [Test, Category("Exception")]
  public void Given_EmptyFileInAFolderBeforeContent_When_Written_Then_Refused()
    => Assert.Throws<NotSupportedException>(() => NowCompressWriter.Write(new MemoryStream(),
      [new("A/empty"), new("B/full", Data: [1])]));

  [Test, Category("EdgeCase")]
  public void Given_EmptyFilesAmongSiblings_When_Written_Then_MovedBehindTheFilesWithContent() {
    var entries = RoundTrip([new("e1"), new("full", Data: [7]), new("e2"), new("Sub/x", Data: [8])]);
    Assert.That(entries.Select(e => $"{e.Path}|{e.Data.Length}"), Is.EqualTo(new[] { "full|1", "Sub|0", "Sub/x|1", "e1|0", "e2|0" }));
  }

  [Test, Category("Exception")]
  public void Given_NoEntries_When_Written_Then_Refused()
    => Assert.Throws<ArgumentException>(() => NowCompressWriter.Write(new MemoryStream(), []));

  // ── descriptor ───────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Given_RsrcSuffixedInput_When_CreatedByDescriptor_Then_BecomesTheResourceFork() {
    var descriptor = new NowCompressFormatDescriptor();
    using var ms = new MemoryStream();
    descriptor.Create(ms, [
      ArchiveInputInfo.InMemory("app", "data"u8.ToArray()),
      ArchiveInputInfo.InMemory("app.rsrc", "resource"u8.ToArray()),
      ArchiveInputInfo.InMemory("lonely.rsrc", "only"u8.ToArray()),
    ], new FormatCreateOptions());
    ms.Position = 0;
    var listing = descriptor.List(ms, null).Select(e => $"{e.Name}|{e.OriginalSize}");
    Assert.That(listing, Is.EqualTo(new[] { "app|4", "app.rsrc|8", "lonely.rsrc|4" }));
  }

  // ── the oracle ───────────────────────────────────────────────────────

  [Test, Category("ThirdPartyVector")]
  public void Given_SameInputs_When_Written_Then_BytesEqualTheFixtureUnarExtracted() {
    using var ms = new MemoryStream();
    NowCompressWriter.Write(ms, OursInputs());
    Assert.That(ms.ToArray(), Is.EqualTo(Vector("ours.now")));
  }

  // unar-ours.txt: one line per fork unar wrote, "path|length|sha256".
  [Test, Category("ThirdPartyVector")]
  public void Given_OurFixture_When_UnarExtractedIt_Then_EveryForkMatchesTheInputs() {
    var expected = new List<string>();
    foreach (var input in OursInputs().Where(i => !i.IsDirectory)) {
      if (input.Data is { Length: > 0 } || input.Resource is not { Length: > 0 })
        expected.Add($"{input.Path}|{(input.Data ?? []).Length}|{Sha(input.Data ?? [])}");
      if (input.Resource is { Length: > 0 } r)
        expected.Add($"{input.Path}.rsrc|{r.Length}|{Sha(r)}");
    }
    var captured = Encoding.UTF8.GetString(Vector("unar-ours.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Assert.That(captured.Order(StringComparer.Ordinal), Is.EqualTo(expected.Order(StringComparer.Ordinal)));
  }

  /// <summary>Writes the probe archives for the oracle into <c>CWB_NOWCOMPRESS_PROBE</c> (a directory) when set.</summary>
  [Test, Category("ThirdPartyVector")]
  public void Given_ProbeDirectory_When_Set_Then_ProbeArchivesWritten() {
    var dir = Environment.GetEnvironmentVariable("CWB_NOWCOMPRESS_PROBE");
    if (string.IsNullOrEmpty(dir))
      Assert.Ignore("Set CWB_NOWCOMPRESS_PROBE to write the probe archives.");
    Directory.CreateDirectory(dir!);
    using (var f = File.Create(Path.Combine(dir!, "ours.now")))
      NowCompressWriter.Write(f, OursInputs());
    using (var f = File.Create(Path.Combine(dir!, "stored.now")))
      NowCompressWriter.Write(f, OursInputs(), compress: false);
    Assert.Pass();
  }
}
