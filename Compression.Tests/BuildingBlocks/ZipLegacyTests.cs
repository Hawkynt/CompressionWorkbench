using System.Text;
using Compression.Core.Dictionary.Zip;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Round-trip coverage for the legacy PKWARE ZIP building blocks
/// (Shrink/Reduce/Implode) exposed via <see cref="IBuildingBlock"/>.
/// </summary>
[TestFixture]
public class ZipLegacyTests {

  private static IEnumerable<IBuildingBlock> Blocks() {
    yield return new ShrinkBuildingBlock();
    yield return new ReduceBuildingBlock();
    yield return new ImplodeBuildingBlock();
  }

  private static byte[] RoundTrip(IBuildingBlock bb, byte[] data) {
    var compressed = bb.Compress(data);
    return bb.Decompress(compressed);
  }

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    Assert.That(RoundTrip(bb, []), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    var data = new byte[] { 0x42 };
    Assert.That(RoundTrip(bb, data), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void ShortText_RoundTrips([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    var data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog.");
    Assert.That(RoundTrip(bb, data), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatingPattern_RoundTrips([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 16);
    Assert.That(RoundTrip(bb, data), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void LongRun_OfSameByte_RoundTrips([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    var data = new byte[4096];
    Array.Fill<byte>(data, 0xAA);
    var compressed = bb.Compress(data);
    Assert.That(bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    // A 4 KB constant run must compress on every legacy method.
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void TextCorpus_RoundTrips([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    var sb = new StringBuilder();
    for (var i = 0; i < 200; i++) sb.Append("compression workbench shrink reduce implode ").Append(i).Append('\n');
    var data = Encoding.ASCII.GetBytes(sb.ToString());
    Assert.That(RoundTrip(bb, data), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("RoundTrip")]
  public void RandomData_RoundTrips([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    var rng = new Random(unchecked((int)0xB16B00B5));
    var data = new byte[6000];
    rng.NextBytes(data);
    Assert.That(RoundTrip(bb, data), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Decompress_TooSmallHeader_Throws([ValueSource(nameof(Blocks))] IBuildingBlock bb) {
    Assert.That(() => bb.Decompress([0x01]), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(new ShrinkBuildingBlock().Id, Is.EqualTo("BB_Shrink"));
      Assert.That(new ReduceBuildingBlock().Id, Is.EqualTo("BB_Reduce"));
      Assert.That(new ImplodeBuildingBlock().Id, Is.EqualTo("BB_Implode"));
      foreach (var bb in Blocks())
        Assert.That(bb.Family, Is.EqualTo(AlgorithmFamily.Dictionary), bb.Id);
    });
  }

  [Test, Category("EdgeCase")]
  public void Registry_Enumerates_AllThree() {
    // The source generator scans IBuildingBlock implementors; ensure the new
    // blocks are discoverable by their IDs.
    Assert.Multiple(() => {
      foreach (var id in new[] { "BB_Shrink", "BB_Reduce", "BB_Implode" })
        Assert.That(BuildingBlockRegistry.GetById(id), Is.Not.Null, id);
    });
  }
}
