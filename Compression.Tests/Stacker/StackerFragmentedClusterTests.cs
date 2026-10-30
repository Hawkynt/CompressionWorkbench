using Compression.Core.FileSystems.Cvf;
using Compression.Tests.Cvf;
using Compression.Tests.Support;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Stacker fragmented clusters (<c>docs/CVF-CODECS-ON-DISK.md</c>, "Stacker fragmented clusters"): a
/// compressed cluster split over the AMAP run and further pieces named by a list in the run's first
/// sector. The layout was established by building volumes and seeing which ones the dmsdos driver reads
/// back; the DriverProof cases here keep that comparison, and our reader must read the same volumes.
/// </summary>
[TestFixture]
public sealed class StackerFragmentedClusterTests {
  private const int Ss = 512;
  private const int AmapEntryOfCluster2 = (2 + 3) * Ss + 2 * 3;   // AMAP area 0 (FAT start 2), cluster 2

  /// <summary>How to damage an otherwise valid piece list.</summary>
  internal enum Damage { None, WrongTotal, FourthByteSet, NoSignature, PiecePastEnd }

  /// <summary>
  /// A one-file volume whose single cluster is stored fragmented: a first run of
  /// <paramref name="firstRun"/> sectors holding the list, then pieces of the given sector counts (the
  /// last takes whatever is left of the payload).
  /// </summary>
  internal static byte[] Volume(byte[] cluster, byte[] payload, int firstRun, int[] middlePieces, Damage damage = Damage.None) {
    var writer = new GenuineStackerWriter { CompressionMethod = CvfLzMethod.Stored };
    writer.AddFile("F.BIN", cluster);
    var image = writer.Build();
    var run = image[AmapEntryOfCluster2] | image[AmapEntryOfCluster2 + 1] << 8;
    Array.Clear(image, run * Ss, 16 * Ss);

    var pieceCount = middlePieces.Length + 1;
    var headerBytes = 4 + 4 * pieceCount;
    var inFirst = firstRun * Ss - headerBytes;
    var rest = payload.Length - inFirst - middlePieces.Sum() * Ss;
    Assume.That(rest, Is.GreaterThan(0), "the last piece must hold part of the payload");
    int[] counts = [.. middlePieces, (rest + Ss - 1) / Ss];

    var h = run * Ss;
    image[h] = damage == Damage.NoSignature ? (byte)0xEC : (byte)0xED;
    image[h + 1] = (byte)pieceCount;
    var total = counts.Sum() - 1 + (damage == Damage.WrongTotal ? 1 : 0);
    image[h + 2] = (byte)total;
    image[h + 3] = (byte)(total >> 8);

    payload.AsSpan(0, inFirst).CopyTo(image.AsSpan(h + headerBytes));
    var at = inFirst;
    var sector = run + 20;
    for (var i = 0; i < counts.Length; ++i, sector += 10) {
      var e = h + 4 + 4 * i;
      var named = damage == Damage.PiecePastEnd && i == counts.Length - 1 ? image.Length / Ss + 5 : sector;
      image[e] = (byte)named;
      image[e + 1] = (byte)(named >> 8);
      image[e + 2] = (byte)(counts[i] - 1);
      image[e + 3] = damage == Damage.FourthByteSet ? (byte)1 : (byte)0;
      var take = Math.Min(counts[i] * Ss, payload.Length - at);
      payload.AsSpan(at, take).CopyTo(image.AsSpan(sector * Ss));
      at += take;
    }
    image[AmapEntryOfCluster2 + 2] = (byte)(0x80 | (firstRun - 1));   // bit 7: fragmented; run shorter than the cluster
    return image;
  }

  private static byte[] Payload(string codec, byte[] data) => codec == "sd3" ? Sd3Codec.Encode(data) : Sd4Codec.Encode(data);

  private static IEnumerable<TestCaseData> Shapes() {
    foreach (var codec in new[] { "sd3", "sd4" }) {
      yield return new TestCaseData(codec, 2, Array.Empty<int>()).SetName($"{{m}}({codec}, two pieces)");
      yield return new TestCaseData(codec, 2, new[] { 2 }).SetName($"{{m}}({codec}, three pieces)");
      yield return new TestCaseData(codec, 1, new[] { 1, 1 }).SetName($"{{m}}({codec}, four pieces, one-sector first run)");
    }
  }

  private static byte[] Read(byte[] image) {
    using var reader = new GenuineStackerReader(new MemoryStream(image));
    return reader.Extract(reader.Entries.Single(e => e.Name == "F.BIN"));
  }

  [Test, Category("Codec"), TestCaseSource(nameof(Shapes))]
  public void GivenAFragmentedCluster_WhenRead_ThenItIsByteExact(string codec, int firstRun, int[] middle) {
    var data = CvfSamples.Get("text-8k");

    Assert.That(Read(Volume(data, Payload(codec, data), firstRun, middle)), Is.EqualTo(data));
  }

  [Test, Category("DriverProof"), TestCaseSource(nameof(Shapes))]
  public void GivenAFragmentedCluster_WhenReadByDmsdos_ThenItIsByteExact(string codec, int firstRun, int[] middle) {
    DmsdosOracle.Require();
    var data = CvfSamples.Get("text-8k");
    var path = Path.Combine(Path.GetTempPath(), $"cwb-stac-frag-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(path, Volume(data, Payload(codec, data), firstRun, middle));
      var (exit, bytes) = DmsdosOracle.ReadFile(path, "F.BIN");

      Assert.That(exit, Is.Zero, "dcread failed");
      Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(data.Length));
      Assert.That(bytes[..data.Length], Is.EqualTo(data));
    } finally {
      File.Delete(path);
    }
  }

  // dmsdos refuses a list whose total disagrees with its entries ("sector count mismatch"); so do we.
  [Test, Category("DriverProof")]
  public void GivenAWrongPieceTotal_WhenReadByDmsdos_ThenItIsRefusedAsByUs() {
    DmsdosOracle.Require();
    var data = CvfSamples.Get("text-8k");
    var image = Volume(data, Sd3Codec.Encode(data), 2, [2], Damage.WrongTotal);
    var path = Path.Combine(Path.GetTempPath(), $"cwb-stac-frag-{Guid.NewGuid():N}.cvf");
    try {
      File.WriteAllBytes(path, image);
      var (exit, bytes) = DmsdosOracle.ReadFile(path, "F.BIN");

      Assert.That(exit != 0 || bytes.Length < data.Length || !bytes[..data.Length].SequenceEqual(data), Is.True,
        "dmsdos read a cluster whose piece total is wrong");
      Assert.That(() => Read(image), Throws.InstanceOf<InvalidDataException>());
    } finally {
      File.Delete(path);
    }
  }

  [Test, Category("Codec")]
  public void GivenAWrongPieceTotal_WhenRead_ThenItIsRejected() {
    var data = CvfSamples.Get("text-8k");

    Assert.That(() => Read(Volume(data, Sd3Codec.Encode(data), 2, [2], Damage.WrongTotal)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void GivenAPieceEntryWithItsFourthByteSet_WhenRead_ThenItIsRefusedNotGuessed() {
    var data = CvfSamples.Get("text-8k");

    Assert.That(() => Read(Volume(data, Sd3Codec.Encode(data), 2, [2], Damage.FourthByteSet)), Throws.InstanceOf<NotSupportedException>());
  }

  [Test, Category("Codec")]
  public void GivenAFragmentedRunWithoutItsSignature_WhenRead_ThenItIsRejected() {
    var data = CvfSamples.Get("text-8k");

    Assert.That(() => Read(Volume(data, Sd3Codec.Encode(data), 2, [2], Damage.NoSignature)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void GivenAPiecePastTheEndOfTheVolume_WhenRead_ThenItIsRejected() {
    var data = CvfSamples.Get("text-8k");

    Assert.That(() => Read(Volume(data, Sd3Codec.Encode(data), 2, [2], Damage.PiecePastEnd)), Throws.InstanceOf<InvalidDataException>());
  }
}
