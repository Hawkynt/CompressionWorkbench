using Compression.Core.FileSystems.Cvf;

namespace Compression.Tests.Cvf;

/// <summary>
/// Boundary behaviour of every CVF cluster codec (<c>docs/CVF-CODECS-ON-DISK.md</c>): empty and one-byte
/// clusters, the three cluster sizes the volumes use, incompressible data, and the longest match length
/// and farthest distance each grammar can express — one past each must still round-trip, by falling back
/// to a shorter form.
/// </summary>
[TestFixture]
public sealed class CvfCodecBoundaryTests {

  private static readonly CvfLzMethod[] AllMethods = [CvfLzMethod.Ds, CvfLzMethod.Jm, CvfLzMethod.Sq, CvfLzMethod.Sd3, CvfLzMethod.Sd4];

  private static bool IsStacker(CvfLzMethod method) => method is CvfLzMethod.Sd3 or CvfLzMethod.Sd4;

  private static byte[] RoundTrip(byte[] data, CvfLzMethod method, int level = 2) {
    var payload = CvfLzCodec.Encode(data, method, level)!;
    return IsStacker(method)
      ? CvfLzCodec.DecompressStacker(payload, payload.Length, data.Length)
      : CvfLzCodec.Decompress(payload, payload.Length, data.Length);
  }

  private static byte[] Random(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  [Test, Category("Codec")]
  public void GivenAnEmptyCluster_WhenEncodedAndDecoded_ThenNothingComesBack([ValueSource(nameof(AllMethods))] CvfLzMethod method)
    => Assert.That(RoundTrip([], method), Is.Empty);

  [Test, Category("Codec")]
  public void GivenAOneByteCluster_WhenEncodedAndDecoded_ThenTheByteComesBack(
      [ValueSource(nameof(AllMethods))] CvfLzMethod method, [Values(0x00, 0x7F, 0x80, 0xFF)] int value)
    => Assert.That(RoundTrip([(byte)value], method), Is.EqualTo(new[] { (byte)value }));

  [Test, Category("Codec")]
  public void GivenAClusterOfEachVolumeSize_WhenEncodedAndDecoded_ThenItIsByteExact(
      [ValueSource(nameof(AllMethods))] CvfLzMethod method, [Values(512, 8192, 32768)] int size,
      [Values("text", "zeros", "random")] string shape) {
    var data = shape switch {
      "zeros" => new byte[size],
      "random" => Random(size, size),
      _ => CvfSamples.Get("text-32k")[..size],
    };

    Assert.That(RoundTrip(data, method), Is.EqualTo(data));
  }

  [Test, Category("Codec")]
  public void GivenIncompressibleData_WhenCompressed_ThenTheCallerIsToldToStoreIt([Values(CvfLzMethod.Ds, CvfLzMethod.Jm, CvfLzMethod.Sq, CvfLzMethod.Sd4)] CvfLzMethod method)
    => Assert.That(CvfLzCodec.Compress(Random(8192, 99), method, 2), Is.Null);

  [Test, Category("Codec")]
  public void GivenCompressibleData_WhenCompressed_ThenThePayloadIsSmaller([ValueSource(nameof(AllMethods))] CvfLzMethod method) {
    var data = CvfSamples.Get("text-8k");

    Assert.That(CvfLzCodec.Compress(data, method, 2)!.Length, Is.LessThan(data.Length / 2));
  }

  // DS copies at most 512 bytes per match, JM 513; SD-4's long matches extend at 21, 36, 291 and 4386.
  [Test, Category("Codec")]
  public void GivenARunAroundEachMaximumMatchLength_WhenEncodedAndDecoded_ThenItIsByteExact(
      [ValueSource(nameof(AllMethods))] CvfLzMethod method,
      [Values(2, 3, 5, 6, 20, 21, 22, 35, 36, 37, 290, 291, 292, 511, 512, 513, 514, 1025, 4385, 4386, 4387, 8191)] int runLength) {
    var data = Random(8192 + 64, runLength);
    data.AsSpan(17, runLength).Fill(data[16]);   // a literal then a run: one long distance-1 match

    Assert.That(RoundTrip(data, method), Is.EqualTo(data));
  }

  // DS/JM reach 4414 (4415 is the sync marker); SD-4 short matches 2047, long ones 2720; SD-3 2047.
  [Test, Category("Codec")]
  public void GivenARepeatAtEachDistanceBoundary_WhenEncodedAndDecoded_ThenItIsByteExact(
      [ValueSource(nameof(AllMethods))] CvfLzMethod method,
      [Values(1, 63, 64, 319, 320, 2047, 2048, 2720, 2721, 4414, 4415, 4416)] int distance,
      [Values(3, 5, 6, 40)] int length) {
    var data = Random(distance + length + 32, distance * 7 + length);
    data.AsSpan(8, length).CopyTo(data.AsSpan(8 + distance));

    Assert.That(RoundTrip(data, method), Is.EqualTo(data));
  }

  [Test, Category("Codec")]
  public void GivenADsOrJmPayload_WhenEncoded_ThenItCarriesTheMethodHeaderAndAWholeNumberOfWords(
      [Values(CvfLzMethod.Ds, CvfLzMethod.Jm, CvfLzMethod.Sq)] CvfLzMethod method) {
    var payload = CvfLzCodec.Encode(CvfSamples.Get("x86-8k"), method, 1)!;
    var expected = method switch { CvfLzMethod.Ds => "DS\0\0"u8.ToArray(), CvfLzMethod.Jm => "JM\0\0"u8.ToArray(), _ => "SQ\0\0"u8.ToArray() };

    Assert.Multiple(() => {
      Assert.That(payload[..4], Is.EqualTo(expected));
      Assert.That(payload.Length % 2, Is.Zero);
    });
  }

  [Test, Category("Codec")]
  public void GivenAutoMethod_WhenEncoded_ThenTheSmallerOfDsAndJmIsKept() {
    var data = CvfSamples.Get("table-8k");
    var ds = CvfLzCodec.Encode(data, CvfLzMethod.Ds, 2)!;
    var jm = CvfLzCodec.Encode(data, CvfLzMethod.Jm, 2)!;

    Assert.That(CvfLzCodec.Encode(data, CvfLzMethod.Auto, 2)!.Length, Is.EqualTo(Math.Min(ds.Length, jm.Length)));
  }

  [Test, Category("Codec")]
  public void GivenStoredMethod_WhenEncoded_ThenThereIsNoPayload()
    => Assert.That(CvfLzCodec.Encode(CvfSamples.Get("text-8k"), CvfLzMethod.Stored, 2), Is.Null);

  [Test, Category("Codec")]
  public void GivenAPayloadPaddedToWholeSectors_WhenDecoded_ThenThePaddingIsIgnored(
      [Values(CvfLzMethod.Ds, CvfLzMethod.Jm, CvfLzMethod.Sq)] CvfLzMethod method) {
    var data = CvfSamples.Get("dir-8k");
    var payload = CvfLzCodec.Encode(data, method, 2)!;
    var padded = new byte[(payload.Length + 511) / 512 * 512];
    payload.CopyTo(padded, 0);

    Assert.That(CvfLzCodec.Decompress(padded, padded.Length, data.Length), Is.EqualTo(data));
  }

  // A file's last cluster may hold fewer sectors than the cluster: its stream closes with the sync and
  // the rest of the cluster reads as zero.
  [Test, Category("Codec")]
  public void GivenAStreamShorterThanTheCluster_WhenDecoded_ThenTheTailIsZero([Values(CvfLzMethod.Ds, CvfLzMethod.Jm)] CvfLzMethod method) {
    var data = CvfSamples.Get("text-8k")[..1536];
    var payload = CvfLzCodec.Encode(data, method, 2)!;

    var cluster = CvfLzCodec.Decompress(payload, payload.Length, 8192);

    Assert.Multiple(() => {
      Assert.That(cluster[..1536], Is.EqualTo(data));
      Assert.That(cluster[1536..], Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("Codec")]
  public void GivenATruncatedStream_WhenDecoded_ThenItIsRejected([Values(CvfLzMethod.Ds, CvfLzMethod.Jm)] CvfLzMethod method) {
    var payload = CvfLzCodec.Encode(CvfSamples.Get("text-8k"), method, 2)!;

    Assert.That(() => CvfLzCodec.Decompress(payload[..(payload.Length / 2)], payload.Length / 2, 8192), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void GivenAnUnknownMethodHeader_WhenDecoded_ThenItIsRejected()
    => Assert.That(() => CvfLzCodec.Decompress("XY\0\0\0\0\0\0"u8.ToArray(), 8, 512), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Codec")]
  public void GivenAPayloadShorterThanItsHeader_WhenDecoded_ThenItIsRejected()
    => Assert.That(() => CvfLzCodec.Decompress([0x44, 0x53], 2, 512), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Codec")]
  public void GivenAMatchBeforeTheClusterStart_WhenDecoded_ThenItIsRejected() {
    // DS: "0 0" + distance 5 (LSB-first) then a length code — with nothing decoded yet.
    var writer = new LsbBitWriter();
    foreach (var b in "DS\0\0"u8) writer.WriteByte(b);
    writer.Write(0b00, 2); writer.Write(5, 6); writer.Write(1, 1);
    var payload = writer.ToArray(2);

    Assert.That(() => CvfLzCodec.Decompress(payload, payload.Length, 512), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void GivenALengthCodeWithNineLeadingZeros_WhenDecoded_ThenItIsRejected() {
    var writer = new LsbBitWriter();
    foreach (var b in "JM\0\0"u8) writer.WriteByte(b);
    writer.Write(0x41, 8);                       // literal 'A' ("0" + 7 bits)
    writer.Write(0b01, 2); writer.Write(0, 1); writer.Write(1, 6);   // match, distance 1
    writer.Write(0, 9); writer.Write(1, 1);       // nine zeros: invalid
    var payload = writer.ToArray(2);

    Assert.That(() => CvfLzCodec.Decompress(payload, payload.Length, 512), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void GivenDifferentLevels_WhenEncoded_ThenEveryLevelRoundTripsAndHigherIsNoLarger(
      [Values(CvfLzMethod.Ds, CvfLzMethod.Jm, CvfLzMethod.Sd4)] CvfLzMethod method) {
    var data = CvfSamples.Get("x86-8k");
    var sizes = new List<int>();
    foreach (var level in new[] { 1, 2, 3, 4 }) {
      Assert.That(RoundTrip(data, method, level), Is.EqualTo(data), $"level {level}");
      sizes.Add(CvfLzCodec.Encode(data, method, level)!.Length);
    }

    Assert.That(sizes[^1], Is.LessThanOrEqualTo(sizes[0]));
  }
}
