using System.Text;
using Compression.Core.FileSystems.Cvf;
using Compression.Tests.Cvf;
using Compression.Tests.Support;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Stacker cluster codecs against the dmsdos driver as a black box (Linux natively, Windows through
/// WSL): volumes our writer builds must read back byte-exact through <c>dcread</c> and through our own
/// reader. Covers the SD-4 encoder with back-references and the whole-cluster ForceCompress case.
/// </summary>
[TestFixture]
public sealed class GenuineStackerCodecOracleTests {
  private const int ClusterBytes = 8192;

  private string _dir = null!;

  [SetUp]
  public void SetUp() => Directory.CreateDirectory(this._dir = Path.Combine(Path.GetTempPath(), $"cwb-stac-oracle-{Guid.NewGuid():N}"));

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { /* best effort */ }
  }

  private static byte[] Build(CvfLzMethod method, bool force, params (string Name, byte[] Data)[] files) {
    var writer = new GenuineStackerWriter { CompressionMethod = method, CompressionLevel = 3, ForceCompress = force };
    foreach (var (n, d) in files) writer.AddFile(n, d);
    return writer.Build();
  }

  private static void AssertOurReaderReads(byte[] image, (string Name, byte[] Data)[] files) {
    using var reader = new GenuineStackerReader(new MemoryStream(image));
    foreach (var (name, expected) in files)
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == name)), Is.EqualTo(expected), $"our reader: {name}");
  }

  private void AssertDmsdosReads(byte[] image, (string Name, byte[] Data)[] files) {
    var path = Path.Combine(this._dir, "stac.cvf");
    File.WriteAllBytes(path, image);
    Assert.That(DmsdosOracle.Detect(path), Does.Contain("stacker"), "dmsdos does not detect the volume");
    foreach (var (name, expected) in files) {
      var (exit, data) = DmsdosOracle.ReadFile(path, name);
      Assert.That(exit, Is.Zero, $"dcread failed for {name}");
      Assert.That(data.Length, Is.GreaterThanOrEqualTo(expected.Length), $"dcread returned too few bytes for {name}");
      Assert.That(data[..expected.Length], Is.EqualTo(expected), $"dmsdos: {name}");
    }
  }

  /// <summary>A cluster whose SD-3 payload needs all 16 sectors: 15 sectors &lt; payload ≤ 16 sectors.</summary>
  internal static byte[] WholeClusterPayloadData() {
    var random = new byte[ClusterBytes];
    new Random(4711).NextBytes(random);
    int lo = 0, hi = ClusterBytes;                 // number of random bytes; the rest repeats one byte
    while (lo < hi) {
      var mid = (lo + hi) / 2;
      var size = Sd3Codec.Encode(Mix(random, mid)).Length;
      if (size <= 15 * 512) lo = mid + 1; else hi = mid;
    }
    var data = Mix(random, lo);
    var sectors = (Sd3Codec.Encode(data).Length + 511) / 512;
    Assume.That(sectors, Is.EqualTo(16), "could not construct a whole-cluster SD-3 payload");
    return data;

    static byte[] Mix(byte[] random, int count) {
      var d = new byte[ClusterBytes];
      random.AsSpan(0, count).CopyTo(d);
      return d;
    }
  }

  [Test, Category("Codec")]
  public void GivenForceCompress_WhenACompressedClusterNeedsAllSectors_ThenItIsStoredAndReadsBackExact() {
    var data = WholeClusterPayloadData();
    (string, byte[])[] files = [("FULL.BIN", data)];

    var image = Build(CvfLzMethod.Sd3, force: true, files);

    AssertOurReaderReads(image, files);
  }

  [Test, Category("DriverProof")]
  public void GivenForceCompress_WhenACompressedClusterNeedsAllSectors_ThenDmsdosReadsItBackExact() {
    DmsdosOracle.Require();
    (string, byte[])[] files = [("FULL.BIN", WholeClusterPayloadData())];

    AssertDmsdosReads(Build(CvfLzMethod.Sd3, force: true, files), files);
  }

  private static (string, byte[])[] CodecSamples() {
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("SD-4 encoder verification line with some repetition.\r\n", 600)));
    var runs = new byte[3 * ClusterBytes];
    for (var i = 0; i < runs.Length; ++i) runs[i] = (byte)(i / 5000);   // long matches: every extension
    return [
      ("TEXT.TXT", text),
      ("RUNS.BIN", runs),
      ("SAMPTEXT.TXT", CvfSamples.Get("text-8k")),
      ("SAMPX86.BIN", CvfSamples.Get("x86-8k")),
      ("SAMPTAB.BIN", CvfSamples.Get("table-8k")),
      ("SAMPALPH.BIN", CvfSamples.Get("alphabet-8k")),
      ("SAMPDIR.BIN", CvfSamples.Get("dir-8k")),
      ("ONE.BIN", [0x5A]),
    ];
  }

  [Test, Category("DriverProof")]
  public void GivenSd4ClustersWithBackReferences_WhenReadByDmsdos_ThenEveryFileIsByteExact([Values(CvfLzMethod.Sd4, CvfLzMethod.Auto)] CvfLzMethod method) {
    DmsdosOracle.Require();
    var files = CodecSamples();
    var image = Build(method, force: false, files);

    AssertDmsdosReads(image, files);
    AssertOurReaderReads(image, files);
  }

  /// <summary>
  /// One cluster whose byte counts grow just faster than Fibonacci (1, 1, 3, 5, 9, 15, … 1973, each
  /// the sum of the two before plus one, so no tie lets a code stay shallow), the rest one more value:
  /// an optimal code over them is 16 bits deep, so a literals-only SD-4 stream needs the full 15-bit
  /// limit.
  /// </summary>
  internal static byte[] SkewedCluster() {
    var data = new List<byte>(ClusterBytes);
    int a = 1, b = 1;
    for (var symbol = 0; symbol < 16; ++symbol, (a, b) = (b, a + b + 1))
      data.AddRange(Enumerable.Repeat((byte)(symbol * 13), a));
    data.AddRange(Enumerable.Repeat((byte)250, ClusterBytes - data.Count));
    var array = data.ToArray();
    new Random(1).Shuffle(array);
    return array;
  }

  // The 5-bit "last" field after the signature word is the longest main-code length.
  private static int LongestMainCode(byte[] payload) => (payload[2] | payload[3] << 8) >> 8 & 0x1F;

  [Test, Category("Codec")]
  public void GivenSkewedLiterals_WhenSd4Encoded_ThenTheMainCodeUsesTheFull15BitsAndRoundTrips() {
    var data = SkewedCluster();
    var payload = Sd4Codec.Encode(data, level: 2, matches: false);

    Assert.Multiple(() => {
      Assert.That(LongestMainCode(payload), Is.EqualTo(15));
      Assert.That(Sd4Codec.Decode(payload, payload.Length, data.Length), Is.EqualTo(data));
    });
  }

  // A stored volume whose one cluster is then rewritten as a compressed run holding the given payload.
  private static byte[] VolumeWithClusterPayload(byte[] cluster, byte[] payload) {
    var image = Build(CvfLzMethod.Stored, force: false, ("SKEW.BIN", cluster));
    const int amapEntry = (2 + 3) * 512 + 2 * 3;     // AMAP area 0 for cluster 2 (FAT start 2)
    var sector = image[amapEntry] | image[amapEntry + 1] << 8;
    var sectors = (payload.Length + 511) / 512;
    Assume.That(sectors, Is.LessThan(16), "payload must be shorter than the cluster");
    Array.Clear(image, sector * 512, 16 * 512);
    payload.CopyTo(image, sector * 512);
    image[amapEntry + 2] = (byte)(sectors - 1);        // flags 0: compressed
    return image;
  }

  [Test, Category("Codec")]
  public void GivenAPatchedSd4Cluster_WhenReadByOurReader_ThenItIsByteExact() {
    var data = SkewedCluster();
    (string, byte[])[] files = [("SKEW.BIN", data)];

    AssertOurReaderReads(VolumeWithClusterPayload(data, Sd4Codec.Encode(data, 2, matches: false)), files);
  }

  [Test, Category("DriverProof")]
  public void GivenSd4CodesOf15Bits_WhenReadByDmsdos_ThenTheClusterIsByteExact() {
    DmsdosOracle.Require();
    var data = SkewedCluster();
    (string, byte[])[] files = [("SKEW.BIN", data)];

    AssertDmsdosReads(VolumeWithClusterPayload(data, Sd4Codec.Encode(data, 2, matches: false)), files);
  }

  [Test, Category("Codec")]
  public void GivenSd4Clusters_WhenEncoded_ThenTheyAreWithinTenPercentOfTheDmsdosEncoder(
      [Values("text-8k", "x86-8k", "table-8k", "alphabet-8k", "dir-8k")] string sample) {
    var ours = Sd4Codec.Encode(CvfSamples.Get(sample), level: 3).Length;
    var theirs = CvfReferenceVectorTests.Load($"{sample}.sd4").Length;

    Assert.That(ours, Is.LessThanOrEqualTo(theirs * 11 / 10), $"ours {ours} B, dmsdos {theirs} B");
  }
}
