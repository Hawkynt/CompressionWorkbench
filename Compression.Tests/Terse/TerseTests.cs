using System.Reflection;
using Compression.Core.Dictionary.Terse;
using Compression.Registry;
using FileFormat.Terse;

namespace Compression.Tests.Terse;

/// <summary>
/// IBM TERSE: decoding files IBM AMATERSE wrote (see ReferenceVectors/README.md) and
/// round-tripping our PACK encoder.
/// </summary>
[TestFixture]
public class TerseTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TerseVectors.{name}")
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [TestCase("FB.XARGS", "PACK")]
  [TestCase("FB.XARGS", "SPACK")]
  [TestCase("VB.XARGS", "PACK")]
  [TestCase("VB.XARGS", "SPACK")]
  [TestCase("FB.GRAMMAR.LSP", "PACK")]
  [TestCase("FB.GRAMMAR.LSP", "SPACK")]
  [Category("Compatibility")]
  public void Decode_AmaterseFile_MatchesTheZosDataset(string dataset, string method) {
    var decoded = TerseCodec.Decode(Vector($"{dataset}.{method}"));
    Assert.That(decoded, Is.EqualTo(Vector($"{dataset}.expected")));
  }

  [Test, Category("Compatibility")]
  public void ReadHeader_HostFiles_AreRecognised() {
    var pack = TerseCodec.ReadHeader(Vector("VB.XARGS.PACK"));
    var spack = TerseCodec.ReadHeader(Vector("FB.XARGS.SPACK"));
    Assert.Multiple(() => {
      Assert.That(pack.IsHost, Is.True);
      Assert.That(pack.IsSpack, Is.False);
      Assert.That(pack.IsVariable, Is.True);
      Assert.That(spack.IsSpack, Is.True);
      Assert.That(spack.IsVariable, Is.False);
    });
  }

  public static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty");
    yield return new TestCaseData(new byte[] { 0 }).SetName("OneZero");
    yield return new TestCaseData(new byte[] { 0x40, 0x40, 0x40 }).SetName("EbcdicSpaces");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("AllBytes");
    var text = new byte[30_000];
    var phrase = "TERSE keeps a 4096-entry dictionary and recycles the least recently used. "u8;
    for (var i = 0; i < text.Length; ++i) text[i] = phrase[i % phrase.Length];
    yield return new TestCaseData(text).SetName("Text30k");
    var random = new byte[20_000];
    new Random(3).NextBytes(random);
    yield return new TestCaseData(random).SetName("Random20k");
    yield return new TestCaseData(new byte[50_000]).SetName("Zeros50k");
    var spaces = new byte[9_000];
    Array.Fill(spaces, (byte)0x40);
    yield return new TestCaseData(spaces).SetName("EbcdicSpaces9000");
    var kwkwk = new byte[5_000];
    for (var i = 0; i < kwkwk.Length; ++i) kwkwk[i] = (byte)(i % 3 == 0 ? 'a' : 'b');
    yield return new TestCaseData(kwkwk).SetName("RepeatingPattern");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void EncodePack_RoundTrips(byte[] data) {
    var packed = TerseCodec.EncodePack(data);
    Assert.Multiple(() => {
      Assert.That(packed[0], Is.EqualTo(0x02), "host header, PACK");
      Assert.That(TerseCodec.Decode(packed), Is.EqualTo(data));
    });
  }

  [Test, Category("RoundTrip")]
  public void EncodePack_PastDictionaryRecycling_RoundTrips() {
    // Enough distinct material to fill all 3838 nodes several times over.
    var data = new byte[200_000];
    var rng = new Random(11);
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)('a' + rng.Next(6));
    Assert.That(TerseCodec.Decode(TerseCodec.EncodePack(data)), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void EncodePack_CompressesText() {
    var data = new byte[60_000];
    var phrase = "the same words again and again and again "u8;
    for (var i = 0; i < data.Length; ++i) data[i] = phrase[i % phrase.Length];
    Assert.That(TerseCodec.EncodePack(data).Length, Is.LessThan(data.Length / 3));
  }

  [Test, Category("Exception")]
  public void ReadHeader_UnknownVersion_Throws()
    => Assert.Throws<InvalidDataException>(() => TerseCodec.ReadHeader([0x09, 0, 0, 0, 0, 0]));

  [Test, Category("Exception")]
  public void ReadHeader_BadNativeValidationBytes_Throws()
    => Assert.Throws<InvalidDataException>(() => TerseCodec.ReadHeader([0x01, 0x89, 0x69, 0xA6, 0, 0]));

  [Test, Category("Exception")]
  public void ReadHeader_HostZeroRecordLength_Throws()
    => Assert.Throws<InvalidDataException>(() => TerseCodec.ReadHeader([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));

  [Test, Category("Exception")]
  public void Decode_TruncatedCode_Throws() {
    byte[] file = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0x04]; // one byte of a 12-bit code
    Assert.Throws<InvalidDataException>(() => TerseCodec.Decode(file));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsRegistered_AndRoundTrips() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    Assert.That(FormatRegistry.GetById("Terse"), Is.Not.Null);
    var ops = (IStreamFormatOperations)new TerseFormatDescriptor();
    using var packed = new MemoryStream();
    ops.Compress(new MemoryStream("descriptor"u8.ToArray()), packed);
    packed.Position = 0;
    using var plain = new MemoryStream();
    ops.Decompress(packed, plain);
    Assert.That(plain.ToArray(), Is.EqualTo("descriptor"u8.ToArray()));
  }
}

/// <summary>The reference decoder (TerseDecompress, C++ build) must decode our PACK output.</summary>
[TestFixture]
[Category("ExternalInterop")]
public class TerseReferenceDecoderInteropTests {
  private const string Revision = "12f5f9f34821defeef696c3232d97b7733dc79a6";
  private const string Tool = $"/tmp/cwb-tersedecompress-{Revision}/tersedecompress";

  private static readonly Lazy<string?> BuildError = new(() => {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    foreach (var tool in new[] { "g++", "curl", "tar" })
      if (!FsInteropToolbox.WslHasTool(tool)) return $"'{tool}' is required to build TerseDecompress.";
    var dir = $"/tmp/cwb-tersedecompress-{Revision}";
    var r = FsInteropToolbox.RunWsl(
      $"test -x {Tool} && exit 0; set -e; rm -rf {dir}; mkdir -p {dir}; cd {dir}; " +
      $"curl -sSL https://github.com/openmainframeproject/tersedecompress/archive/{Revision}.tar.gz | tar xz --strip-components=1; " +
      "g++ -O1 -w -std=c++17 -DGIT_TAG=0 -o tersedecompress cpp/src/*.cpp");
    return r.ExitCode == 0 ? null : $"building TerseDecompress failed:\n{r.StdOut}\n{r.StdErr}";
  });

  [Test]
  public void OurPack_IsDecodedByTerseDecompress() {
    if (BuildError.Value is { } error)
      Assert.Ignore(error);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_terse_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      foreach (var data in TerseTests.Payloads().Select(t => (byte[])t.Arguments[0]!)) {
        if (data.Length == 0) continue; // TERSE does not process empty files
        var input = Path.Combine(dir, "in.tersed");
        var output = Path.Combine(dir, "out.bin");
        File.WriteAllBytes(input, TerseCodec.EncodePack(data));
        File.Delete(output);
        var r = FsInteropToolbox.RunWsl($"{Tool} -b {FsInteropToolbox.WinToWsl(input)} {FsInteropToolbox.WinToWsl(output)}");
        Assert.That(r.ExitCode, Is.EqualTo(0), $"TerseDecompress failed for {data.Length} bytes:\n{r.StdOut}\n{r.StdErr}");
        Assert.That(File.ReadAllBytes(output), Is.EqualTo(data), $"{data.Length}-byte payload");
      }
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
