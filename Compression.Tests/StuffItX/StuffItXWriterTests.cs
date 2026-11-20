using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.StuffItX;

namespace Compression.Tests.StuffItX;

/// <summary>
/// The StuffIt X writer, with The Unarchiver's unar as the oracle: it must extract what we write
/// byte for byte. Streams in codings we cannot produce (x86 preprocessing, Blend) are handed to
/// unar through the writer's raw path, and our reader must decode them exactly as unar does.
/// </summary>
[TestFixture]
public class StuffItXWriterTests {
  private static readonly DateTime When = new(2009, 7, 1, 12, 30, 0, DateTimeKind.Utc);

  private static List<StuffItXWriter.Input> Inputs() => [
    new("hello.txt", "hello sitx\n"u8.ToArray(), When),
    new("docs/fox.txt", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog.\n", 400))), When),
    new("docs/deep/empty.bin", [], When),
    new("docs/empty dir", null, When),
    new("noise.bin", Noise(200_000, 7), When), // more than one 64 KiB block
    new("twice.bin", Twice(), When), // far matches, repeats and runs longer than one match
  ];

  /// <summary>Noise, the same noise again 70 000 bytes later, runs of one byte and binary values above 127.</summary>
  private static byte[] Twice() {
    var noise = Noise(70_000, 9);
    var run = Enumerable.Repeat((byte)0xC3, 1000).ToArray();
    var pattern = Enumerable.Range(0, 3000).Select(i => (byte)(i % 7 == 0 ? 0x80 + i % 100 : 'a' + i % 13)).ToArray();
    return [.. noise, .. run, .. pattern, .. noise, .. pattern, .. run];
  }

  private static byte[] Noise(int n, int seed) {
    var b = new byte[n];
    new Random(seed).NextBytes(b);
    return b;
  }

  private static byte[] Write(IEnumerable<StuffItXWriter.Input> inputs, StuffItXCompression compression) {
    using var ms = new MemoryStream();
    StuffItXWriter.Write(ms, inputs, compression);
    return ms.ToArray();
  }

  [TestCase(StuffItXCompression.Store)]
  [TestCase(StuffItXCompression.Deflate)]
  [TestCase(StuffItXCompression.Darkhorse)]
  [TestCase(StuffItXCompression.Brimstone)]
  [TestCase(StuffItXCompression.Cyanide)]
  [TestCase(StuffItXCompression.Blend)]
  [TestCase(StuffItXCompression.Obscured)]
  [Category("RoundTrip")]
  public void GivenFiles_WhenWrittenAndRead_ThenEveryFileFolderAndDateRoundTrips(StuffItXCompression compression) {
    using var r = new StuffItXReader(new MemoryStream(Write(Inputs(), compression)));
    foreach (var input in Inputs().Where(i => i.Data != null)) {
      var e = r.Entries.Single(x => x.FullPath == input.Path);
      Assert.That(r.Extract(e), Is.EqualTo(input.Data), input.Path);
      Assert.That(e.LastModified, Is.EqualTo(When), input.Path);
    }
    Assert.That(r.Entries.Where(e => e.IsDirectory).Select(e => e.FullPath), Is.EquivalentTo(new[] { "docs", "docs/deep", "docs/empty dir" }));
    foreach (var id in r.StreamIds) r.DecodeStream(id); // the stored CRC-32 is checked
  }

  [Test, Category("EdgeCase")]
  public void GivenOnlyFoldersAndEmptyFiles_WhenWrittenAndRead_ThenTheyAllComeBack() {
    using var r = new StuffItXReader(new MemoryStream(Write([new("a", null), new("a/b.txt", [])], StuffItXCompression.Deflate)));
    Assert.That(r.Entries.Select(e => (e.FullPath, e.IsDirectory)), Is.EquivalentTo(new[] { ("a", true), ("a/b.txt", false) }));
  }

  [Test, Category("HappyPath")]
  public void GivenDeflate_WhenWritten_ThenTheStreamShrinksAndIsNamedDeflate() {
    var archive = Write(Inputs(), StuffItXCompression.Deflate);
    Assert.That(archive.Length, Is.LessThan(Write(Inputs(), StuffItXCompression.Store).Length));
    using var r = new StuffItXReader(new MemoryStream(archive));
    Assert.That(r.Entries.Single(e => e.FullPath == "hello.txt").Method, Is.EqualTo("Deflate"));
  }

  [TestCase(StuffItXCompression.Darkhorse, "Darkhorse")]
  [TestCase(StuffItXCompression.Brimstone, "Brimstone")]
  [TestCase(StuffItXCompression.Cyanide, "Cyanide")]
  [TestCase(StuffItXCompression.Blend, "Blend")]
  [Category("HappyPath")]
  public void GivenAModelCoder_WhenWritten_ThenTheStreamShrinksAndIsNamedForIt(StuffItXCompression compression, string name) {
    var text = Inputs().Where(i => i.Path.EndsWith(".txt", StringComparison.Ordinal)).ToList();
    var archive = Write(text, compression);
    Assert.That(archive.Length, Is.LessThan(Write(text, StuffItXCompression.Store).Length / 2));
    using var r = new StuffItXReader(new MemoryStream(archive));
    Assert.That(r.Entries.Single(e => e.FullPath == "hello.txt").Method, Is.EqualTo(name));
  }

  [Test, Category("Exception")]
  public void GivenPassword_WhenCreatedThroughTheDescriptor_ThenRefused()
    => Assert.That(() => new StuffItXFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])], new FormatCreateOptions { Password = "x" }),
      Throws.InstanceOf<NotSupportedException>());

  [TestCase("store")]
  [TestCase("deflate")]
  [TestCase("darkhorse")]
  [TestCase("brimstone")]
  [TestCase("cyanide")]
  [TestCase("blend")]
  [TestCase("obscured")]
  [TestCase("darkhorse+x86")]
  [Category("HappyPath")]
  public void GivenMethod_WhenCreatedThroughTheDescriptor_ThenTheArchiveReadsBack(string method) {
    using var ms = new MemoryStream();
    new StuffItXFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("x/y.txt", "y"u8.ToArray())], new FormatCreateOptions { MethodName = method });
    Assert.That(new StuffItXFormatDescriptor().List(new MemoryStream(ms.ToArray()), null).Single(e => !e.IsDirectory).Name, Is.EqualTo("x/y.txt"));
  }

  // ── unar as the oracle ────────────────────────────────────────────────────────

  [TestCase(StuffItXCompression.Store)]
  [TestCase(StuffItXCompression.Deflate)]
  [TestCase(StuffItXCompression.Darkhorse)]
  [TestCase(StuffItXCompression.Brimstone)]
  [TestCase(StuffItXCompression.Cyanide)]
  [TestCase(StuffItXCompression.Blend)]
  [TestCase(StuffItXCompression.Obscured)]
  [Category("ExternalInterop")]
  public void GivenOurArchive_WhenUnarExtractsIt_ThenEveryFileMatches(StuffItXCompression compression) {
    var theirs = Unar(Write(Inputs(), compression));
    foreach (var input in Inputs().Where(i => i.Data != null))
      Assert.That(theirs[input.Path], Is.EqualTo(input.Data), input.Path);
    Assert.That(theirs.Keys, Has.Member("docs/empty dir/"), "the empty folder");
  }

  [TestCase(StuffItXCompression.Store)]
  [TestCase(StuffItXCompression.Cyanide)]
  [TestCase(StuffItXCompression.Darkhorse)]
  [Category("ExternalInterop")]
  public void GivenX86PreprocessingWeWrote_WhenUnarAndWeExtractIt_ThenTheCodeComesBack(StuffItXCompression compression) {
    var payload = X86Payload();
    using var ms = new MemoryStream();
    StuffItXWriter.Write(ms, [new("code.bin", payload)], compression, x86: true);
    var archive = ms.ToArray();
    using (var r = new StuffItXReader(new MemoryStream(archive)))
      Assert.That(r.Extract(r.Entries.Single(e => e.FullPath == "code.bin")), Is.EqualTo(payload));
    Assert.That(Unar(archive)["code.bin"], Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void GivenCode_WhenX86FilteredAndUnfiltered_ThenItIsTheSame() {
    var payload = X86Payload();
    var filtered = StuffItXX86.Encode(payload);
    Assert.That(filtered, Is.Not.EqualTo(payload));
    Assert.That(StuffItXX86.Decode(filtered, filtered.Length), Is.EqualTo(payload));
  }

  /// <summary>
  /// E8/E9 operands with top bytes 00 and FF, close together and far apart, so the filter's
  /// history takes every branch.
  /// </summary>
  private static byte[] X86Payload() {
    var rng = new Random(86);
    var b = new List<byte>();
    for (var i = 0; i < 3000; ++i) {
      switch (rng.Next(6)) {
        case 0:
        case 1: {
          b.Add(rng.Next(2) == 0 ? (byte)0xE8 : (byte)0xE9);
          var operand = new byte[4];
          BinaryPrimitives.WriteInt32LittleEndian(operand, rng.Next(-0x800000, 0x800000) + b.Count);
          if (rng.Next(4) == 0) operand[3] = (byte)rng.Next(256);
          b.AddRange(operand);
          break;
        }
        case 2:
          b.Add(0xE8); // a run of call bytes: hits one to five bytes apart
          break;
        default:
          b.Add((byte)rng.Next(256));
          break;
      }
    }
    // unar reads an operand's four bytes even at the very end; give the last call room for them
    b.AddRange(new byte[8]);
    return [.. b];
  }

  [Test, Category("ExternalInterop")]
  public void GivenX86PreprocessedStream_WhenUnarAndWeDecodeIt_ThenTheBytesAgree() {
    var payload = X86Payload();
    using var ms = new MemoryStream();
    // stored coding, x86 filter, no CRC: the decoded bytes are what unar says they are
    StuffItXWriter.WriteCoded(ms, [new("code.bin", payload)], -1, 2, static d => d, checksum: false);
    var archive = ms.ToArray();

    var theirs = Unar(archive)["code.bin"];
    using var r = new StuffItXReader(new MemoryStream(archive));
    var ours = r.Extract(r.Entries.Single(e => e.FullPath == "code.bin"));
    Assert.That(theirs, Is.Not.EqualTo(payload), "the filter must have changed something");
    Assert.That(ours, Is.EqualTo(theirs));
  }

  /// <summary>Stored Blend segments, with stray 0x77 bytes between and inside them.</summary>
  private static byte[] Blend(byte[] data) {
    var ms = new MemoryStream();
    var rng = new Random(4);
    var be = new byte[4];
    for (var offset = 0; offset < data.Length;) {
      var size = Math.Min(data.Length - offset, rng.Next(1, 9000));
      ms.Write([0x77, 0x00]);
      BinaryPrimitives.WriteUInt32BigEndian(be, (uint)size);
      ms.Write(be);
      ms.Write(data, offset, size);
      offset += size;
      if (rng.Next(3) == 0) ms.Write([0x12, 0x77, 0x09]); // noise the reader resynchronises over
    }
    return ms.ToArray();
  }

  [Test, Category("RoundTrip")]
  public void GivenBlendStreamOfStoredSegments_WhenRead_ThenTheSegmentsJoin() {
    // stored segments: unar cannot cut them from its block stream, so this one is ours alone
    var data = Noise(40_000, 11);
    for (var i = 0; i < data.Length; i += 97) data[i] = 0x77;
    using var ms = new MemoryStream();
    StuffItXWriter.WriteCoded(ms, [new("blend.bin", data)], 4, -1, Blend);
    using var r = new StuffItXReader(new MemoryStream(ms.ToArray()));
    Assert.That(r.Extract(r.Entries.Single(e => e.FullPath == "blend.bin")), Is.EqualTo(data));
  }

  /// <summary>
  /// A Blend stream of segments StuffIt itself coded: the Brimstone and Cyanide streams of the
  /// StuffIt 7 archive in ReferenceVectors, each behind a Blend header, with stray bytes between.
  /// </summary>
  [Test, Category("ExternalInterop")]
  public void GivenBlendStreamOfStuffItCodedSegments_WhenUnarAndWeDecodeIt_ThenBothGiveTheData() {
    using var source = new StuffItXReader(new MemoryStream(Vector("testfile.stuffit7_dlx.mac9.sitx")));
    var blend = new MemoryStream();
    var expected = new MemoryStream();
    var be = new byte[4];
    foreach (var id in source.StreamIds) {
      var type = source.StreamMethod(id) switch { "Brimstone" => 3, "Cyanide" => 2, _ => -1 };
      if (type < 0) continue;
      var (payload, length) = source.StreamPayload(id);
      var decoded = source.DecodeStream(id).AsSpan(0, (int)length).ToArray(); // CRC-checked
      if (decoded.Length == 0) continue;
      blend.Write([0x77, (byte)type]);
      BinaryPrimitives.WriteUInt32BigEndian(be, (uint)decoded.Length);
      blend.Write(be);
      blend.Write(payload);
      blend.Write([0x12, 0x77, 0x09]); // noise the reader resynchronises over
      expected.Write(decoded);
    }
    var data = expected.ToArray();
    Assert.That(data.Length, Is.GreaterThan(1000));
    using var ms = new MemoryStream();
    StuffItXWriter.WriteCoded(ms, [new("blend.bin", data)], 4, -1, _ => blend.ToArray());
    var archive = ms.ToArray();

    Assert.That(Unar(archive)["blend.bin"], Is.EqualTo(data));
    using var r = new StuffItXReader(new MemoryStream(archive));
    Assert.That(r.Extract(r.Entries.Single(e => e.FullPath == "blend.bin")), Is.EqualTo(data));
  }

  private static byte[] Vector(string name) {
    using var s = typeof(StuffItXWriterTests).Assembly.GetManifestResourceStream("StuffItXVectors." + name)
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>unar's extraction of <paramref name="archive"/>: file contents by '/'-path, folders as "path/".</summary>
  /// <remarks>
  /// <c>CWB_UNAR</c> names another unar (a WSL path), e.g. one built from current XADMaster: the
  /// unar 1.10.1 Ubuntu ships predates Blend and reports such streams as not fully supported.
  /// </remarks>
  private static Dictionary<string, byte[]> Unar(byte[] archive) {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    var unar = Environment.GetEnvironmentVariable("CWB_UNAR") ?? "unar";
    if (unar == "unar" && !FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sitxw_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var path = Path.Combine(tmp, "ours.sitx");
      File.WriteAllBytes(path, archive);
      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"'{unar}' -q -f -k skip -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      if (run.ExitCode != 0 && (run.StdOut + run.StdErr).Contains("not fully supported", StringComparison.Ordinal))
        Assert.Ignore($"this unar cannot decode the coding (set CWB_UNAR to one built from current XADMaster):\n{run.StdOut}");
      Assert.That(run.ExitCode, Is.Zero, $"unar failed:\n{run.StdOut}\n{run.StdErr}");
      var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
      foreach (var f in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        result[Path.GetRelativePath(outDir, f).Replace('\\', '/')] = File.ReadAllBytes(f);
      foreach (var d in Directory.EnumerateDirectories(outDir, "*", SearchOption.AllDirectories))
        result[Path.GetRelativePath(outDir, d).Replace('\\', '/') + "/"] = [];
      return result;
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
