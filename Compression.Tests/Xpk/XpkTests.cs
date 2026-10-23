using System.Reflection;
using Compression.Core.Dictionary.Xpk;
using Compression.Registry;
using FileFormat.Gzip;
using FileFormat.Xpk;

namespace Compression.Tests.Xpk;

[TestFixture]
public class XpkTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"XpkVectors.{name}")
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly Lazy<byte[]> Original = new(() => {
    using var plain = new MemoryStream();
    new GzipFormatDescriptor().Decompress(new MemoryStream(Vector("test_C1.raw.gz")), plain);
    return plain.ToArray();
  });

  [TestCase("test_C1_sqsh.xpkf", "SQSH")]
  [TestCase("test_C1_nuke.xpkf", "NUKE")]
  [TestCase("test_C1_duke.xpkf", "DUKE")]
  [Category("Compatibility")]
  public void Decode_AmigaSubLibraryFile_MatchesTheOriginal(string file, string type) {
    var packed = Vector(file);
    Assert.Multiple(() => {
      Assert.That(XpkStream.ReadType(packed), Is.EqualTo(type));
      Assert.That(XpkStream.Decode(packed), Is.EqualTo(Original.Value));
    });
  }

  public static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(new byte[] { 42 }).SetName("OneByte");
    yield return new TestCaseData(new byte[200]).SetName("Zeros200");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("AllBytes");
    var mixed = new byte[100_000];
    var rng = new Random(4);
    for (var i = 0; i < mixed.Length;) {
      var run = rng.Next(1, 300);
      var v = (byte)rng.Next(256);
      var literal = rng.Next(2) == 0;
      for (var k = 0; k < run && i < mixed.Length; ++k) mixed[i++] = literal ? (byte)rng.Next(256) : v;
    }
    yield return new TestCaseData(mixed).SetName("MixedRunsAndLiterals100k");
    var random = new byte[40_000];
    new Random(8).NextBytes(random);
    yield return new TestCaseData(random).SetName("Random40k");
    yield return new TestCaseData(new byte[0x8000]).SetName("ExactlyOneChunk");
    yield return new TestCaseData(new byte[0x8001]).SetName("OneChunkPlusOne");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void EncodeRlen_RoundTrips(byte[] data) => Assert.That(XpkStream.Decode(XpkStream.Encode(data, "RLEN")), Is.EqualTo(data));

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void EncodeNone_RoundTrips(byte[] data) => Assert.That(XpkStream.Decode(XpkStream.Encode(data, "NONE")), Is.EqualTo(data));

  [Test, Category("Spec")]
  public void Rlen_RunAndLiteralCodes() {
    // 0xFD = run of 3, then a literal count of 2.
    Assert.That(XpkSubLibraries.DecodeRlen([0xFD, 0x41, 0x02, 0x42, 0x43], 5), Is.EqualTo("AAABC"u8.ToArray()));
  }

  [Test, Category("Exception")]
  public void Rlen_ZeroCount_Throws()
    => Assert.Throws<InvalidDataException>(() => XpkSubLibraries.DecodeRlen([0x00, 0x41], 1));

  [Test, Category("Exception")]
  public void Decode_HeaderChecksumBroken_Throws() {
    var packed = XpkStream.Encode("checksum"u8.ToArray());
    packed[20] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => XpkStream.Decode(packed));
  }

  [Test, Category("Exception")]
  public void Decode_ChunkDataCorrupted_Throws() {
    var packed = XpkStream.Encode(new byte[500], "RLEN");
    packed[36 + 8] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => XpkStream.Decode(packed));
  }

  [Test, Category("Exception")]
  public void Decode_UnimplementedSubLibrary_IsRefused() {
    var packed = XpkStream.Encode(new byte[100], "RLEN");
    "ZZZZ"u8.CopyTo(packed.AsSpan(8));
    packed[33] = 0;
    byte x = 0;
    for (var i = 0; i < 36; ++i) x ^= packed[i];
    packed[33] = x;
    Assert.Throws<NotSupportedException>(() => XpkStream.Decode(packed));
  }

  [Test, Category("Exception")]
  public void Encode_EmptyInput_IsRefused()
    => Assert.Throws<ArgumentException>(() => XpkStream.Encode([]));

  [Test, Category("HappyPath")]
  public void Descriptor_IsRegisteredByMagic_AndRoundTrips() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    Assert.That(FormatRegistry.GetById("Xpk"), Is.Not.Null);
    var ops = (IStreamFormatOperations)new XpkFormatDescriptor();
    using var packed = new MemoryStream();
    ops.Compress(new MemoryStream("xpk descriptor round trip"u8.ToArray()), packed);
    packed.Position = 0;
    using var plain = new MemoryStream();
    ops.Decompress(packed, plain);
    Assert.That(plain.ToArray(), Is.EqualTo("xpk descriptor round trip"u8.ToArray()));
  }
}

/// <summary>ancient (the reference XPK decoder) must decode the XPK files we write.</summary>
[TestFixture]
[Category("ExternalInterop")]
public class XpkAncientInteropTests {
  private const string Revision = "61cd9088a218ce43fd389f3f3c48f35fc70f7834";
  private const string Tool = $"/tmp/cwb-ancient-{Revision}/obj/ancient";

  internal static readonly Lazy<string?> BuildError = new(() => {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    foreach (var tool in new[] { "g++", "make", "curl", "tar" })
      if (!FsInteropToolbox.WslHasTool(tool)) return $"'{tool}' is required to build ancient.";
    var dir = $"/tmp/cwb-ancient-{Revision}";
    var r = FsInteropToolbox.RunWsl(
      $"test -x {Tool} && exit 0; set -e; rm -rf {dir}; mkdir -p {dir}; cd {dir}; " +
      $"curl -sSL https://github.com/temisu/ancient/archive/{Revision}.tar.gz | tar xz --strip-components=1; " +
      "make -s -j2 -f Makefile.unix obj/ancient > build.log 2>&1 || (tail -30 build.log; exit 1)");
    return r.ExitCode == 0 ? null : $"building ancient failed:\n{r.StdOut}\n{r.StdErr}";
  });

  internal static string AncientTool => Tool;

  [TestCase("RLEN")]
  [TestCase("NONE")]
  public void OurXpk_IsDecodedByAncient(string type) {
    if (BuildError.Value is { } error)
      Assert.Ignore(error);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_xpk_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      foreach (var data in XpkTests.Payloads().Select(t => (byte[])t.Arguments[0]!)) {
        var input = Path.Combine(dir, "in.xpk");
        var output = Path.Combine(dir, "out.bin");
        File.WriteAllBytes(input, XpkStream.Encode(data, type));
        File.Delete(output);
        var r = FsInteropToolbox.RunWsl($"{Tool} d {FsInteropToolbox.WinToWsl(input)} {FsInteropToolbox.WinToWsl(output)}");
        Assert.That(r.ExitCode, Is.EqualTo(0), $"ancient rejected {type} ({data.Length} bytes):\n{r.StdOut}\n{r.StdErr}");
        Assert.That(File.ReadAllBytes(output), Is.EqualTo(data), $"{type}, {data.Length} bytes");
      }
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
