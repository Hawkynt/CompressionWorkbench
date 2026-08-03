using System.Text;
using Compression.Core.Dictionary.Dna;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class DnaBuildingBlockTests {

  private static readonly DnaBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    foreach (byte b in new byte[] { (byte)'A', (byte)'C', (byte)'G', (byte)'T', (byte)'N', 0xFF }) {
      var round = Bb.Decompress(Bb.Compress([b]));
      Assert.That(round, Is.EqualTo(new[] { b }).AsCollection);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Repetitive_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(new string('A', 500));
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xC5);
    var data = new byte[2048];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog.");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void PureAcgtSequence_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("ACGTACGTACGTGGCCAATTACGTACGTGATTACA");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void PureAcgtSequence_CompressesToUnderAQuarter() {
    var bases = "ACGT"u8;
    var data = new byte[4000];
    for (var i = 0; i < data.Length; i++)
      data[i] = bases[i % 4];

    var compressed = Bb.Compress(data);
    // 2-bit packing of 4000 pure-ACGT bytes should land well under a 1:1 ratio.
    Assert.That(compressed.Length, Is.LessThan(data.Length / 3));

    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void NonAcgtBytes_RoundTrip() {
    // FASTA header, lowercase bases, ambiguity code N, and arbitrary binary bytes.
    var text = Encoding.ASCII.GetBytes(">seq1 example\nACGTNacgtNXYZ");
    var data = new byte[text.Length + 2];
    text.CopyTo(data, 0);
    data[^2] = 0x00;
    data[^1] = 0xFF;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void AllNonAcgtBytes_RoundTrip() {
    var data = Encoding.ASCII.GetBytes("hello world, not dna at all!");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Dna"));
      Assert.That(Bb.DisplayName, Is.EqualTo("DNA Sequence Compression"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
