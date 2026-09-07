using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Lzfse;

namespace Compression.Tests.Lzfse;

[TestFixture]
public class LzfseEntropyBlockTests {
  [TestCase(LzfseBlockMode.Auto)]
  [TestCase(LzfseBlockMode.Lzfse)]
  [TestCase(LzfseBlockMode.Lzvn)]
  [Category("RoundTrip")]
  public void RoundTrip_AllBlockModes(LzfseBlockMode mode) {
    var original = MakeCompressibleData(48 * 1024);

    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed, mode, LzfseCompressionLevel.Maximum, 16 * 1024);

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("Interoperability")]
  public void LzfseMode_EmitsEntropyCodedBvx2Block() {
    var original = MakeCompressibleData(20 * 1024);

    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed, LzfseBlockMode.Lzfse, LzfseCompressionLevel.Maximum, 20 * 1024);

    var encoded = compressed.ToArray();
    Assert.That(encoded.Length, Is.GreaterThan(8));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded), Is.EqualTo(0x32787662u), "Expected bvx2 entropy-coded LZFSE block");

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void MultiBlock_RoundTripsAcrossEntropyBlockBoundaries() {
    var original = MakeCompressibleData(95 * 1024);

    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed, LzfseBlockMode.Auto, LzfseCompressionLevel.Balanced, LzfseStream.DefaultBlockSize);

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Interoperability"), Category("Boundary")]
  public void Decompress_V1PartialFrequencyTables_MatchesAppleFseCheck() {
    const int headerSize = 772;
    const int literalPayloadSize = 7;
    const int lmdPayloadSize = 8;
    const int payloadSize = literalPayloadSize + lmdPayloadSize;
    var encoded = new byte[headerSize + payloadSize + 4];

    BinaryPrimitives.WriteUInt32LittleEndian(encoded, 0x31787662u); // bvx1
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(4), 1); // one raw byte
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(8), payloadSize);
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(12), 4); // literals are 4-way interleaved
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(16), 1); // one L/M/D record
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(20), literalPayloadSize);
    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(24), lmdPayloadSize);

    // Deliberately use only one state in each FSE table. Apple's fse_check_freq accepts
    // sum(freq) <= stateCount; state zero remains fully defined and the zero payload keeps
    // every transition on state zero.
    BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(54), 1);  // L symbol 1 => L=1, 1/64 states
    BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(92), 1);  // M symbol 0 => M=0, 1/64 states
    BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(132), 1); // D symbol 0 => D=0, 1/256 states
    BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(260), 1); // literal 0, 1/1024 states

    BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(headerSize + payloadSize), 0x24787662u); // bvx$

    using var input = new MemoryStream(encoded);
    using var output = new MemoryStream();
    LzfseStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 0 }));
  }

  [Test, Category("MalformedInput")]
  public void Decompress_V1FrequencyTableExceedsStateCount_Throws() {
    const int headerSize = 772;
    var encoded = new byte[headerSize];
    BinaryPrimitives.WriteUInt32LittleEndian(encoded, 0x31787662u);
    BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(52), 65); // L has only 64 states

    using var input = new MemoryStream(encoded);
    using var output = new MemoryStream();

    Assert.That(() => LzfseStream.Decompress(input, output), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("MalformedInput")]
  public void Decompress_TruncatedV2Header_Throws() {
    byte[] truncated = [0x62, 0x76, 0x78, 0x32, 0, 0, 0, 0];
    using var input = new MemoryStream(truncated);
    using var output = new MemoryStream();

    Assert.That(() => LzfseStream.Decompress(input, output), Throws.TypeOf<EndOfStreamException>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesOptimizerAxes() {
    var descriptor = new LzfseFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    Assert.That(descriptor.OptionsSchema.Select(option => option.Key), Is.EquivalentTo(new[] { "Mode", "Level", "BlockSize" }));

    var combinations = descriptor.OptionsSchema.Aggregate(1, (count, option) => count * option.AllowedValues!.Count);
    Assert.That(combinations, Is.EqualTo(36));
  }

  private static byte[] MakeCompressibleData(int length) {
    var result = new byte[length];
    ReadOnlySpan<byte> phrase = "LZFSE/tANS optimizer test payload :: 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ\n"u8;
    for (var offset = 0; offset < result.Length;) {
      var count = Math.Min(phrase.Length, result.Length - offset);
      phrase[..count].CopyTo(result.AsSpan(offset));
      offset += count;
    }
    return result;
  }
}
