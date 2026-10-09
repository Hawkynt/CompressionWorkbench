#pragma warning disable CS1591

using BenchmarkDotNet.Attributes;
using Compression.Core.Deflate;
using Bcl = System.IO.Compression;

namespace Compression.Benchmarks;

/// <summary>
/// The DEFLATE building block against the platform's zlib (zlib-ng in .NET 9+) at the same
/// zlib level, on every <see cref="DeflateCorpus"/> kind. The platform codec appears here only
/// as the yardstick; product code never uses it.
/// </summary>
/// <remarks>
/// Both decoders inflate the same bitstream — ours, at the level under test — so the decode
/// figures compare decoders rather than encoders. Each corpus is <see cref="DeflateCorpus.Size"/>
/// bytes: MB/s is that size over the mean.
/// </remarks>
[Config(typeof(InProcessConfig))]
public class DeflateBenchmarks {

  private byte[] _data = null!;
  private byte[] _compressed = null!;
  private readonly MemoryStream _sink = new();

  [ParamsSource(nameof(Corpora))]
  public string Corpus { get; set; } = null!;

  [Params(1, 6, 9)]
  public int Level { get; set; }

  [Params("Ours", "Platform")]
  public string Codec { get; set; } = null!;

  public static IEnumerable<string> Corpora => DeflateCorpus.Kinds;

  [GlobalSetup]
  public void Setup() {
    this._data = DeflateCorpus.Get(this.Corpus);
    this._compressed = DeflateCompressor.Compress(this._data, (DeflateCompressionLevel)this.Level);
  }

  [Benchmark]
  public long Compress() {
    this._sink.Position = 0;
    this._sink.SetLength(0);
    if (this.Codec == "Ours") {
      var compressor = new DeflateCompressor(this._sink, (DeflateCompressionLevel)this.Level);
      compressor.Write(this._data);
      compressor.Finish();
    } else
      using (var deflate = new Bcl.DeflateStream(this._sink, new Bcl.ZLibCompressionOptions { CompressionLevel = this.Level }, leaveOpen: true))
        deflate.Write(this._data);

    return this._sink.Length;
  }

  [Benchmark]
  public int Decompress() {
    // Both stream into the same 64 KB buffer, so neither pays for growing a whole-output array.
    var buffer = this._buffer;
    using var input = new MemoryStream(this._compressed);
    var total = 0;
    int n;
    if (this.Codec == "Ours") {
      var inflate = new DeflateDecompressor(input);
      while ((n = inflate.Decompress(buffer)) > 0)
        total += n;
    } else {
      using var inflate = new Bcl.DeflateStream(input, Bcl.CompressionMode.Decompress);
      while ((n = inflate.Read(buffer)) > 0)
        total += n;
    }

    return total;
  }

  private readonly byte[] _buffer = new byte[1 << 16];
}
