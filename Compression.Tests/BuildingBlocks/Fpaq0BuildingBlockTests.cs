using System.Buffers.Binary;
using System.Text;
using Compression.Core.Entropy.Fpaq;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public sealed class Fpaq0BuildingBlockTests {
  private static readonly Fpaq0BuildingBlock Bb = new();

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
      Assert.That(compressed.Length, Is.LessThan(data.Length / 100));
      Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedPhrase_RoundTripsAndCompresses() {
    const string phrase = "the quick brown fox jumps over the lazy dog. ";
    var builder = new StringBuilder(20_000 + phrase.Length);
    while (builder.Length < 20_000)
      builder.Append(phrase);
    var data = Encoding.ASCII.GetBytes(builder.ToString(0, 20_000));

    var compressed = Bb.Compress(data);
    var roundTrip = Bb.Decompress(compressed);

    Assert.Multiple(() => {
      Assert.That(compressed.Length, Is.LessThan(data.Length * 3 / 4));
      Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var random = new Random(0xF0A0);
    var data = new byte[8192];
    random.NextBytes(data);

    var roundTrip = Bb.Decompress(Bb.Compress(data));

    Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrip() {
    var data = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    var roundTrip = Bb.Decompress(Bb.Compress(data));
    Assert.That(roundTrip, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void KnownVector_IsDeterministic() {
    var data = Encoding.ASCII.GetBytes("abababababababab");
    var compressed = Bb.Compress(data);

    Assert.That(compressed, Is.EqualTo(new byte[] {
      16, 0, 0, 0,
      0x61, 0x6F, 0x3D, 0x33, 0xCC, 0x9A, 0x80,
    }).AsCollection);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void MissingPayload_IsRejected() {
    byte[] malformed = [1, 0, 0, 0];
    Assert.That(() => Bb.Decompress(malformed), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void NegativeLength_IsRejected() {
    var malformed = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(malformed, -1);
    Assert.That(() => Bb.Decompress(malformed), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Fpaq0"));
      Assert.That(Bb.DisplayName, Is.EqualTo("FPAQ0"));
      Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Entropy));
    });
  }
}
