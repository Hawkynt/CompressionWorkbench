#pragma warning disable CS1591
namespace FileFormat.SplitFile;

/// <summary>
/// Splits a file into numbered parts (.001, .002, ...).
/// </summary>
public static class SplitFileWriter {
  /// <summary>
  /// Splits the input stream into parts of the given size.
  /// </summary>
  /// <param name="input">The input data stream.</param>
  /// <param name="outputDir">Directory to write parts to.</param>
  /// <param name="baseName">Base filename (e.g., "archive").</param>
  /// <param name="partSize">Maximum size of each part in bytes.</param>
  /// <returns>The number of parts written.</returns>
  public static int Split(Stream input, string outputDir, string baseName, long partSize) {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(partSize, 0);
    Directory.CreateDirectory(outputDir);

    var buffer = new byte[Math.Min(partSize, 81920)];
    var partNum = 1;

    while (true) {
      var partPath = Path.Combine(outputDir, $"{baseName}.{partNum:D3}");
      long written = 0;
      var anyData = false;

      using (var fs = File.Create(partPath)) {
        while (written < partSize) {
          var toRead = (int)Math.Min(buffer.Length, partSize - written);
          var read = input.Read(buffer, 0, toRead);
          if (read == 0) break;
          fs.Write(buffer, 0, read);
          written += read;
          anyData = true;
        }
      }

      if (!anyData) {
        File.Delete(partPath);
        break;
      }

      partNum++;

      // Check if source is exhausted
      if (written < partSize) break;
    }

    return partNum - 1;
  }
}
