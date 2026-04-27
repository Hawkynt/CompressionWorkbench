namespace Compression.Analysis;

/// <summary>
/// Writes carved files produced by <see cref="FileCarver"/> to an output
/// directory. File names encode the source offset so recovered files can be
/// traced back to their location in the original image:
/// <c>offset_0x001A000.jpg</c>.
/// </summary>
public static class FileCarverOutputSink {

  /// <summary>
  /// Writes each carved file into <paramref name="outputDir"/>. When the
  /// <paramref name="hits"/> were produced with <c>ExtractData=false</c>, the
  /// bytes are re-read from <paramref name="stream"/> on the fly.
  /// </summary>
  public static IReadOnlyList<string> ExtractAll(
      Stream stream,
      IReadOnlyList<CarvedFile> hits,
      string outputDir) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(hits);
    ArgumentException.ThrowIfNullOrEmpty(outputDir);

    Directory.CreateDirectory(outputDir);
    var written = new List<string>(hits.Count);

    foreach (var h in hits) {
      var name = $"offset_0x{h.Offset:X8}{NormaliseExtension(h.Extension)}";
      var fullPath = Path.Combine(outputDir, name);

      if (h.Data is { } bytes) {
        File.WriteAllBytes(fullPath, bytes);
      } else {
        if (!stream.CanSeek) throw new InvalidOperationException(
          "Stream must be seekable when hits were produced with ExtractData=false.");
        StreamCopyRange(stream, h.Offset, h.Length, fullPath);
      }

      written.Add(fullPath);
    }

    return written;
  }

  private static void StreamCopyRange(Stream src, long offset, long length, string outPath) {
    src.Position = offset;
    using var dst = File.Create(outPath);
    var buf = new byte[81920];
    var remaining = length;
    while (remaining > 0) {
      var toRead = (int)Math.Min(buf.Length, remaining);
      var read = src.Read(buf, 0, toRead);
      if (read <= 0) break;
      dst.Write(buf, 0, read);
      remaining -= read;
    }
  }

  private static string NormaliseExtension(string ext) {
    if (string.IsNullOrEmpty(ext)) return ".bin";
    return ext.StartsWith('.') ? ext : "." + ext;
  }
}
