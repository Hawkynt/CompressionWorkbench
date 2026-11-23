using System.IO.Compression;
using System.Security.Cryptography;

namespace Compression.Tests.Apfs;

/// <summary>
/// Volumes from fox-it/dissect.apfs's test data (AGPL-3.0): downloaded when a test runs, at
/// a pinned revision and SHA-256, cached in the temp folder, never committed. A test that
/// needs one is skipped when it cannot be fetched.
/// </summary>
internal static class DissectApfsSamples {
  private const string Base = "https://media.githubusercontent.com/media/fox-it/dissect.apfs/a5626763922a16182a670e4906cfb8b88cf4baa6/tests/_data/";
  private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

  public static byte[] Encrypted() => Get("encrypted.bin.gz", "9780647470fd4a818c0c7ae86f70f2b7d39c48a5ccf2f89f0f2bd47d35915f1d");
  public static byte[] Snapshot() => Get("snapshot.bin.gz", "63dedb8142c17e6ab6709c9fe2c37c695f1caa617946bc8d499087c5299dcf2b");

  private static byte[] Get(string name, string sha256) {
    lock (Cache) {
      if (Cache.TryGetValue(name, out var cached)) return cached;
      var file = Path.Combine(Path.GetTempPath(), "cwb-dissect-apfs-" + name);
      byte[] gz;
      if (File.Exists(file) && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) == sha256) {
        gz = File.ReadAllBytes(file);
      } else {
        try {
          using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
          gz = http.GetByteArrayAsync(Base + name).GetAwaiter().GetResult();
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
          Assert.Ignore($"{name} (AGPL-3.0) is downloaded at test time and the download failed: {ex.Message}");
          throw;
        }
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(gz)), Is.EqualTo(sha256), $"pinned sample {name} changed upstream");
        try { File.WriteAllBytes(file, gz); } catch (IOException) { /* the cache is best effort */ }
      }
      using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
      using var output = new MemoryStream();
      input.CopyTo(output);
      return Cache[name] = output.ToArray();
    }
  }

  /// <summary>Reads an apfs-fuse manifest: <c>f sha path</c>, <c>l target path</c>, <c>o - path</c>, plus <c>s</c>/<c>n</c> lines.</summary>
  public static IEnumerable<(char Kind, string Value, string Path)> Manifest(string resource) {
    using var stream = typeof(DissectApfsSamples).Assembly.GetManifestResourceStream("ApfsVectors." + resource)!;
    using var text = new StreamReader(stream);
    while (text.ReadLine() is { } line) {
      if (line.Length < 4 || line[0] == '#') continue;
      var parts = line.Split(' ', 3);
      yield return (parts[0][0], parts[1], parts.Length > 2 ? parts[2] : "");
    }
  }
}
