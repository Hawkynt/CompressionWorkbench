using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace Compression.Lib;

/// <summary>
/// Provides parallel compression for ZIP archives.
/// Entries are compressed independently in parallel, then written sequentially via AddRawEntry.
/// </summary>
public static class ParallelCompression {

  /// <summary>
  /// Compresses ZIP entries in parallel and writes them sequentially.
  /// Supports Deflate and Deflate64 methods; other methods fall back to sequential AddEntry.
  /// </summary>
  public static void CreateZipParallel(Stream output, IReadOnlyList<ArchiveInput> inputs,
      string? password, FileFormat.Zip.ZipCompressionMethod method,
      DeflateCompressionLevel level, HashSet<string>? incompressible, int maxThreads,
      FileFormat.Zip.ZipEncryptionMethod encryptionMethod = FileFormat.Zip.ZipEncryptionMethod.Aes256) {

    // Only Deflate/Deflate64/Store benefit from parallel pre-compression
    var canParallelCompress = method is FileFormat.Zip.ZipCompressionMethod.Deflate
      or FileFormat.Zip.ZipCompressionMethod.Deflate64
      or FileFormat.Zip.ZipCompressionMethod.Store;

    if (!canParallelCompress) {
      CreateZipSequential(output, inputs, password, method, level, incompressible, encryptionMethod);
      return;
    }

    var dirs = inputs.Where(i => i.IsDirectory).ToList();
    var files = inputs.Where(i => !i.IsDirectory).ToList();

    // Pre-compress all files in parallel
    var results = new (string Name, byte[] CompressedData, FileFormat.Zip.ZipCompressionMethod Method,
      uint Crc, long OrigSize)[files.Count];

    var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads };

    Parallel.For(0, files.Count, options, i => {
      var input = files[i];
      var data = File.ReadAllBytes(input.FullPath);
      var crc = Crc32.Compute(data);

      var entryMethod = incompressible != null && incompressible.Contains(input.FullPath)
        ? FileFormat.Zip.ZipCompressionMethod.Store
        : method;

      byte[] compressed;
      if (entryMethod == FileFormat.Zip.ZipCompressionMethod.Store) {
        compressed = data;
      }
      else {
        compressed = entryMethod == FileFormat.Zip.ZipCompressionMethod.Deflate64
          ? Deflate64Compressor.Compress(data, level)
          : DeflateCompressor.Compress(data, level);

        if (compressed.Length >= data.Length) {
          compressed = data;
          entryMethod = FileFormat.Zip.ZipCompressionMethod.Store;
        }
      }

      results[i] = (input.EntryName, compressed, entryMethod, crc, data.Length);
    });

    // Write sequentially
    var w = new FileFormat.Zip.ZipWriter(output, leaveOpen: true,
      compressionLevel: level, password: password, encryptionMethod: encryptionMethod);

    foreach (var d in dirs)
      w.AddDirectory(d.EntryName);

    foreach (var (name, compressed, m, crc, origSize) in results)
      w.AddRawEntry(name, compressed, m, crc, origSize);

    w.Finish();
  }

  /// <summary>Sequential fallback for non-Deflate ZIP methods.</summary>
  private static void CreateZipSequential(Stream output, IReadOnlyList<ArchiveInput> inputs,
      string? password, FileFormat.Zip.ZipCompressionMethod method,
      DeflateCompressionLevel level, HashSet<string>? incompressible,
      FileFormat.Zip.ZipEncryptionMethod encryptionMethod = FileFormat.Zip.ZipEncryptionMethod.Aes256) {
    var w = new FileFormat.Zip.ZipWriter(output, leaveOpen: true,
      compressionLevel: level, password: password, encryptionMethod: encryptionMethod);
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.EntryName); continue; }
      var data = File.ReadAllBytes(i.FullPath);
      var entryMethod = incompressible != null && incompressible.Contains(i.FullPath)
        ? FileFormat.Zip.ZipCompressionMethod.Store
        : method;
      w.AddEntry(i.EntryName, data, entryMethod);
    }
    w.Finish();
  }
}
