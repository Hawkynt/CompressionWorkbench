#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.AppleSparse;

/// <summary>
/// Reader for Apple sparsebundle bundles (directories with extension
/// <c>.sparsebundle</c>). Parses the <c>Info.plist</c> to learn band-size and
/// total-size, then enumerates <c>bands/</c> — each file there is a single
/// physical band whose hex-decoded name is the virtual band index.
/// </summary>
/// <remarks>
/// Sparsebundle is a <em>directory</em> format, not a stream format, so this
/// reader is constructed from a path to the bundle root (or to its
/// <c>Info.plist</c>). The bundle-as-stream surface used by
/// <see cref="SparsebundleFormatDescriptor"/> accepts a <see cref="FileStream"/>
/// over <c>Info.plist</c> and derives the bundle directory from its path; for
/// non-file streams it falls back to detection-only.
/// </remarks>
public sealed class SparsebundleReader {

  /// <summary>Band size in bytes (from plist key <c>band-size</c>, default 8 MB).</summary>
  public long BandSize { get; }

  /// <summary>Total virtual size in bytes (from plist key <c>size</c>).</summary>
  public long VirtualSize { get; }

  /// <summary>Sparsebundle backing-store version (plist key <c>bundle-backingstore-version</c>).</summary>
  public long BackingStoreVersion { get; }

  /// <summary>Bundle root directory (the <c>*.sparsebundle</c> folder).</summary>
  public string BundleRoot { get; }

  /// <summary>Bands directory (<c>{BundleRoot}/bands</c>).</summary>
  public string BandsDir { get; }

  /// <summary>Plist key map (raw string values).</summary>
  public IReadOnlyDictionary<string, string> Plist { get; }

  /// <summary>
  /// Opens the sparsebundle at <paramref name="bundleRoot"/>. <paramref name="bundleRoot"/>
  /// must be a directory containing <c>Info.plist</c> and <c>bands/</c>.
  /// </summary>
  public SparsebundleReader(string bundleRoot) {
    ArgumentNullException.ThrowIfNull(bundleRoot);
    if (!Directory.Exists(bundleRoot))
      throw new DirectoryNotFoundException($"Sparsebundle root not found: {bundleRoot}");

    var infoPath = Path.Combine(bundleRoot, "Info.plist");
    if (!File.Exists(infoPath))
      throw new FileNotFoundException("Sparsebundle is missing Info.plist.", infoPath);

    var bandsDir = Path.Combine(bundleRoot, "bands");
    if (!Directory.Exists(bandsDir))
      throw new DirectoryNotFoundException($"Sparsebundle is missing bands/ directory: {bandsDir}");

    this.BundleRoot = bundleRoot;
    this.BandsDir = bandsDir;

    var plist = InfoPlistParser.ParseTopLevelDict(File.ReadAllBytes(infoPath));
    this.Plist = plist;
    this.BandSize = InfoPlistParser.GetInt64(plist, "band-size", defaultValue: 8 * 1024 * 1024);
    this.VirtualSize = InfoPlistParser.GetInt64(plist, "size", defaultValue: 0);
    this.BackingStoreVersion = InfoPlistParser.GetInt64(plist, "bundle-backingstore-version", defaultValue: 1);

    if (this.BandSize <= 0)
      throw new InvalidDataException($"sparsebundle: implausible band-size {this.BandSize}.");
    if (this.VirtualSize < 0)
      throw new InvalidDataException($"sparsebundle: implausible size {this.VirtualSize}.");
  }

  /// <summary>
  /// Tries to construct a <see cref="SparsebundleReader"/> from a file path
  /// pointing at the bundle root or at its <c>Info.plist</c>. Returns
  /// <c>null</c> if neither resolves to a sparsebundle.
  /// </summary>
  public static SparsebundleReader? TryFromPath(string path) {
    if (string.IsNullOrEmpty(path)) return null;
    try {
      var root = Directory.Exists(path)
        ? path
        : (string.Equals(Path.GetFileName(path), "Info.plist", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path)
            : null);
      if (string.IsNullOrEmpty(root)) return null;
      if (!Directory.Exists(Path.Combine(root, "bands"))) return null;
      if (!File.Exists(Path.Combine(root, "Info.plist"))) return null;
      return new SparsebundleReader(root);
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Reads <paramref name="destination"/>.<see cref="Span{T}.Length"/> bytes
  /// from virtual offset <paramref name="virtualOffset"/>. Missing bands
  /// return zeros.
  /// </summary>
  public int Read(long virtualOffset, Span<byte> destination) {
    if (virtualOffset < 0) throw new ArgumentOutOfRangeException(nameof(virtualOffset));
    if (virtualOffset >= this.VirtualSize) return 0;

    var remaining = (int)Math.Min(destination.Length, this.VirtualSize - virtualOffset);
    var total = 0;
    while (remaining > 0) {
      var bandIdx = virtualOffset / this.BandSize;
      var bandOff = (int)(virtualOffset % this.BandSize);
      var take = (int)Math.Min(remaining, this.BandSize - bandOff);

      var bandPath = Path.Combine(this.BandsDir, bandIdx.ToString("x", CultureInfo.InvariantCulture));
      if (!File.Exists(bandPath)) {
        destination.Slice(total, take).Clear();
      } else {
        using var fs = new FileStream(bandPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bandLen = fs.Length;
        if (bandOff >= bandLen) {
          destination.Slice(total, take).Clear();
        } else {
          fs.Position = bandOff;
          var canRead = (int)Math.Min(take, bandLen - bandOff);
          var n = fs.Read(destination.Slice(total, canRead));
          if (n < take)
            destination.Slice(total + n, take - n).Clear();
        }
      }

      virtualOffset += take;
      total += take;
      remaining -= take;
    }
    return total;
  }

  /// <summary>Materialises the full virtual disk as a byte array.</summary>
  public byte[] ExtractDisk() {
    var buf = new byte[this.VirtualSize];
    if (this.VirtualSize > 0)
      this.Read(0, buf);
    return buf;
  }
}
