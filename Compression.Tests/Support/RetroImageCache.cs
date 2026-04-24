#pragma warning disable CS1591
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Compression.Tests.Support;

/// <summary>
/// Fetches retro disk images from public-domain sources (zimmers.net, archive.org)
/// and caches them locally so tests don't refetch on every run.
/// <para>
/// Downloads are written under <c>%TEMP%/cwb-retro-image-cache</c>. Tests that need
/// a wild image call <see cref="Fetch"/> or <see cref="FetchGz"/>; if the network is
/// offline or the URL 404s the helper returns <c>null</c> and the caller should
/// <see cref="NUnit.Framework.Assert.Ignore(string)"/> with a user-visible note.
/// </para>
/// <para>
/// All sample images are cryptographic-hash-verified against a <c>sha256</c>
/// literal the test supplies — this locks the fixture to a known artifact and
/// catches mirror tampering.
/// </para>
/// </summary>
internal static class RetroImageCache {
  private static readonly Lazy<HttpClient> _http = new(() => {
    var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    h.DefaultRequestHeaders.UserAgent.ParseAdd("CompressionWorkbench-Tests/1.0");
    return h;
  });

  private static string CacheDir {
    get {
      var dir = Path.Combine(Path.GetTempPath(), "cwb-retro-image-cache");
      Directory.CreateDirectory(dir);
      return dir;
    }
  }

  /// <summary>
  /// Fetches a raw (uncompressed) disk image from <paramref name="url"/>, verifies
  /// its sha256 matches <paramref name="sha256"/>, and returns the cached local path.
  /// Returns <c>null</c> on any failure (404, DNS, timeout, hash mismatch).
  /// </summary>
  public static string? Fetch(string url, string sha256, string localName) =>
    FetchInternal(url, sha256, localName, decompressGz: false);

  /// <summary>
  /// Fetches a gzipped disk image (<c>.d64.gz</c> etc.), decompresses it, verifies
  /// the decompressed bytes' sha256 matches <paramref name="sha256OfDecompressed"/>,
  /// and returns the cached local path of the decompressed image. Returns <c>null</c>
  /// on any failure.
  /// </summary>
  public static string? FetchGz(string url, string sha256OfDecompressed, string localName) =>
    FetchInternal(url, sha256OfDecompressed, localName, decompressGz: true);

  private static string? FetchInternal(string url, string expectedSha256, string localName, bool decompressGz) {
    var localPath = Path.Combine(CacheDir, localName);
    // Cache hit: trust if sha matches.
    if (File.Exists(localPath) && Sha256Of(localPath).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
      return localPath;

    // Cache miss or stale — refetch.
    try {
      var bytes = _http.Value.GetByteArrayAsync(url).GetAwaiter().GetResult();
      if (decompressGz) {
        using var inMs = new MemoryStream(bytes);
        using var gz = new GZipStream(inMs, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        bytes = outMs.ToArray();
      }
      var actualSha = Sha256Of(bytes);
      if (!actualSha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) {
        // Hash mismatch — don't cache a mystery blob; surface as unavailable.
        return null;
      }
      File.WriteAllBytes(localPath, bytes);
      return localPath;
    } catch (HttpRequestException) {
      return null;  // offline, DNS, 4xx, 5xx
    } catch (TaskCanceledException) {
      return null;  // timeout
    } catch (IOException) {
      return null;  // disk write failed
    } catch (InvalidDataException) {
      return null;  // gzip corrupt
    }
  }

  private static string Sha256Of(string path) {
    using var fs = File.OpenRead(path);
    var hash = SHA256.HashData(fs);
    return Convert.ToHexStringLower(hash);
  }

  private static string Sha256Of(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
