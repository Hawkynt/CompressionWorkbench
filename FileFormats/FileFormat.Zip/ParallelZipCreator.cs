#pragma warning disable CS1591
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Registry;

namespace FileFormat.Zip;

/// <summary>
/// Parallel ZIP creation: entries are compressed independently in parallel,
/// then written sequentially via <see cref="ZipWriter.AddRawEntry"/>.
/// </summary>
public static class ParallelZipCreator {

  /// <summary>
  /// Compresses ZIP entries in parallel and writes them sequentially. Only
  /// Deflate / Deflate64 / Store benefit from pre-compression; other methods
  /// fall through to sequential <see cref="ZipWriter.AddEntry"/>.
  /// </summary>
  public static void CreateZipParallel(Stream output, IReadOnlyList<ArchiveInputInfo> inputs,
      string? password, ZipCompressionMethod method, DeflateCompressionLevel level,
      HashSet<string>? incompressible, int maxThreads,
      ZipEncryptionMethod encryptionMethod = ZipEncryptionMethod.Aes256) {

    var canParallelCompress = method is ZipCompressionMethod.Deflate
      or ZipCompressionMethod.Deflate64
      or ZipCompressionMethod.Store;

    if (!canParallelCompress) {
      CreateZipSequential(output, inputs, password, method, level, incompressible, encryptionMethod);
      return;
    }

    var dirs = inputs.Where(i => i.IsDirectory).ToList();
    var files = inputs.Where(i => !i.IsDirectory).ToList();

    var results = new (string Name, byte[] CompressedData, ZipCompressionMethod Method,
      uint Crc, long OrigSize)[files.Count];

    var options = new ParallelOptions { MaxDegreeOfParallelism = maxThreads };
    Parallel.For(0, files.Count, options, i => {
      var input = files[i];
      var data = File.ReadAllBytes(input.FullPath);
      var crc = Crc32.Compute(data);

      var entryMethod = incompressible != null && incompressible.Contains(input.FullPath)
        ? ZipCompressionMethod.Store
        : method;

      byte[] compressed;
      if (entryMethod == ZipCompressionMethod.Store) {
        compressed = data;
      } else {
        compressed = entryMethod == ZipCompressionMethod.Deflate64
          ? Deflate64Compressor.Compress(data, level)
          : DeflateCompressor.Compress(data, level);
        if (compressed.Length >= data.Length) {
          compressed = data;
          entryMethod = ZipCompressionMethod.Store;
        }
      }
      results[i] = (input.ArchiveName, compressed, entryMethod, crc, data.Length);
    });

    var w = new ZipWriter(output, leaveOpen: true,
      compressionLevel: level, password: password, encryptionMethod: encryptionMethod);
    foreach (var d in dirs)
      w.AddDirectory(d.ArchiveName);
    foreach (var (name, compressed, m, crc, origSize) in results)
      w.AddRawEntry(name, compressed, m, crc, origSize);
    w.Finish();
  }

  /// <summary>Sequential fallback for non-Deflate ZIP methods.</summary>
  private static void CreateZipSequential(Stream output, IReadOnlyList<ArchiveInputInfo> inputs,
      string? password, ZipCompressionMethod method, DeflateCompressionLevel level,
      HashSet<string>? incompressible,
      ZipEncryptionMethod encryptionMethod = ZipEncryptionMethod.Aes256) {
    var w = new ZipWriter(output, leaveOpen: true,
      compressionLevel: level, password: password, encryptionMethod: encryptionMethod);
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.ArchiveName); continue; }
      var data = File.ReadAllBytes(i.FullPath);
      var entryMethod = incompressible != null && incompressible.Contains(i.FullPath)
        ? ZipCompressionMethod.Store
        : method;
      w.AddEntry(i.ArchiveName, data, entryMethod);
    }
    w.Finish();
  }
}
