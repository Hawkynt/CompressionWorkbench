#pragma warning disable CS1591

using BenchmarkDotNet.Attributes;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Benchmarks;

/// <summary>
/// Benchmarks each compression building block (algorithm primitive) on 1 MB of
/// compressible (repeated English text) and incompressible (random) data.
/// </summary>
[Config(typeof(InProcessConfig))]
public class BuildingBlockBenchmarks {

  private static readonly string[] BlockIds = ["BB_Deflate", "BB_Lz4", "BB_Snappy", "BB_Brotli", "BB_Lzma", "BB_Huffman"];

  private byte[] _compressibleData = null!;
  private byte[] _incompressibleData = null!;

  /// <summary>Maps block id to pre-compressed bytes for each data kind.</summary>
  private Dictionary<string, byte[]> _compressedCompressible = null!;
  private Dictionary<string, byte[]> _compressedIncompressible = null!;

  [Params("Compressible", "Incompressible")]
  public string DataKind { get; set; } = null!;

  [Params("BB_Deflate", "BB_Lz4", "BB_Snappy", "BB_Brotli", "BB_Lzma", "BB_Huffman")]
  public string BlockId { get; set; } = null!;

  [GlobalSetup]
  public void Setup() {
    FormatRegistration.EnsureInitialized();

    // 1 MB compressible: repeated English sentence
    const string sentence = "The quick brown fox jumps over the lazy dog. ";
    var sb = new System.Text.StringBuilder(1_048_576 + sentence.Length);
    while (sb.Length < 1_048_576)
      sb.Append(sentence);
    _compressibleData = System.Text.Encoding.UTF8.GetBytes(sb.ToString(0, 1_048_576));

    // 1 MB incompressible: deterministic pseudo-random
    _incompressibleData = new byte[1_048_576];
    var rng = new Random(42);
    rng.NextBytes(_incompressibleData);

    // Pre-compress for decompression benchmarks
    _compressedCompressible = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    _compressedIncompressible = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var id in BlockIds) {
      var bb = BuildingBlockRegistry.GetById(id);
      if (bb == null) continue;
      _compressedCompressible[id] = bb.Compress(_compressibleData);
      _compressedIncompressible[id] = bb.Compress(_incompressibleData);
    }
  }

  private byte[] SourceData => DataKind == "Compressible" ? _compressibleData : _incompressibleData;
  private byte[] CompressedData => DataKind == "Compressible"
    ? _compressedCompressible[BlockId]
    : _compressedIncompressible[BlockId];

  [Benchmark]
  public byte[] Compress() {
    var bb = BuildingBlockRegistry.GetById(BlockId)!;
    return bb.Compress(SourceData);
  }

  [Benchmark]
  public byte[] Decompress() {
    var bb = BuildingBlockRegistry.GetById(BlockId)!;
    return bb.Decompress(CompressedData);
  }
}
