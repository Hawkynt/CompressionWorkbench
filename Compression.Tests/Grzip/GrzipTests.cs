using Compression.Core.Entropy.Grzip;
using Compression.Registry;
using FileFormat.Grzip;

namespace Compression.Tests.Grzip;

[TestFixture]
public class GrzipTests {

  internal static byte[] Text(int n) {
    var phrase = "GRZipII sorts blocks, ranks them and codes the ranks adaptively. "u8;
    var d = new byte[n];
    for (var i = 0; i < n; ++i) d[i] = phrase[i % phrase.Length];
    return d;
  }

  internal static byte[] Random(int n, int seed) {
    var d = new byte[n];
    new Random(seed).NextBytes(d);
    return d;
  }

  public static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty");
    yield return new TestCaseData(new byte[] { 1, 2, 3 }).SetName("Tiny");
    yield return new TestCaseData(Text(31)).SetName("Text31_Stored");
    yield return new TestCaseData(Text(32)).SetName("Text32");
    yield return new TestCaseData(Text(1000)).SetName("Text1000");
    yield return new TestCaseData(Text(100_000)).SetName("Text100k");
    yield return new TestCaseData(Random(50_000, 1)).SetName("Random50k");
    yield return new TestCaseData(new byte[70_000]).SetName("Zeros70k");
    var mixed = new byte[60_000];
    var rng = new Random(9);
    for (var i = 0; i < mixed.Length; ++i) mixed[i] = (byte)(rng.Next(4) == 0 ? rng.Next(256) : 'a' + i % 7);
    yield return new TestCaseData(mixed).SetName("Mixed60k");
    var ranks = new byte[40_000];
    for (var i = 0; i < ranks.Length; ++i) ranks[i] = (byte)(i * 37 % 251);
    yield return new TestCaseData(ranks).SetName("AllRanks40k");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void RoundTrip(byte[] data) {
    var packed = GrzipCodec.Encode(data);
    Assert.That(GrzipCodec.Decode(packed), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Encode_CompressesText() {
    var data = Text(200_000);
    Assert.That(GrzipCodec.Encode(data).Length, Is.LessThan(data.Length / 20));
  }

  [Test, Category("Spec")]
  public void Encode_StartsWithTheSignature()
    => Assert.That(GrzipCodec.Encode(Text(100))[..12], Is.EqualTo(GrzipCodec.Signature.ToArray()));

  [Test, Category("Exception")]
  public void Decode_HeaderCrcBroken_Throws() {
    var packed = GrzipCodec.Encode(Text(5000));
    packed[12] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => GrzipCodec.Decode(packed));
  }

  [Test, Category("Exception")]
  public void Decode_PayloadCorrupted_Throws() {
    var packed = GrzipCodec.Encode(Text(5000));
    packed[^3] ^= 0x40;
    Assert.Throws<InvalidDataException>(() => GrzipCodec.Decode(packed));
  }

  [Test, Category("Exception")]
  public void Decode_BadSignature_Throws()
    => Assert.Throws<InvalidDataException>(() => GrzipCodec.Decode("GRZipIII\0\0\0\0"u8));

  [Test, Category("Exception")]
  public void Decode_TruncatedBlock_Throws() {
    var packed = GrzipCodec.Encode(Text(5000));
    Assert.Throws<InvalidDataException>(() => GrzipCodec.Decode(packed.AsSpan(0, packed.Length - 5)));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsRegistered_AndRoundTrips() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    Assert.That(FormatRegistry.GetById("Grzip"), Is.Not.Null);
    var ops = (IStreamFormatOperations)new GrzipFormatDescriptor();
    using var packed = new MemoryStream();
    ops.Compress(new MemoryStream(Text(3000)), packed);
    packed.Position = 0;
    using var plain = new MemoryStream();
    ops.Decompress(packed, plain);
    Assert.That(plain.ToArray(), Is.EqualTo(Text(3000)));
  }
}

/// <summary>
/// The reference grzip 0.3.0 program, built from its sources: it must decompress what we
/// write, and we must decompress what it writes in every mode (WFC/MTF, BWT/ST4, LZP on and
/// off, the delta filter, the alternative sort).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class GrzipReferenceInteropTests {
  private const string Revision = "5752a6a2a38e16211952553fcffa59570855ac42";
  private const string Tool = $"/tmp/cwb-grzip-{Revision}/grzip";

  private static readonly Lazy<string?> BuildError = new(() => {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    foreach (var tool in new[] { "gcc", "curl" })
      if (!FsInteropToolbox.WslHasTool(tool)) return $"'{tool}' is required to build grzip.";
    var dir = $"/tmp/cwb-grzip-{Revision}";
    var files = "grzip.c BWT.c CRC32.c GRZipII.c LZP.c MTF_Ari.c Rec_Flt.c ST4.c WFC_Ari.c WFC_MTF.h grzip.h intl.h libGRZip.h libgrzip.c main.c";
    var r = FsInteropToolbox.RunWsl(
      $"test -x {Tool} && exit 0; set -e; rm -rf {dir}; mkdir -p {dir}; cd {dir}; " +
      $"for f in {files}; do curl -sSfL -o $f https://raw.githubusercontent.com/mistydemeo/quickbms/{Revision}/libs/grzip/$f; done; " +
      "gcc -O2 -w -DSMALLENDIAN -o grzip grzip.c libgrzip.c -lm");
    return r.ExitCode == 0 ? null : $"building grzip failed:\n{r.StdOut}\n{r.StdErr}";
  });

  private string _dir = null!;

  [SetUp]
  public void Setup() {
    if (BuildError.Value is { } error) Assert.Ignore(error);
    this._dir = Path.Combine(Path.GetTempPath(), $"cwb_grzip_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static IEnumerable<byte[]> Samples() {
    yield return GrzipTests.Text(100_000);
    yield return GrzipTests.Random(30_000, 2);
    var words = new byte[80_000];
    for (var i = 0; i < words.Length; i += 2) { words[i] = (byte)(i / 7); words[i + 1] = (byte)(i / 900); }
    yield return words;
    yield return new byte[20_000];
  }

  [Test]
  public void OurGrz_IsDecompressedByGrzip() {
    var n = 0;
    foreach (var data in Samples()) {
      var input = Path.Combine(this._dir, $"ours{n}.grz");
      var output = Path.Combine(this._dir, $"ours{n}.out");
      File.WriteAllBytes(input, GrzipCodec.Encode(data));
      var r = FsInteropToolbox.RunWsl($"{Tool} -d -c -q {FsInteropToolbox.WinToWsl(input)} > {FsInteropToolbox.WinToWsl(output)}");
      Assert.That(r.ExitCode, Is.EqualTo(0), $"grzip rejected sample {n}:\n{r.StdOut}\n{r.StdErr}");
      Assert.That(File.ReadAllBytes(output), Is.EqualTo(data), $"sample {n}");
      ++n;
    }
  }

  [TestCase("-m1")]
  [TestCase("-m2")]
  [TestCase("-m3")]
  [TestCase("-m4")]
  [TestCase("-m1 -l")]
  [TestCase("-m1 -D")]
  [TestCase("-m2 -S")]
  [TestCase("-m1 -b1m")]
  public void GrzipOutput_IsDecodedByUs(string options) {
    var n = 0;
    foreach (var data in Samples()) {
      var input = Path.Combine(this._dir, $"theirs{n}.bin");
      var packed = Path.Combine(this._dir, $"theirs{n}.grz");
      File.WriteAllBytes(input, data);
      var r = FsInteropToolbox.RunWsl($"{Tool} {options} -c -q {FsInteropToolbox.WinToWsl(input)} > {FsInteropToolbox.WinToWsl(packed)}");
      Assert.That(r.ExitCode, Is.EqualTo(0), $"grzip {options} failed:\n{r.StdOut}\n{r.StdErr}");
      Assert.That(GrzipCodec.Decode(File.ReadAllBytes(packed)), Is.EqualTo(data), $"{options}, sample {n}");
      ++n;
    }
  }
}
