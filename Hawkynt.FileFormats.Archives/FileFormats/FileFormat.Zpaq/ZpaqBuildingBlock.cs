using Compression.Registry;

namespace FileFormat.Zpaq;

/// <summary>
/// ZPAQ compression as a building block: the input coded as a plain ZPAQ stream (locator tag,
/// blocks of one segment, SHA-1 trailers) with zpaq's method 4 — an order-0 ICM, an ISSE chain
/// up to order 6, a match model and a mixer, all described in the block's own ZPAQL header.
/// </summary>
/// <remarks>
/// The output is what libzpaq's <c>compress(in, out, "4")</c> produces and any ZPAQ Level 2
/// decoder (<c>zpaq x</c>, <c>unzpaq</c>) reads; <see cref="Decompress"/> accepts any ZPAQ stream,
/// whatever its method. Blocks hold at most 16 MiB - 4 KiB each, as in libzpaq.
/// </remarks>
public sealed class ZpaqBuildingBlock : IBuildingBlock {
  private const string Method = "4";
  private const int BlockSize = (1 << 24) - 4096;

  /// <inheritdoc/>
  public string Id => "BB_Zpaq";
  /// <inheritdoc/>
  public string DisplayName => "ZPAQ";
  /// <inheritdoc/>
  public string Description => "ZPAQ Level 2 stream, method 4: ICM + ISSE chain + match model + mixer, contexts computed by the block's ZPAQL program";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var output = new ZpaqBuffer();
    for (var offset = 0; offset < data.Length; offset += BlockSize) {
      var chunk = new ZpaqBuffer(data.Slice(offset, Math.Min(BlockSize, data.Length - offset)));
      ZpaqMethods.CompressBlock(chunk, output, Method, storeSha1: true);
    }
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ZpaqStream.Decompress(data);
}

/// <summary>Decodes a whole ZPAQ stream (all blocks and segments, filenames and comments ignored).</summary>
internal static class ZpaqStream {
  /// <summary>Concatenates the decoded data of every segment, verifying stored SHA-1 trailers.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> data) {
    var input = new ZpaqBuffer(data);
    var output = new ZpaqBuffer();
    var d = new ZpaqDecompresser(input);
    d.SetOutput(output);
    while (d.FindBlock()) {
      while (d.FindFilename() != null) {
        d.ReadComment();
        var sha1 = new ZpaqSha1();
        d.SetSha1(sha1);
        d.Decompress();
        var stored = d.ReadSegmentEnd();
        if (stored != null && !stored.AsSpan().SequenceEqual(sha1.Result()))
          ZpaqError.Throw("segment checksum failed");
      }
    }
    return output.ToArray();
  }
}
