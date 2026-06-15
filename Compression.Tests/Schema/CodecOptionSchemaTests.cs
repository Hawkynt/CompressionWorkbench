#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Schema;

/// <summary>
/// Verifies that the per-codec option schemas (<see cref="IFormatOptionsSchema"/>)
/// expose only axes the writer genuinely honors. For each enriched axis we assert:
/// (a) the schema lists it, (b) two distinct values yield DIFFERENT compressed
/// output, (c) both values round-trip losslessly, and (d) the schema default
/// reproduces today's no-options output byte-for-byte. A coverage test reports how
/// many stream codecs publish a schema.
/// </summary>
[TestFixture]
public class CodecOptionSchemaTests {

  // General-purpose payload: > 64 KiB and structured, so the LZ4 block-size axis
  // spans multiple blocks at 64 KiB but a single block at 4 MiB, the checksum axes
  // change the framing, and everything round-trips.
  private static byte[] SamplePayload() {
    var data = new byte[200_000];
    var rng = new Random(1234);
    var phrase = new byte[64];
    rng.NextBytes(phrase);
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(phrase[i % phrase.Length] + (i % 211 == 0 ? (i / 211) & 0x0F : 0));
    return data;
  }

  // Payload with many *competing* match candidates: variable-length runs of
  // low-entropy random bytes. The encoder's match-finder depth (its compression
  // level) genuinely changes which matches are chosen here, so a level axis can be
  // proved to alter output. (For pure repeats or random data, Fast/Normal/Best all
  // emit identical bytes — the only kind of input where a level axis is observable.)
  private static byte[] LevelSensitivePayload() {
    var rng = new Random(7);
    var data = new byte[150_000];
    var p = 0;
    while (p < data.Length) {
      var run = rng.Next(2, 18);
      var value = (byte)rng.Next(64);
      for (var k = 0; k < run && p < data.Length; ++k)
        data[p++] = value;
    }
    return data;
  }

  private static FormatCreateOptions Opts(params (string Key, string Value)[] kv)
    => new() { FormatSpecific = kv.ToDictionary(t => t.Key, t => t.Value) };

  private static byte[] Compress(IStreamFormatOperations ops, byte[] input, FormatCreateOptions options) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    ops.Compress(inMs, outMs, options);
    return outMs.ToArray();
  }

  private static byte[] CompressDefault(IStreamFormatOperations ops, byte[] input) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    ops.Compress(inMs, outMs);
    return outMs.ToArray();
  }

  private static byte[] Decompress(IStreamFormatOperations ops, byte[] compressed) {
    using var inMs = new MemoryStream(compressed, writable: false);
    using var outMs = new MemoryStream();
    ops.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  /// <summary>Asserts an axis is listed, two values differ in output, both round-trip,
  /// and the schema-default value reproduces the no-options output byte-for-byte.</summary>
  private static void AssertHonoredAxis(
      IFormatDescriptor descriptor, string axisKey, string valueA, string valueB, byte[]? payload = null) {
    var ops = (IStreamFormatOperations)descriptor;
    var schema = (IFormatOptionsSchema)descriptor;
    var input = payload ?? SamplePayload();

    var axis = schema.OptionsSchema.FirstOrDefault(o => o.Key == axisKey);
    Assert.That(axis, Is.Not.Null, $"{descriptor.Id} schema must list axis '{axisKey}'.");

    var outA = Compress(ops, input, Opts((axisKey, valueA)));
    var outB = Compress(ops, input, Opts((axisKey, valueB)));
    Assert.That(outA, Is.Not.EqualTo(outB),
      $"{descriptor.Id}: axis '{axisKey}' values '{valueA}' vs '{valueB}' must change output.");

    Assert.That(Decompress(ops, outA), Is.EqualTo(input),
      $"{descriptor.Id}: '{axisKey}={valueA}' must round-trip losslessly.");
    Assert.That(Decompress(ops, outB), Is.EqualTo(input),
      $"{descriptor.Id}: '{axisKey}={valueB}' must round-trip losslessly.");

    // Default value of this axis must reproduce the no-options output.
    var atDefault = Compress(ops, input, Opts((axisKey, axis!.Default)));
    Assert.That(atDefault, Is.EqualTo(CompressDefault(ops, input)),
      $"{descriptor.Id}: axis '{axisKey}' at its schema default must equal the no-options output.");
  }

  // ── LZ4 ─────────────────────────────────────────────────────────────────

  [Test]
  public void Lz4_LevelAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Lz4.Lz4FormatDescriptor(), "Level", "Fast", "Hc", LevelSensitivePayload());

  [Test]
  public void Lz4_BlockSizeAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Lz4.Lz4FormatDescriptor(), "BlockSize", "64 KB", "4 MB");

  [Test]
  public void Lz4_ContentChecksumAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Lz4.Lz4FormatDescriptor(), "ContentChecksum", "true", "false");

  [Test]
  public void Lz4_BlockChecksumAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Lz4.Lz4FormatDescriptor(), "BlockChecksum", "false", "true");

  [Test]
  public void Lz4_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Lz4.Lz4FormatDescriptor();
    var input = SamplePayload();
    var atDefaults = Compress(d, input, Opts(
      ("Level", "Fast"), ("BlockSize", "4 MB"), ("ContentChecksum", "true"), ("BlockChecksum", "false")));
    Assert.That(atDefaults, Is.EqualTo(CompressDefault(d, input)));
  }

  // ── XZ ──────────────────────────────────────────────────────────────────

  [Test]
  public void Xz_LevelAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Xz.XzFormatDescriptor(), "Level", "Fast", "Best", LevelSensitivePayload());

  [Test]
  public void Xz_DictionarySizeAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Xz.XzFormatDescriptor(), "DictionarySize", "64 KB", "8 MB");

  [Test]
  public void Xz_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Xz.XzFormatDescriptor();
    var input = SamplePayload();
    var atDefaults = Compress(d, input, Opts(("Level", "Normal"), ("DictionarySize", "8 MB")));
    Assert.That(atDefaults, Is.EqualTo(CompressDefault(d, input)));
  }

  // ── Lzip ────────────────────────────────────────────────────────────────

  [Test]
  public void Lzip_LevelAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Lzip.LzipFormatDescriptor(), "Level", "Fast", "Best", LevelSensitivePayload());

  [Test]
  public void Lzip_DictionarySizeAxis_HonoredAndRoundTrips()
    => AssertHonoredAxis(new FileFormat.Lzip.LzipFormatDescriptor(), "DictionarySize", "64 KB", "8 MB");

  [Test]
  public void Lzip_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Lzip.LzipFormatDescriptor();
    var input = SamplePayload();
    var atDefaults = Compress(d, input, Opts(("Level", "Normal"), ("DictionarySize", "8 MB")));
    Assert.That(atDefaults, Is.EqualTo(CompressDefault(d, input)));
  }

  // ── Existing single-axis codecs: defaults must remain byte-identical ──────

  [Test]
  public void Lzma_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Lzma.LzmaFormatDescriptor();
    var input = SamplePayload();
    var atDefaults = Compress(d, input, Opts(
      ("Level", "Normal"), ("DictionarySize", "8 MB"), ("Lc", "3"), ("Lp", "0"), ("Pb", "2")));
    Assert.That(atDefaults, Is.EqualTo(CompressDefault(d, input)));
  }

  [Test]
  public void Zstd_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Zstd.ZstdFormatDescriptor();
    var input = SamplePayload();
    Assert.That(Compress(d, input, Opts(("Level", "3"))), Is.EqualTo(CompressDefault(d, input)));
  }

  [Test]
  public void Brotli_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Brotli.BrotliFormatDescriptor();
    var input = SamplePayload();
    Assert.That(Compress(d, input, Opts(("Quality", "Default"))), Is.EqualTo(CompressDefault(d, input)));
  }

  [Test]
  public void Gzip_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Gzip.GzipFormatDescriptor();
    var input = SamplePayload();
    Assert.That(Compress(d, input, Opts(("Level", "Default"))), Is.EqualTo(CompressDefault(d, input)));
  }

  [Test]
  public void Zlib_DefaultOptions_MatchNoOptionsOutput() {
    var d = new FileFormat.Zlib.ZlibFormatDescriptor();
    var input = SamplePayload();
    Assert.That(Compress(d, input, Opts(("Level", "Default"))), Is.EqualTo(CompressDefault(d, input)));
  }

  // ── Optimizer integration: multi-axis schemas widen the real search ───────

  [Test]
  public void Optimizer_Lz4_ExploresMultipleAxesAndRoundTrips() {
    var d = new FileFormat.Lz4.Lz4FormatDescriptor();
    var input = SamplePayload();
    var result = CompressionOptimizer.OptimizeStream(input, d, d,
      new CompressionOptimizer.OptimizerOptions { Effort = CompressionOptimizer.Effort.Max });
    // 2 levels × 4 block sizes × 2 content-checksum × 2 block-checksum = 32 combos.
    Assert.That(result.Probes, Is.GreaterThan(1),
      "Lz4 multi-axis schema must give the optimizer more than one combination to probe.");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(input),
      "Optimizer's winning Lz4 output must round-trip.");
  }

  [Test]
  public void Optimizer_Xz_ExploresMultipleAxesAndRoundTrips() {
    var d = new FileFormat.Xz.XzFormatDescriptor();
    var input = SamplePayload();
    var result = CompressionOptimizer.OptimizeStream(input, d, d,
      new CompressionOptimizer.OptimizerOptions { Effort = CompressionOptimizer.Effort.Max });
    Assert.That(result.Probes, Is.GreaterThan(1),
      "Xz multi-axis schema must give the optimizer more than one combination to probe.");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(input),
      "Optimizer's winning Xz output must round-trip.");
  }

  // ── Coverage (informational) ──────────────────────────────────────────────

  [Test]
  public void Coverage_StreamCodecsWithSchema_IsReported() {
    // Snapshot of the stream codecs that now publish a tunable option schema.
    IFormatDescriptor[] schemaCarriers = [
      new FileFormat.Lzma.LzmaFormatDescriptor(),
      new FileFormat.Xz.XzFormatDescriptor(),
      new FileFormat.Lzip.LzipFormatDescriptor(),
      new FileFormat.Lz4.Lz4FormatDescriptor(),
      new FileFormat.Zstd.ZstdFormatDescriptor(),
      new FileFormat.Brotli.BrotliFormatDescriptor(),
      new FileFormat.Gzip.GzipFormatDescriptor(),
      new FileFormat.Zlib.ZlibFormatDescriptor(),
    ];

    var totalAxes = 0;
    foreach (var d in schemaCarriers) {
      Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>(), $"{d.Id} must publish a schema.");
      var schema = (IFormatOptionsSchema)d;
      Assert.That(schema.OptionsSchema, Is.Not.Empty, $"{d.Id} schema must list at least one axis.");
      totalAxes += schema.OptionsSchema.Count;
    }

    TestContext.Out.WriteLine($"Stream codecs with a schema: {schemaCarriers.Length}; total declared axes: {totalAxes}.");
    Assert.That(totalAxes, Is.GreaterThanOrEqualTo(schemaCarriers.Length));
  }
}
