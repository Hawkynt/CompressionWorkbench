using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Rzip;

namespace Compression.Tests.Optimizer;

/// <summary>
/// RZIP's match threshold and hash-bucket search depth are encoder-only choices:
/// both can change the token parse without changing the wire grammar. The optimizer
/// therefore searches them per input and keeps the smallest complete stream.
/// </summary>
[TestFixture]
public class RzipOptionsTests {

  private static byte[] CompressAt(RzipFormatDescriptor descriptor, byte[] data, int minMatch, int candidates) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["MinMatch"] = minMatch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["CandidateSearchLimit"] = candidates.ToString(System.Globalization.CultureInfo.InvariantCulture),
      },
    });
    return output.ToArray();
  }

  private static byte[] CompressDefault(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    RzipStream.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(RzipFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  private static byte[] ShortRepeatSample() {
    using var output = new MemoryStream();
    ReadOnlySpan<byte> prefix = "RZIP-PREFIX!"u8; // 12 repeated bytes; record suffix stays unique.
    Span<byte> suffix = stackalloc byte[sizeof(int)];

    for (var i = 0; i < 256; ++i) {
      output.Write(prefix);
      BinaryPrimitives.WriteInt32LittleEndian(suffix, i);
      output.Write(suffix);
    }

    return output.ToArray();
  }

  private static byte[] CandidateDepthSample() {
    ReadOnlySpan<byte> prefix = "ABCDEFGH"u8;
    ReadOnlySpan<byte> continuation = "THIS-IS-A-LONG-UNIQUE-CONTINUATION-"u8;
    var target = new byte[prefix.Length + continuation.Length + 64];
    prefix.CopyTo(target);
    continuation.CopyTo(target.AsSpan(prefix.Length));
    for (var i = 0; i < 64; ++i)
      target[prefix.Length + continuation.Length + i] = (byte)i;

    using var output = new MemoryStream();
    output.Write(target);

    // Every decoy has the same 8-byte signature as target but disagrees immediately
    // afterwards. With a one-candidate search the old long match is hidden behind the
    // newest decoy; a 64-candidate search can still recover it.
    for (var i = 0; i < 40; ++i) {
      output.Write(prefix);
      var discriminator = (byte)(i + 1);
      if (discriminator == target[prefix.Length])
        discriminator = byte.MaxValue;
      output.WriteByte(discriminator);
      for (var j = 0; j < 23; ++j)
        output.WriteByte((byte)(i * 17 + j * 31));
    }

    output.Write(target);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesSearchableOptimizerAxes() {
    var descriptor = new RzipFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    Assert.That(descriptor.OptionsSchema.Select(option => option.Key),
      Is.EquivalentTo(new[] { "MinMatch", "CandidateSearchLimit" }));
    Assert.That(descriptor.OptionsSchema.All(option => option.AllowedValues is { Count: > 1 }), Is.True);
  }

  [Test, Category("Regression"), Category("RoundTrip")]
  public void ParameterizedDefaults_AreByteIdenticalToLegacyEntryPoint() {
    var descriptor = new RzipFormatDescriptor();
    var data = ShortRepeatSample();

    var legacy = CompressDefault(data);
    var parameterized = CompressAt(descriptor, data, minMatch: 16, candidates: 32);

    Assert.That(parameterized, Is.EqualTo(legacy));
    Assert.That(Decompress(descriptor, parameterized), Is.EqualTo(data));
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void MinMatchOption_ChangesParseAndCanRecoverShortRepeats() {
    var descriptor = new RzipFormatDescriptor();
    var data = ShortRepeatSample();

    var fine = CompressAt(descriptor, data, minMatch: 8, candidates: 32);
    var coarse = CompressAt(descriptor, data, minMatch: 64, candidates: 32);

    Assert.That(fine.Length, Is.LessThan(coarse.Length));
    Assert.That(Decompress(descriptor, fine), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, coarse), Is.EqualTo(data));
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void CandidateSearchLimit_ChangesHowFarBackHashCollisionsAreSearched() {
    var descriptor = new RzipFormatDescriptor();
    var data = CandidateDepthSample();

    var shallow = CompressAt(descriptor, data, minMatch: 8, candidates: 1);
    var deep = CompressAt(descriptor, data, minMatch: 8, candidates: 64);

    Assert.That(deep.Length, Is.LessThan(shallow.Length));
    Assert.That(Decompress(descriptor, shallow), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, deep), Is.EqualTo(data));
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void Optimizer_ExhaustivelyFindsSmallestDeclaredCombination() {
    var descriptor = new RzipFormatDescriptor();
    var data = ShortRepeatSample().Concat(CandidateDepthSample()).ToArray();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);
    var minMatches = descriptor.OptionsSchema.Single(option => option.Key == "MinMatch").AllowedValues!;
    var candidateLimits = descriptor.OptionsSchema.Single(option => option.Key == "CandidateSearchLimit").AllowedValues!;

    Assert.That(result.Parameters.Keys, Is.EquivalentTo(new[] { "MinMatch", "CandidateSearchLimit" }));
    Assert.That(result.Probes, Is.EqualTo(minMatches.Count * candidateLimits.Count));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));

    foreach (var minMatch in minMatches)
      foreach (var candidateLimit in candidateLimits) {
        var single = CompressAt(
          descriptor,
          data,
          int.Parse(minMatch, System.Globalization.CultureInfo.InvariantCulture),
          int.Parse(candidateLimit, System.Globalization.CultureInfo.InvariantCulture));
        Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(single.Length),
          $"optimizer must beat MinMatch={minMatch}, CandidateSearchLimit={candidateLimit}");
      }
  }
}
