using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Lizard;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Verifies that all four Lizard method families participate in managed
/// compression and in the generic optimizer search.
/// </summary>
[TestFixture]
public class LizardOptionsTests {
  private const int FrameHeaderSize = 15;

  private static IEnumerable<string> SupportedLevels() =>
    Enumerable.Range(10, 40).Select(static value => value.ToString());

  private static byte[] CompressibleSample() {
    var random = new Random(7319);
    var phrases = new[] {
      "lizard formerly lz5 ",
      "compression workbench ",
      "five stream block format ",
      "fast lz codewords ",
    };

    using var output = new MemoryStream();
    for (var i = 0; i < 250; ++i) {
      var phrase = Encoding.ASCII.GetBytes(phrases[random.Next(phrases.Length)]);
      output.Write(phrase);
      output.WriteByte((byte)('A' + i % 7));
    }
    return output.ToArray();
  }

  private static byte[] CompressAt(LizardFormatDescriptor descriptor, byte[] data, string level, string blockSize = "4 MB") {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["Level"] = level,
        ["BlockSize"] = blockSize,
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(LizardFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [TestCaseSource(nameof(SupportedLevels))]
  [Category("Spec")]
  public void EverySupportedLevel_IsEncodedAndRoundTrips(string level) {
    var descriptor = new LizardFormatDescriptor();
    var data = CompressibleSample();
    var compressed = CompressAt(descriptor, data, level);

    Assert.That(ReadOuterBlockSize(compressed) & 0x80000000u, Is.Zero,
      "compressible sample should exercise a compressed Lizard frame block");
    Assert.That(ReadPayloadLevel(compressed), Is.EqualTo((byte)int.Parse(level)));
    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void FastLzFamily_UsesEmbeddedOffsetsAndReferenceMinimumOffset() {
    var descriptor = new LizardFormatDescriptor();
    var data = Enumerable.Repeat((byte)'A', 4096).ToArray();
    var compressed = CompressAt(descriptor, data, "10");
    var streams = ReadRawStreams(compressed);

    Assert.Multiple(() => {
      Assert.That(streams.Header, Is.Zero);
      Assert.That(streams.Lengths, Is.Empty);
      Assert.That(streams.Offset16, Is.Empty, "fastLZ4 offsets live in the literal stream");
      Assert.That(streams.Offset24, Is.Empty);
      Assert.That(streams.Tokens, Is.Not.Empty);
      Assert.That(ReadFirstFastMatchOffset(streams), Is.EqualTo(8),
        "reference fast-LZ parsers require offsets >= 8 because the decoder copies matches in 8-byte chunks");
    });
    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void LizV1Family_UsesSeparate16BitOffsetStream() {
    var descriptor = new LizardFormatDescriptor();
    var data = CompressibleSample();
    var compressed = CompressAt(descriptor, data, "20");
    var streams = ReadRawStreams(compressed);

    Assert.Multiple(() => {
      Assert.That(streams.Header, Is.Zero);
      Assert.That(streams.Lengths, Is.Empty);
      Assert.That(streams.Offset16.Length, Is.GreaterThanOrEqualTo(2));
      Assert.That(streams.Offset16.Length % 2, Is.Zero);
      Assert.That(streams.Tokens, Is.Not.Empty);
    });
    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void LizV1Family_Uses24BitOffsetsBeyond64KiB() {
    var descriptor = new LizardFormatDescriptor();
    var data = LongOffsetSample();
    var compressed = CompressAt(descriptor, data, "29", "128 KB");
    var streams = ReadRawStreams(compressed);

    Assert.That(streams.Offset24.Length, Is.GreaterThanOrEqualTo(3),
      "LIZv1 must move matches at distances >= 64 KiB to the 24-bit offset stream");

    var longOffsets = Enumerable.Range(0, streams.Offset24.Length / 3)
      .Select(index => ReadUInt24(streams.Offset24, index * 3))
      .ToArray();
    Assert.That(longOffsets, Has.Some.GreaterThanOrEqualTo(1 << 16));
    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void HuffmanFamily_EntropyCodesTokenOrLiteralStream() {
    var descriptor = new LizardFormatDescriptor();
    var cycle = DeBruijnSequence(12, 4);
    var data = new byte[cycle.Length * 2 + 16];
    cycle.CopyTo(data, 0);
    cycle.CopyTo(data, cycle.Length);
    Array.Fill(data, (byte)0x0B, cycle.Length * 2, 16);

    var compressed = CompressAt(descriptor, data, "30", "128 KB");
    var header = ReadInternalBlockHeader(compressed);

    Assert.That(header & 0x03, Is.Not.Zero,
      "level 30 must consider HUF for the token/literal streams and this vector makes it profitable");
    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void HuffmanCodec_UsesFseCompressedWeightTableWhenDirectWeightsCannotFit() {
    using var source = new MemoryStream();
    for (var repetition = 0; repetition < 64; ++repetition)
      for (var symbol = 0; symbol < 128; ++symbol)
        source.WriteByte((byte)symbol);
    for (var repetition = 0; repetition < 4; ++repetition)
      for (var symbol = 128; symbol < 256; ++symbol)
        source.WriteByte((byte)symbol);

    var data = source.ToArray();
    var compressed = LizardHuffman.TryCompress(data);

    Assert.That(compressed, Is.Not.Null, "skewed full-byte alphabet should be HUF-compressible");
    Assert.That(compressed![0], Is.InRange((byte)1, (byte)127),
      "HUF table headers below 128 are standard FSE-compressed weight tables");
    Assert.That(LizardHuffman.Decompress(compressed, data.Length), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Schema_ExposesAllReferenceLevels() {
    var descriptor = new LizardFormatDescriptor();
    var level = descriptor.OptionsSchema.Single(option => option.Key == "Level");

    Assert.Multiple(() => {
      Assert.That(level.Default, Is.EqualTo("17"));
      Assert.That(level.AllowedValues,
        Is.EqualTo(Enumerable.Range(10, 40).Select(static value => value.ToString()).ToArray()));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    });
  }

  [Test, Category("Spec")]
  public void CompressOptimal_UsesLevel49() {
    var descriptor = new LizardFormatDescriptor();
    var data = CompressibleSample();
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();

    descriptor.CompressOptimal(input, output);
    var compressed = output.ToArray();

    Assert.That(ReadPayloadLevel(compressed), Is.EqualTo(49));
    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Optimizer_SearchesAllLevelsAndBlockSizes_AndResultRoundTrips() {
    var descriptor = new LizardFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("Level"));
      Assert.That(result.Parameters, Does.ContainKey("BlockSize"));
      Assert.That(result.Probes, Is.EqualTo(160), "40 levels x 4 block sizes must be searched exhaustively");
      Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
      Assert.That(result.Ratio, Is.LessThan(1.0));
    });
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));

    foreach (var (level, blockSize) in new[] {
      ("10", "128 KB"),
      ("17", "4 MB"),
      ("29", "128 KB"),
      ("39", "4 MB"),
      ("49", "128 KB"),
      ("49", "4 MB"),
    }) {
      var candidate = CompressAt(descriptor, data, level, blockSize);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.LongLength),
        $"optimizer result must be no larger than level {level}, block size {blockSize}");
    }
  }

  private static byte[] LongOffsetSample() {
    var random = new Random(842771);
    var marker = new byte[4096];
    var filler = new byte[66_000];
    var tail = new byte[32];
    random.NextBytes(marker);
    random.NextBytes(filler);
    random.NextBytes(tail);

    var result = new byte[marker.Length + filler.Length + marker.Length + tail.Length];
    marker.CopyTo(result, 0);
    filler.CopyTo(result, marker.Length);
    marker.CopyTo(result, marker.Length + filler.Length);
    tail.CopyTo(result, result.Length - tail.Length);
    return result;
  }

  private static byte[] DeBruijnSequence(int alphabetSize, int order) {
    var work = new int[alphabetSize * order];
    var result = new List<byte>((int)Math.Pow(alphabetSize, order));

    void Generate(int t, int p) {
      if (t > order) {
        if (order % p == 0)
          for (var i = 1; i <= p; ++i)
            result.Add((byte)work[i]);
        return;
      }

      work[t] = work[t - p];
      Generate(t + 1, p);
      for (var value = work[t - p] + 1; value < alphabetSize; ++value) {
        work[t] = value;
        Generate(t + 1, t);
      }
    }

    Generate(1, 1);
    return result.ToArray();
  }

  private static RawStreams ReadRawStreams(byte[] compressed) {
    Assert.That(ReadOuterBlockSize(compressed) & 0x80000000u, Is.Zero,
      "test vector must produce a compressed frame block");

    var position = FrameHeaderSize + 4;
    ++position; // compression level
    var header = compressed[position++];
    Assert.That(header & 0x1F, Is.Zero, "raw-stream parser is only for non-HUF internal blocks");

    var lengths = ReadRawStream(compressed, ref position);
    var offset16 = ReadRawStream(compressed, ref position);
    var offset24 = ReadRawStream(compressed, ref position);
    var tokens = ReadRawStream(compressed, ref position);
    var literals = ReadRawStream(compressed, ref position);
    return new(header, lengths, offset16, offset24, tokens, literals);
  }

  private static byte[] ReadRawStream(byte[] data, ref int position) {
    var length = ReadUInt24(data, position);
    position += 3;
    var result = data.AsSpan(position, length).ToArray();
    position += length;
    return result;
  }

  private static ushort ReadFirstFastMatchOffset(RawStreams streams) {
    var token = streams.Tokens[0];
    var position = 0;
    var literalLength = token & 0x0F;
    Assert.That(literalLength, Is.LessThan(15), "minimum-offset vector should not need a literal-length extension");
    position += literalLength;
    return BinaryPrimitives.ReadUInt16LittleEndian(streams.Literals.AsSpan(position, 2));
  }

  private static uint ReadOuterBlockSize(byte[] compressed) =>
    BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(FrameHeaderSize, 4));

  private static byte ReadPayloadLevel(byte[] compressed) => compressed[FrameHeaderSize + 4];

  private static byte ReadInternalBlockHeader(byte[] compressed) {
    Assert.That(ReadOuterBlockSize(compressed) & 0x80000000u, Is.Zero,
      "test vector must produce a compressed frame block");
    return compressed[FrameHeaderSize + 5];
  }

  private static int ReadUInt24(byte[] data, int offset) =>
    data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;

  private sealed record RawStreams(
    byte Header,
    byte[] Lengths,
    byte[] Offset16,
    byte[] Offset24,
    byte[] Tokens,
    byte[] Literals);
}
