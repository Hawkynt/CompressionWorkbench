using Compression.Core.Entropy;
using Compression.Core.Entropy.Fse;

namespace Compression.Tests.Entropy;

[TestFixture]
public class FseTansBuildingBlockTests {
  [TestCase(8)]
  [TestCase(10)]
  [Category("RoundTrip")]
  public void FseByteCodec_RoundTripsTableSizes(int tableLog) {
    var original = MakeSkewedData(8192);
    var compressed = FseByteCodec.Compress(original, tableLog);
    var decompressed = FseByteCodec.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void FseBuildingBlock_RoundTrips() {
    var block = new FseBuildingBlock();
    var original = MakeSkewedData(4096);
    Assert.That(block.Decompress(block.Compress(original)), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void TansBuildingBlock_RoundTrips() {
    var block = new TansBuildingBlock();
    var original = MakeSkewedData(4096);
    Assert.That(block.Decompress(block.Compress(original)), Is.EqualTo(original));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void TansBuildingBlock_AllByteValues_RoundTrips() {
    var block = new TansBuildingBlock();
    var original = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    Assert.That(block.Decompress(block.Compress(original)), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void FseAndTans_EmptyInput_RoundTrips() {
    Assert.Multiple(() => {
      Assert.That(new FseBuildingBlock().Decompress(new FseBuildingBlock().Compress([])), Is.Empty);
      Assert.That(new TansBuildingBlock().Decompress(new TansBuildingBlock().Compress([])), Is.Empty);
    });
  }

  [Test, Category("MalformedInput")]
  public void FseByteCodec_RejectsInvalidNormalizedCounts() {
    byte[] malformed = [
      1, 0, 0, 0, // raw length
      8,           // tableLog
      0, 0,        // max symbol
      1, 0,        // normalized frequency: 1 instead of 256
      1,            // dummy entropy payload/sentinel
    ];

    Assert.That(() => FseByteCodec.Decompress(malformed), Throws.TypeOf<InvalidDataException>());
  }

  private static byte[] MakeSkewedData(int length) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = i % 17 switch {
        < 10 => (byte)'A',
        < 14 => (byte)'B',
        14 => (byte)'C',
        _ => (byte)(i & 0xFF),
      };
    return result;
  }
}
