using Compression.Core.Dictionary.Lzs;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzsLevelTests {
  [Test, Category("Spec")]
  public void BuildingBlock_LengthFive_UsesRfc2395Code() {
    var codec = new LzsBuildingBlock();

    // RFC 2395 grammar for AAAAAA:
    // literal A | offset 1, length 5 | end marker, then zero padding.
    var compressed = codec.Compress("AAAAAA"u8);

    Assert.That(compressed, Is.EqualTo(new byte[] {
      0x06, 0x00, 0x00, 0x00,
      0x20, 0xE0, 0x73, 0x00,
    }));
    Assert.That(codec.Decompress(compressed), Is.EqualTo("AAAAAA"u8.ToArray()));
  }

  [Test, Category("Spec")]
  public void BuildingBlock_InvalidBackReference_IsRejected() {
    // Declared output size 1, followed immediately by a short offset 1 while
    // the history is still empty. Padding after the offset is irrelevant.
    byte[] malformed = [0x01, 0x00, 0x00, 0x00, 0xC0, 0x80];

    Assert.That(
      () => new LzsBuildingBlock().Decompress(malformed),
      Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void AllLevels_RoundTrip() {
    var codec = new LzsBuildingBlock();
    var data = CompressibleSample();

    foreach (var level in Enum.GetValues<LzsCompressionLevel>())
      Assert.That(codec.Decompress(codec.Compress(data, level)), Is.EqualTo(data), level.ToString());
  }

  [Test, Category("Compression")]
  public void MaximumLevel_FindsSmallerLazyParse() {
    var codec = new LzsBuildingBlock();
    var data = LazyImprovementSample();

    var balanced = codec.Compress(data, LzsCompressionLevel.Balanced);
    var maximum = codec.Compress(data, LzsCompressionLevel.Maximum);

    Assert.That(maximum.Length, Is.LessThan(balanced.Length));
    Assert.That(codec.Decompress(maximum), Is.EqualTo(data));
  }

  private static byte[] LazyImprovementSample() => [
    3, 4, 3, 5, 0, 4, 1, 2, 4, 0, 0, 3, 0, 1, 2, 0,
    5, 2, 5, 5, 4, 4, 0, 5, 5, 5, 4, 4, 0, 5, 0, 0,
    2, 0, 3, 5, 1, 3, 4, 2, 2, 4, 1, 4, 5, 5, 3, 3,
    4, 4, 3, 3, 1, 5, 2, 1, 5, 1, 4, 3, 3, 2, 5, 2,
  ];

  private static byte[] CompressibleSample() {
    var data = new byte[8192];
    var pattern = "LZS optimizer: RFC 2395 sliding-window parsing with repeated phrases and local variations. "u8;
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];

    // Break selected repetitions so the match finder sees both short and long alternatives.
    for (var i = 257; i < data.Length; i += 521)
      data[i] ^= (byte)(i >> 4);
    return data;
  }
}
