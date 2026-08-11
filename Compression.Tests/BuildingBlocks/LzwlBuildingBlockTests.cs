using System.Text;
using Compression.Core.Dictionary.Lzwl;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzwlBuildingBlockTests {

  private static readonly LzwlBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x41];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[20480];
    Array.Fill(data, (byte)0x61);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 10));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog. ", 200)));
    Assert.That(Bb.Decompress(Bb.Compress(data)), Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// Every one of the sixteen digrams over the alphabet 0x70..0x73 occurs
  /// exactly twice here, so the frequency sort is asked to order candidates it
  /// finds equal for almost the whole table. The rule is that equal frequencies
  /// go in ascending digram value order, and the emitted table — 7070, 7071,
  /// 7072, 7073, 7170, ... — is what pins it. The wire format is shared
  /// byte-for-byte with the JavaScript implementation in the Cipher project.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void EqualDigramFrequencies_TableIsInAscendingDigramOrder() {
    var data = new List<byte>(64);
    for (var repeat = 0; repeat < 2; ++repeat)
      for (var first = 0; first < 4; ++first)
        for (var second = 0; second < 4; ++second) {
          data.Add((byte)(0x70 + first));
          data.Add((byte)(0x70 + second));
        }

    var compressed = Bb.Compress(data.ToArray());

    Assert.Multiple(() => {
      Assert.That(Convert.ToHexString(compressed), Is.EqualTo(
        "4000000010007070707170727073717071717172717372707271727272737371737273737370"
        + "804060503824160D0784426150B87C321B0E88C12110C80C16130D8141A15208F47220"));
      Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Lzwl"));
      Assert.That(Bb.DisplayName, Is.EqualTo("LZWL"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
