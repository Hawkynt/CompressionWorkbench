using Compression.Lib;
using Compression.Registry;
using FileFormat.Rnc;

namespace Compression.Tests.Rnc;

[TestFixture]
public class RncTests {
  private static byte[] Compress(byte[] data, RncCompressionOptions? options = null) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    if (options is null)
      RncStream.Compress(input, output);
    else
      RncStream.Compress(input, output, options);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    RncStream.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, RNC!"u8.ToArray();
    Assert.That(Decompress(Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    Assert.That(Decompress(Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = Enumerable.Range(0, 512).Select(i => (byte)(i % 16)).ToArray();
    Assert.That(Decompress(Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Empty() {
    Assert.That(Decompress(Compress([])), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    byte[] data = [0x42];
    Assert.That(Decompress(Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_256KB_Random() {
    var rng = new Random(42);
    var data = new byte[256 * 1024];
    rng.NextBytes(data);
    Assert.That(Decompress(Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("Compatibility")]
  public void Decompress_Method1KnownVector() {
    // Independent Method-1 vector: 512 bytes 00..0F repeated, using canonical
    // category Huffman codes and an overlapping LZ match. The wire shape follows
    // ProPack's LSB-first 16-bit bit words and raw-byte interleaving.
    var packed = Convert.FromHexString(
      "524E43010000020000000021D22132B40001980000880200100A0000000042002038000102030405060708090A0B0C0D0E0F77");
    var expected = Enumerable.Range(0, 512).Select(i => (byte)(i % 16)).ToArray();
    Assert.That(Decompress(packed), Is.EqualTo(expected));
  }

  [Test, Category("Compatibility")]
  public void Crc16_StandardCheckValue() {
    Assert.That(RncStream.Crc16("123456789"u8), Is.EqualTo(0xBB3D));
    Assert.That(RncStream.Crc16([]), Is.Zero);
  }

  [Test, Category("HappyPath")]
  public void Header_HasCorrectMagicSizesAndCrcs() {
    var data = Enumerable.Range(0, 100).Select(i => (byte)(i * 37)).ToArray();
    var packed = Compress(data);

    Assert.That(packed.AsSpan(0, 4).ToArray(), Is.EqualTo("RNC\x01"u8.ToArray()));
    Assert.That(ReadBE32(packed, 4), Is.EqualTo((uint)data.Length));
    Assert.That(ReadBE32(packed, 8), Is.EqualTo((uint)(packed.Length - 18)));
    Assert.That(ReadBE16(packed, 12), Is.EqualTo(RncStream.Crc16(data)));
    Assert.That(ReadBE16(packed, 14), Is.EqualTo(RncStream.Crc16(packed.AsSpan(18))));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesOptimizationSurface() {
    var descriptor = new RncFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    Assert.That(descriptor.OptionsSchema.Select(option => option.Key), Is.EquivalentTo([
      "DictionarySize", "BlockSize", "SearchDepth", "Parser",
    ]));
  }

  [Test, Category("HappyPath"), Category("Optimization")]
  public void Optimizer_SearchesRncSettings_AndKeepsBestValidResult() {
    var data = Enumerable.Range(0, 4096)
      .Select(i => (byte)((i % 257) < 224 ? i % 32 : (i * 73) & 0xFF))
      .ToArray();
    var descriptor = new RncFormatDescriptor();
    var baseline = Compress(data);

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Probes, Is.GreaterThan(1));
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(baseline.Length));
      Assert.That(result.Parameters.Keys, Does.Contain("DictionarySize"));
      Assert.That(result.Parameters.Keys, Does.Contain("BlockSize"));
      Assert.That(result.Parameters.Keys, Does.Contain("SearchDepth"));
      Assert.That(result.Parameters.Keys, Does.Contain("Parser"));
      Assert.That(Decompress(result.Bytes), Is.EqualTo(data));
    });
  }

  [Test, Category("Negative")]
  public void Decompress_RejectsCorruptPackedCrc() {
    var packed = Compress("CRC me"u8.ToArray());
    packed[^1] ^= 0x80;
    Assert.That(() => Decompress(packed), Throws.TypeOf<InvalidDataException>());
  }

  private static uint ReadBE32(ReadOnlySpan<byte> data, int offset)
    => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

  private static ushort ReadBE16(ReadOnlySpan<byte> data, int offset)
    => (ushort)((data[offset] << 8) | data[offset + 1]);
}
