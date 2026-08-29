using System.Buffers.Binary;
using System.Text;
using Compression.Core.Entropy;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public sealed class BpeBuildingBlockTests {
  private static readonly BpeBuildingBlock Bb = new();
  private static readonly BpeBuildingBlock Exhaustive = new(BpeConstructionStrategy.Exhaustive);

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var roundTrip = Bb.Decompress(compressed);

    Assert.Multiple(() => {
      Assert.That(compressed, Is.EqualTo(new byte[] { 0, 0, 0, 0 }).AsCollection);
      Assert.That(roundTrip, Is.Empty);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedByte_RoundTripsAndCompresses() {
    var data = new byte[20_000];
    Array.Fill(data, (byte)'a');

    var compressed = Bb.Compress(data);
    var roundTrip = Bb.Decompress(compressed);

    Assert.Multiple(() => {
      Assert.That(compressed.Length, Is.LessThan(data.Length / 10));
      Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedPhrase_RoundTripsAndCompresses() {
    const string phrase = "the quick brown fox jumps over the lazy dog. ";
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(phrase, 512)));

    var compressed = Bb.Compress(data);
    var roundTrip = Bb.Decompress(compressed);

    Assert.Multiple(() => {
      Assert.That(compressed.Length, Is.LessThan(data.Length * 3 / 4));
      Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xB0E);
    var data = new byte[8192];
    rng.NextBytes(data);

    var roundTrip = Bb.Decompress(Bb.Compress(data));

    Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_FallsBackToRawBlock() {
    var data = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();

    var compressed = Bb.Compress(data);
    var roundTrip = Bb.Decompress(compressed);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(compressed), Is.EqualTo(256));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(compressed.AsSpan(4, 2)), Is.EqualTo(256));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(compressed.AsSpan(6, 2)), Is.EqualTo(256));
      Assert.That(compressed.AsSpan(8).ToArray(), Is.EqualTo(data).AsCollection);
      Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void MoreThanOneBlock_RoundTripsAcrossBoundary() {
    var data = new byte[ushort.MaxValue + 4096];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31 + i / 17);

    var roundTrip = Bb.Decompress(Bb.Compress(data));

    Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void KnownVector_UsesDeterministicPairOrder() {
    var data = Encoding.ASCII.GetBytes("abababababababab");

    var compressed = Bb.Compress(data);

    Assert.That(compressed, Is.EqualTo(new byte[] {
      16, 0, 0, 0,
      16, 0, 11, 0,
      2,
      0, (byte)'a', (byte)'b',
      1, 0, 0,
      1, 1, 1, 1,
    }).AsCollection);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void ExhaustiveConstruction_BeatsGreedyOnNonGreedyGrammar() {
    byte[] data = [0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1];

    var greedy = Bb.Compress(data);
    var exhaustive = Exhaustive.Compress(data);

    Assert.Multiple(() => {
      Assert.That(greedy.Length, Is.EqualTo(21));
      Assert.That(exhaustive.Length, Is.EqualTo(20));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(greedy.AsSpan(6, 2)), Is.EqualTo(13));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(exhaustive.AsSpan(6, 2)), Is.EqualTo(12));
      Assert.That(Bb.Decompress(exhaustive), Is.EqualTo(data).AsCollection);
      Assert.That(Exhaustive.Decompress(greedy), Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void ExhaustiveConstruction_RoundTripsAcrossSearchBlocks() {
    var data = new byte[130];
    Array.Fill(data, (byte)'a');

    var compressed = Exhaustive.Compress(data);

    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void MalformedForwardRule_IsRejected() {
    byte[] malformed = {
      4, 0, 0, 0,
      4, 0, 8, 0,
      2,
      0, 1, (byte)'a',
      1, (byte)'b', (byte)'c',
      0,
    };

    Assert.That(() => Bb.Decompress(malformed), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_BPE"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Byte Pair Encoding"));
      Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
      Assert.That(Bb.ConstructionStrategy, Is.EqualTo(BpeConstructionStrategy.Greedy));
      Assert.That(Exhaustive.ConstructionStrategy, Is.EqualTo(BpeConstructionStrategy.Exhaustive));
    });
  }
}
