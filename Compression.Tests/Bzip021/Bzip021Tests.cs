using Compression.Core.Streams.Bzip021;
using Compression.Registry;
using FileFormat.Bzip021;

namespace Compression.Tests.Bzip021;

[TestFixture]
public class Bzip021Tests {

  internal static byte[] Text(int n) {
    var phrase = "bzip 0.21 coded its move-to-front values arithmetically. "u8;
    var d = new byte[n];
    for (var i = 0; i < n; ++i) d[i] = phrase[i % phrase.Length];
    return d;
  }

  public static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty");
    yield return new TestCaseData(new byte[] { 7 }).SetName("OneByte");
    yield return new TestCaseData(new byte[] { 5, 5, 5, 5 }).SetName("RunOfFour");
    yield return new TestCaseData(new byte[300]).SetName("RunPast255");
    yield return new TestCaseData(new byte[] { 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2 }).SetName("TwoRuns");
    yield return new TestCaseData(Text(20_000)).SetName("Text20k_SpottingActive");
    var rnd = new byte[30_000];
    new Random(5).NextBytes(rnd);
    yield return new TestCaseData(rnd).SetName("Random30k");
    yield return new TestCaseData(Text(250_000)).SetName("Text250k_MultiBlockAtLevel1");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void RoundTrip_DefaultBlockSize(byte[] data)
    => Assert.That(Bzip021Codec.Decode(Bzip021Codec.Encode(data)), Is.EqualTo(data));

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void RoundTrip_SmallestBlockSize(byte[] data)
    => Assert.That(Bzip021Codec.Decode(Bzip021Codec.Encode(data, blockSize100k: 1)), Is.EqualTo(data));

  [Test, Category("Spec")]
  public void Encode_WritesMagicAndLevelDigit() {
    var packed = Bzip021Codec.Encode(Text(10), blockSize100k: 4);
    Assert.That(packed[..4], Is.EqualTo("BZ04"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Encode_CompressesText() {
    var data = Text(100_000);
    Assert.That(Bzip021Codec.Encode(data).Length, Is.LessThan(data.Length / 20));
  }

  [Test, Category("Exception")]
  public void Decode_Bzip2Magic_IsRejected()
    => Assert.Throws<InvalidDataException>(() => Bzip021Codec.Decode("BZh91AY&SY"u8));

  [Test, Category("Exception")]
  public void Decode_CorruptPayload_Throws() {
    var packed = Bzip021Codec.Encode(Text(5000));
    packed[packed.Length / 2] ^= 0x10;
    Assert.That(() => Bzip021Codec.Decode(packed), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void Decode_Truncated_Throws() {
    var packed = Bzip021Codec.Encode(Text(5000));
    Assert.Throws<InvalidDataException>(() => Bzip021Codec.Decode(packed.AsSpan(0, packed.Length - 4)));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsRegistered_AndRoundTrips() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    Assert.That(FormatRegistry.GetById("Bzip021"), Is.Not.Null);
    var ops = (IStreamFormatOperations)new Bzip021FormatDescriptor();
    using var packed = new MemoryStream();
    ops.Compress(new MemoryStream(Text(3000)), packed);
    packed.Position = 0;
    using var plain = new MemoryStream();
    ops.Decompress(packed, plain);
    Assert.That(plain.ToArray(), Is.EqualTo(Text(3000)));
  }
}

/// <summary>
/// bzip 0.21 itself, built from the FreeBSD distfile: it decompresses ours, we decompress
/// its output at every block size, and we decode the sample files shipped with it.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class Bzip021ReferenceInteropTests {
  private const string Sha256 = "85846f55406fa019305cee6f0aa3637769e857d8fff00eba26d8effc4539b014";
  private const string Dir = "/tmp/cwb-bzip021";
  private const string Tool = Dir + "/bzip-0.21/bzip";

  private static readonly Lazy<string?> BuildError = new(() => {
    if (!FsInteropToolbox.WslAvailable) return "WSL/Linux shell unavailable.";
    foreach (var tool in new[] { "gcc", "curl", "tar", "sha256sum" })
      if (!FsInteropToolbox.WslHasTool(tool)) return $"'{tool}' is required to build bzip 0.21.";
    var r = FsInteropToolbox.RunWsl(
      $"test -x {Tool} && exit 0; set -e; rm -rf {Dir}; mkdir -p {Dir}; cd {Dir}; " +
      "curl -sSfL -o bzip-0.21.tar.gz http://distcache.freebsd.org/ports-distfiles/bzip-0.21.tar.gz; " +
      $"echo '{Sha256}  bzip-0.21.tar.gz' | sha256sum -c -; tar xzf bzip-0.21.tar.gz; cd bzip-0.21; " +
      "gcc -O2 -w -o bzip bzip.c");
    return r.ExitCode == 0 ? null : $"building bzip 0.21 failed:\n{r.StdOut}\n{r.StdErr}";
  });

  private string _dir = null!;

  [SetUp]
  public void Setup() {
    if (BuildError.Value is { } error) Assert.Ignore(error);
    this._dir = Path.Combine(Path.GetTempPath(), $"cwb_bz021_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static IEnumerable<byte[]> Samples() {
    yield return Bzip021Tests.Text(150_000);
    var rnd = new byte[40_000];
    new Random(2).NextBytes(rnd);
    yield return rnd;
    yield return new byte[1000];
    yield return [9];
  }

  [Test]
  public void OurBz_IsDecompressedByBzip() {
    var n = 0;
    foreach (var data in Samples())
      foreach (var level in new[] { 1, 9 }) {
        var packed = Path.Combine(this._dir, $"ours{n}.bz");
        File.WriteAllBytes(packed, Bzip021Codec.Encode(data, level));
        var r = FsInteropToolbox.RunWsl($"{Tool} -d -c {FsInteropToolbox.WinToWsl(packed)} > {FsInteropToolbox.WinToWsl(packed)}.out");
        Assert.That(r.ExitCode, Is.EqualTo(0), $"bzip rejected sample {n} (level {level}):\n{r.StdErr}");
        Assert.That(File.ReadAllBytes(packed + ".out"), Is.EqualTo(data), $"sample {n}, level {level}");
        ++n;
      }
  }

  [Test]
  public void BzipOutput_IsDecodedByUs([Values(1, 5, 9)] int level) {
    var n = 0;
    foreach (var data in Samples()) {
      var input = Path.Combine(this._dir, $"theirs{n}.bin");
      File.WriteAllBytes(input, data);
      var r = FsInteropToolbox.RunWsl($"{Tool} -{level} -c {FsInteropToolbox.WinToWsl(input)} > {FsInteropToolbox.WinToWsl(input)}.bz");
      Assert.That(r.ExitCode, Is.EqualTo(0), r.StdErr);
      Assert.That(Bzip021Codec.Decode(File.ReadAllBytes(input + ".bz")), Is.EqualTo(data), $"sample {n}, level {level}");
      ++n;
    }
  }

  [TestCase("sample1")]
  [TestCase("sample2")]
  public void ShippedSampleFile_DecodesToItsReference(string name) {
    var packed = Path.Combine(this._dir, name + ".bz");
    var reference = Path.Combine(this._dir, name + ".ref");
    var r = FsInteropToolbox.RunWsl(
      $"cp {Dir}/bzip-0.21/{name}.bz {FsInteropToolbox.WinToWsl(packed)} && cp {Dir}/bzip-0.21/{name}.ref {FsInteropToolbox.WinToWsl(reference)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdErr);
    Assert.That(Bzip021Codec.Decode(File.ReadAllBytes(packed)), Is.EqualTo(File.ReadAllBytes(reference)));
  }
}
