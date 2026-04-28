#pragma warning disable CS1591
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Compression.Tests.Support;

/// <summary>
/// Fetches the FreeDOS 1.4 LiveCD ISO from the upstream mirror and caches it
/// under <c>%TEMP%/cwb-freedos-cache</c>. Used by the <c>Fat_*_FreedosChkdsk</c>
/// gates that boot FreeDOS in DOSBox-X to run <c>chkdsk</c> against a FAT
/// image our writer produced.
/// <para>
/// FreeDOS is GPL — distributing the staged ISO is legal, but we still don't
/// bundle it in the repo. First test run hits the mirror, subsequent runs read
/// the cached file. SHA-256 is hash-pinned against
/// <c>https://download.freedos.org/1.4/verify.txt</c> so a tampered mirror is
/// caught at <see cref="EnsureLiveCdIso"/>.
/// </para>
/// <para>
/// The download is the <c>FD14-LiveCD.zip</c> archive (~520 MB → expands to
/// the ISO + boot floppies). We extract <c>FD14LIVE.iso</c> from inside the
/// zip and discard the rest. <see cref="EnsureLiveCdIso"/> returns the cached
/// path on success or <c>null</c> on any failure (offline, hash mismatch,
/// extraction error) — callers are expected to <c>Assert.Ignore</c> in that
/// case, never fail loudly.
/// </para>
/// </summary>
internal static class FreeDosCache {
  /// <summary>Mirror URL for the LiveCD ZIP. Hash-pinned via <see cref="LiveCdZipSha256"/>.</summary>
  public const string LiveCdZipUrl = "https://download.freedos.org/1.4/FD14-LiveCD.zip";

  /// <summary>SHA-256 of the FD14-LiveCD.zip per upstream verify.txt (2026-04 snapshot).</summary>
  public const string LiveCdZipSha256 = "2020ff6bb681967fd6eff8f51ad2e5cd5ab4421165948cef4246e4f7fcaf6339";

  /// <summary>Filename inside the zip archive of the actual ISO image.</summary>
  public const string IsoEntryName = "FD14LIVE.iso";

  private static readonly Lazy<HttpClient> _http = new(() => {
    var h = new HttpClient {
      // Big download — generous timeout, but bounded so an offline run
      // doesn't hang the test session indefinitely.
      Timeout = TimeSpan.FromMinutes(10),
    };
    h.DefaultRequestHeaders.UserAgent.ParseAdd("CompressionWorkbench-Tests/1.0");
    return h;
  });

  private static string CacheDir {
    get {
      var dir = Path.Combine(Path.GetTempPath(), "cwb-freedos-cache");
      Directory.CreateDirectory(dir);
      return dir;
    }
  }

  /// <summary>
  /// Returns the cached path to <c>FD14LIVE.iso</c>, fetching + extracting on
  /// first use. Returns <c>null</c> on any failure — callers must
  /// <c>Assert.Ignore</c> with an actionable hint in that case.
  /// <para>
  /// Set <c>CWB_FREEDOS_ISO=&lt;path&gt;</c> to bypass the download entirely
  /// (useful for air-gapped CI); the path is returned verbatim if it exists.
  /// </para>
  /// </summary>
  public static string? EnsureLiveCdIso() {
    // 1. Honor explicit env var first — air-gapped CI / dev machines can stage
    //    the ISO out-of-band and skip the network round-trip.
    var explicitPath = Environment.GetEnvironmentVariable("CWB_FREEDOS_ISO");
    if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
      return explicitPath;

    var isoPath = Path.Combine(CacheDir, IsoEntryName);
    if (File.Exists(isoPath) && new FileInfo(isoPath).Length > 0)
      return isoPath;  // cache hit

    // 2. Cache miss → download the zip, hash-verify, extract the ISO.
    var zipPath = Path.Combine(CacheDir, "FD14-LiveCD.zip");
    if (!File.Exists(zipPath) || !Sha256MatchesFile(zipPath, LiveCdZipSha256)) {
      try {
        var bytes = _http.Value.GetByteArrayAsync(LiveCdZipUrl).GetAwaiter().GetResult();
        if (!Sha256OfBytes(bytes).Equals(LiveCdZipSha256, StringComparison.OrdinalIgnoreCase))
          return null; // Tampered mirror — refuse to cache a mystery blob.
        File.WriteAllBytes(zipPath, bytes);
      } catch (HttpRequestException) {
        return null; // offline / DNS / 4xx / 5xx
      } catch (TaskCanceledException) {
        return null; // timeout
      } catch (IOException) {
        return null; // disk write failed
      }
    }

    try {
      using var zs = ZipFile.OpenRead(zipPath);
      var entry = zs.GetEntry(IsoEntryName)
                  ?? zs.Entries.FirstOrDefault(e =>
                       e.FullName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
      if (entry is null) return null;
      entry.ExtractToFile(isoPath, overwrite: true);
      return isoPath;
    } catch (InvalidDataException) {
      return null;  // corrupt zip
    } catch (IOException) {
      return null;
    }
  }

  private static bool Sha256MatchesFile(string path, string expected) {
    try {
      using var fs = File.OpenRead(path);
      var actual = Convert.ToHexStringLower(SHA256.HashData(fs));
      return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    } catch (IOException) {
      return false;
    }
  }

  private static string Sha256OfBytes(byte[] data) =>
    Convert.ToHexStringLower(SHA256.HashData(data));
}
