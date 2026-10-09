#pragma warning disable CS1591

using System.Diagnostics;
using Compression.Core.Deflate;
using Bcl = System.IO.Compression;

namespace Compression.Benchmarks;

/// <summary>
/// A quick ratio and throughput table for the DEFLATE building block against the platform's
/// zlib, for the edit-measure loop where a full BenchmarkDotNet run takes too long. Each cell
/// is the best of several timed runs after a warm-up; <see cref="DeflateBenchmarks"/> is the
/// statistically careful version of the same measurement.
/// </summary>
/// <remarks>Run with <c>dotnet run -c Release -- deflate-table [runs] [corpus...]</c>.</remarks>
public static class DeflateThroughputTable {

  public static void Run(string[] args) {
    var runs = args.Length > 0 && int.TryParse(args[0], out var r) ? r : 5;
    var kinds = args.Length > 1 ? args[1..] : DeflateCorpus.Kinds;
    var buffer = new byte[1 << 16];
    var sink = new MemoryStream();

    Console.WriteLine("| Corpus | Level | Ratio ours | Ratio zlib | Compress ours MB/s | Compress zlib MB/s | Inflate ours MB/s | Inflate zlib MB/s |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var kind in kinds) {
      var data = DeflateCorpus.Get(kind);
      foreach (var level in new[] { 1, 6, 9 }) {
        var ours = DeflateCompressor.Compress(data, (DeflateCompressionLevel)level);
        if (!DeflateDecompressor.Decompress(ours).AsSpan().SequenceEqual(data))
          throw new InvalidOperationException($"{kind} level {level} does not round-trip.");

        var platform = PlatformCompress(data, level, sink);

        var compressOurs = Best(runs, () => {
          sink.Position = 0;
          sink.SetLength(0);
          var compressor = new DeflateCompressor(sink, (DeflateCompressionLevel)level);
          compressor.Write(data);
          compressor.Finish();
        });
        var compressPlatform = Best(runs, () => PlatformCompress(data, level, sink));
        var inflateOurs = Best(runs, () => {
          var inflate = new DeflateDecompressor(new MemoryStream(ours));
          while (inflate.Decompress(buffer) > 0) { }
        });
        var inflatePlatform = Best(runs, () => {
          using var inflate = new Bcl.DeflateStream(new MemoryStream(ours), Bcl.CompressionMode.Decompress);
          while (inflate.Read(buffer) > 0) { }
        });

        Console.WriteLine($"| {kind} | {level} | {Percent(ours.Length, data.Length)} | {Percent(platform, data.Length)} | "
                          + $"{Speed(data.Length, compressOurs)} | {Speed(data.Length, compressPlatform)} | "
                          + $"{Speed(data.Length, inflateOurs)} | {Speed(data.Length, inflatePlatform)} |");
      }
    }
  }

  private static long PlatformCompress(byte[] data, int level, MemoryStream sink) {
    sink.Position = 0;
    sink.SetLength(0);
    using (var deflate = new Bcl.DeflateStream(sink, new Bcl.ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true))
      deflate.Write(data);
    return sink.Length;
  }

  private static double Best(int runs, Action action) {
    action();
    var best = double.MaxValue;
    for (var i = 0; i < runs; ++i) {
      var watch = Stopwatch.StartNew();
      action();
      best = Math.Min(best, watch.Elapsed.TotalSeconds);
    }

    return best;
  }

  private static string Percent(long part, long whole) => FormattableString.Invariant($"{100.0 * part / whole:F2}%");

  private static string Speed(long bytes, double seconds) => FormattableString.Invariant($"{bytes / seconds / (1 << 20):F0}");
}
