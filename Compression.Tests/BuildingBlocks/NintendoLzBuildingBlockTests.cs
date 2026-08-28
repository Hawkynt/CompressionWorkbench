using System.Text;
using Compression.Core.Dictionary.Nintendo;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public sealed class NintendoLzBuildingBlockTests {
  private static readonly Yaz0BuildingBlock Yaz0 = new();
  private static readonly Yay0BuildingBlock Yay0 = new();

  [TestCaseSource(nameof(Blocks))]
  [Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips(IBuildingBlock block) {
    var compressed = block.Compress([]);
    var roundTrip = block.Decompress(compressed);
    Assert.That(roundTrip, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void Yaz0_LiteralOnlyVector_IsStable() {
    var compressed = Yaz0.Compress("ABC"u8);
    Assert.That(compressed, Is.EqualTo(new byte[] {
      (byte)'Y', (byte)'a', (byte)'z', (byte)'0',
      0, 0, 0, 3,
      0, 0, 0, 0, 0, 0, 0, 0,
      0xE0, (byte)'A', (byte)'B', (byte)'C',
    }).AsCollection);
    Assert.That(Yaz0.Decompress(compressed), Is.EqualTo("ABC"u8.ToArray()).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Yay0_LiteralOnlyVector_IsStable() {
    var compressed = Yay0.Compress("ABC"u8);
    Assert.That(compressed, Is.EqualTo(new byte[] {
      (byte)'Y', (byte)'a', (byte)'y', (byte)'0',
      0, 0, 0, 3,
      0, 0, 0, 20,
      0, 0, 0, 20,
      0xE0, 0, 0, 0,
      (byte)'A', (byte)'B', (byte)'C',
    }).AsCollection);
    Assert.That(Yay0.Decompress(compressed), Is.EqualTo("ABC"u8.ToArray()).AsCollection);
  }

  [TestCaseSource(nameof(Blocks))]
  [Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedByte_RoundTripsAndCompresses(IBuildingBlock block) {
    var data = new byte[20_000];
    Array.Fill(data, (byte)'A');
    var compressed = block.Compress(data);
    var roundTrip = block.Decompress(compressed);

    Assert.Multiple(() => {
      Assert.That(compressed.Length, Is.LessThan(data.Length / 10));
      Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
    });
  }

  [TestCaseSource(nameof(Blocks))]
  [Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedPhrase_RoundTripsAndCompresses(IBuildingBlock block) {
    const string phrase = "the quick brown fox jumps over the lazy dog. ";
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(phrase, 512)));
    var compressed = block.Compress(data);
    var roundTrip = block.Decompress(compressed);

    Assert.Multiple(() => {
      Assert.That(compressed.Length, Is.LessThan(data.Length * 3 / 4));
      Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
    });
  }

  [TestCaseSource(nameof(Blocks))]
  [Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips(IBuildingBlock block) {
    var random = new Random(0x59A0);
    var data = new byte[8192];
    random.NextBytes(data);
    Assert.That(block.Decompress(block.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [TestCaseSource(nameof(Blocks))]
  [Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrip(IBuildingBlock block) {
    var data = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    Assert.That(block.Decompress(block.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Yaz0_InvalidBackwardReference_IsRejected() {
    byte[] malformed = {
      (byte)'Y', (byte)'a', (byte)'z', (byte)'0',
      0, 0, 0, 3,
      0, 0, 0, 0, 0, 0, 0, 0,
      0x00, 0x10, 0x00,
    };
    Assert.That(() => Yaz0.Decompress(malformed), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Yay0_InvalidOffsets_AreRejected() {
    byte[] malformed = {
      (byte)'Y', (byte)'a', (byte)'y', (byte)'0',
      0, 0, 0, 1,
      0, 0, 0, 12,
      0, 0, 0, 16,
    };
    Assert.That(() => Yay0.Decompress(malformed), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Yaz0.Id, Is.EqualTo("BB_Yaz0"));
      Assert.That(Yaz0.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
      Assert.That(Yay0.Id, Is.EqualTo("BB_Yay0"));
      Assert.That(Yay0.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
    });
  }

  private static IEnumerable<IBuildingBlock> Blocks() {
    yield return Yaz0;
    yield return Yay0;
  }
}
