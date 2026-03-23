using Compression.Analysis.Statistics;
using Compression.Core.Streams;

namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Attempts format-level decompression using the Compression.Lib format libraries.
/// Supports both stream-based (CompressionStream subclasses) and static decompressors.
/// </summary>
public sealed class TrialFormat : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm { get; }

  private readonly Func<byte[], int, byte[]?>? _spanDecompressor;
  private readonly Func<Stream, Stream>? _streamFactory;

  private TrialFormat(string algorithm, Func<byte[], int, byte[]?> spanDecompressor) {
    Algorithm = algorithm;
    _spanDecompressor = spanDecompressor;
  }

  /// <summary>Creates a trial strategy using a stream-based decompressor factory.</summary>
  public TrialFormat(string algorithm, Func<Stream, Stream> streamFactory) {
    Algorithm = algorithm;
    _streamFactory = streamFactory;
  }

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    try {
      byte[]? result;
      if (_spanDecompressor != null) {
        result = _spanDecompressor(data.ToArray(), maxOutput);
      }
      else if (_streamFactory != null) {
        result = DecompressViaStream(data, maxOutput, ct);
      }
      else {
        return Fail("No decompressor configured");
      }

      if (result == null || result.Length == 0)
        return Fail("Output empty");

      var entropy = BinaryStatistics.ComputeEntropy(result);
      return new(Algorithm, 0, result.Length, entropy, true, null, result);
    }
    catch (Exception ex) {
      return Fail(ex.Message);
    }
  }

  private byte[]? DecompressViaStream(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    using var input = new MemoryStream(data.ToArray());
    using var decompressor = _streamFactory!(input);
    using var output = new MemoryStream();

    var buffer = new byte[8192];
    var totalRead = 0;
    while (totalRead < maxOutput) {
      if (ct.IsCancellationRequested) return null;
      var read = decompressor.Read(buffer, 0, Math.Min(buffer.Length, maxOutput - totalRead));
      if (read == 0) break;
      output.Write(buffer, 0, read);
      totalRead += read;
    }
    return output.ToArray();
  }

  private DecompressionAttempt Fail(string error)
    => new(Algorithm, 0, -1, -1, false, error, null);

  /// <summary>Creates trial strategies for all supported stream formats.</summary>
  public static IEnumerable<TrialFormat> CreateAll() {
    // Stream-based decompressors
    yield return new("Gzip", s => new FileFormat.Gzip.GzipStream(s, CompressionStreamMode.Decompress, leaveOpen: true));
    yield return new("Bzip2", s => new FileFormat.Bzip2.Bzip2Stream(s, CompressionStreamMode.Decompress, leaveOpen: true));
    yield return new("XZ", s => new FileFormat.Xz.XzStream(s, CompressionStreamMode.Decompress, leaveOpen: true));
    yield return new("Zstd", s => new FileFormat.Zstd.ZstdStream(s, CompressionStreamMode.Decompress, leaveOpen: true));
    yield return new("Compress", s => new FileFormat.Compress.CompressStream(s, CompressionStreamMode.Decompress, leaveOpen: true));

    // Static/span-based decompressors
    yield return new("Zlib", (data, _) => FileFormat.Zlib.ZlibStream.Decompress(data));
    yield return new("LZMA", (data, _) => {
      using var input = new MemoryStream(data);
      using var output = new MemoryStream();
      FileFormat.Lzma.LzmaStream.Decompress(input, output);
      return output.ToArray();
    });
    yield return new("Lzip", (data, _) => {
      using var input = new MemoryStream(data);
      using var output = new MemoryStream();
      FileFormat.Lzip.LzipStream.Decompress(input, output);
      return output.ToArray();
    });
  }
}
