using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using FileFormat.Ar;
using FileFormat.Gzip;
using FileFormat.Tar;

namespace FileFormat.Vib;

/// <summary>
/// Metadata and layout options used when creating a CommunitySupported VMware VIB.
/// Signed acceptance levels are deliberately not exposed: creating those requires a
/// VMware-trusted signing identity rather than merely different descriptor metadata.
/// </summary>
public sealed class VibWriterOptions {
  /// <summary>Default VIB package name.</summary>
  public const string DefaultName = "compression-workbench";

  /// <summary>Default package version.</summary>
  public const string DefaultVersion = "1.0.0-0.0.0";

  /// <summary>Default vendor string.</summary>
  public const string DefaultVendor = "Community";

  /// <summary>Default package summary.</summary>
  public const string DefaultSummary = "CommunitySupported VIB created by CompressionWorkbench";

  /// <summary>Default package description.</summary>
  public const string DefaultDescription = "CommunitySupported VIB created by CompressionWorkbench";

  /// <summary>Package name stored in <c>descriptor.xml</c>.</summary>
  public string Name { get; init; } = DefaultName;

  /// <summary>Package version stored in <c>descriptor.xml</c>.</summary>
  public string Version { get; init; } = DefaultVersion;

  /// <summary>Vendor stored in <c>descriptor.xml</c>.</summary>
  public string Vendor { get; init; } = DefaultVendor;

  /// <summary>Short package summary.</summary>
  public string Summary { get; init; } = DefaultSummary;

  /// <summary>Long package description.</summary>
  public string Description { get; init; } = DefaultDescription;

  /// <summary>
  /// Release timestamp. Unix epoch is the deterministic default; callers may supply
  /// a real release time when reproducibility is not required.
  /// </summary>
  public DateTimeOffset ReleaseDate { get; init; } = DateTimeOffset.UnixEpoch;

  /// <summary>Whether ESXi may install the VIB without rebooting first.</summary>
  public bool LiveInstallAllowed { get; init; } = true;

  /// <summary>Whether ESXi may remove the VIB without rebooting first.</summary>
  public bool LiveRemoveAllowed { get; init; } = true;

  /// <summary>Whether the VIB is suitable for stateless ESXi images.</summary>
  public bool StatelessReady { get; init; } = true;

  /// <summary>Whether installation requires maintenance mode.</summary>
  public bool MaintenanceMode { get; init; }

  /// <summary>Unix mode used for regular payload files. Defaults to 0644.</summary>
  public int FileMode { get; init; } = 0x1A4;

  /// <summary>Unix mode used for payload directories. Defaults to 0755.</summary>
  public int DirectoryMode { get; init; } = 0x1ED;

  /// <summary>DEFLATE level used by the TGZ payload.</summary>
  public DeflateCompressionLevel CompressionLevel { get; init; } = DeflateCompressionLevel.Default;
}

/// <summary>
/// Creates a VMware vSphere Installation Bundle containing a single TGZ payload.
/// The emitted VIB is intentionally <c>CommunitySupported</c>: the AR member
/// <c>sig.pkcs7</c> is present but empty, as required for unsigned community VIBs.
/// </summary>
/// <remarks>
/// The descriptor follows the VIB 5.0 shape used by ESXi 8-era community packages:
/// a SHA-256 checksum over the compressed TGZ plus SHA-256 and SHA-1 checksums over
/// the gunzipped TAR. The SHA-1 digest is compatibility metadata mandated by the
/// container convention, not a security decision by this implementation.
/// </remarks>
public sealed class VibWriter : IDisposable {
  /// <summary>Canonical name of the single payload AR member.</summary>
  public const string PayloadName = "payload1";

  private readonly Stream _output;
  private readonly VibWriterOptions _options;
  private readonly bool _leaveOpen;
  private readonly List<VibInput> _entries = [];
  private readonly Dictionary<string, bool> _pathKinds = new(StringComparer.Ordinal);
  private bool _finished;
  private bool _disposed;

  /// <summary>Creates a writer over <paramref name="output"/>.</summary>
  public VibWriter(Stream output, VibWriterOptions? options = null, bool leaveOpen = false) {
    this._output = output ?? throw new ArgumentNullException(nameof(output));
    if (!output.CanWrite)
      throw new ArgumentException("The VIB destination stream must be writable.", nameof(output));
    this._options = options ?? new VibWriterOptions();
    this._leaveOpen = leaveOpen;
    ValidateOptions(this._options);
  }

  /// <summary>Adds a payload path and its bytes.</summary>
  public void AddEntry(string path, byte[] data, bool isDirectory = false) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(data);

    var normalized = NormalizePath(path, isDirectory);
    this.ValidatePathConflict(normalized, isDirectory);
    this._pathKinds.Add(normalized, isDirectory);
    this._entries.Add(new VibInput(normalized, isDirectory ? [] : data, isDirectory));
  }

  /// <summary>Finalizes the TGZ payload, descriptor, empty signature, and outer AR archive.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    var ordered = this._entries
      .OrderBy(e => e.Path, StringComparer.Ordinal)
      .ToArray();

    var tarBytes = BuildTar(ordered, this._options);
    var payloadBytes = BuildGzip(tarBytes, this._options.CompressionLevel);
    var descriptorBytes = BuildDescriptor(ordered, tarBytes, payloadBytes, this._options);

    if (this._output.CanSeek) {
      this._output.Position = 0;
      this._output.SetLength(0);
    }

    using var ar = new ArWriter(this._output, leaveOpen: true);
    ar.Write([
      new ArEntry {
        Name = VibConstants.DescriptorName,
        ModifiedTime = DateTimeOffset.UnixEpoch,
        Data = descriptorBytes,
      },
      new ArEntry {
        Name = VibConstants.SignatureName,
        ModifiedTime = DateTimeOffset.UnixEpoch,
        Data = [],
      },
      new ArEntry {
        Name = PayloadName,
        ModifiedTime = DateTimeOffset.UnixEpoch,
        Data = payloadBytes,
      },
    ]);
  }

  private void ValidatePathConflict(string path, bool isDirectory) {
    if (this._pathKinds.ContainsKey(path))
      throw new ArgumentException($"Duplicate VIB payload path '{path}'.", nameof(path));

    foreach (var existing in this._pathKinds) {
      if (!existing.Value && path.StartsWith(existing.Key + "/", StringComparison.Ordinal))
        throw new ArgumentException($"VIB payload path '{path}' is below file '{existing.Key}'.", nameof(path));
      if (!isDirectory && existing.Key.StartsWith(path + "/", StringComparison.Ordinal))
        throw new ArgumentException($"VIB payload file '{path}' conflicts with existing child '{existing.Key}'.", nameof(path));
    }
  }

  private static byte[] BuildTar(IReadOnlyList<VibInput> entries, VibWriterOptions options) {
    using var tarStream = new MemoryStream();
    using (var tar = new TarWriter(tarStream, leaveOpen: true, format: TarHeaderFormat.Pax, blockingFactor: 1)) {
      foreach (var entry in entries) {
        var tarEntry = new TarEntry {
          Name = entry.IsDirectory ? entry.Path + "/" : entry.Path,
          TypeFlag = entry.IsDirectory ? (byte)'5' : (byte)'0',
          Mode = entry.IsDirectory ? options.DirectoryMode : options.FileMode,
          ModifiedTime = DateTimeOffset.UnixEpoch,
          Size = entry.IsDirectory ? 0 : entry.Data.LongLength,
        };
        if (entry.IsDirectory)
          tar.AddEntry(tarEntry, ReadOnlySpan<byte>.Empty);
        else
          tar.AddEntry(tarEntry, entry.Data);
      }
      tar.Finish();
    }
    return tarStream.ToArray();
  }

  private static byte[] BuildGzip(byte[] tarBytes, DeflateCompressionLevel level) {
    using var output = new MemoryStream();
    using (var gzip = new GzipStream(output, CompressionStreamMode.Compress, level, leaveOpen: true)) {
      gzip.Header.ModificationTime = 0;
      gzip.Write(tarBytes, 0, tarBytes.Length);
    }
    return output.ToArray();
  }

  private static byte[] BuildDescriptor(
      IReadOnlyList<VibInput> entries,
      byte[] tarBytes,
      byte[] payloadBytes,
      VibWriterOptions options) {
    var payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
    var tarSha256 = Convert.ToHexStringLower(SHA256.HashData(tarBytes));
    var tarSha1 = Convert.ToHexStringLower(SHA1.HashData(tarBytes));

    var fileList = new XElement("file-list",
      entries.Where(e => !e.IsDirectory).Select(e => new XElement("file", e.Path)));

    var root = new XElement("vib",
      new XAttribute("version", "5.0"),
      new XElement("type", "bootbank"),
      new XElement("name", options.Name),
      new XElement("version", options.Version),
      new XElement("vendor", options.Vendor),
      new XElement("summary", options.Summary),
      new XElement("description", options.Description),
      new XElement("release-date", options.ReleaseDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
      new XElement("urls"),
      new XElement("relationships",
        new XElement("depends"),
        new XElement("conflicts"),
        new XElement("replaces"),
        new XElement("provides"),
        new XElement("compatibleWith")),
      new XElement("software-tags"),
      new XElement("system-requires",
        new XElement("maintenance-mode", XmlBool(options.MaintenanceMode))),
      fileList,
      new XElement("acceptance-level", "community"),
      new XElement("live-install-allowed", XmlBool(options.LiveInstallAllowed)),
      new XElement("live-remove-allowed", XmlBool(options.LiveRemoveAllowed)),
      new XElement("cimom-restart", "false"),
      new XElement("stateless-ready", XmlBool(options.StatelessReady)),
      new XElement("overlay", "false"),
      new XElement("payloads",
        new XElement("payload",
          new XAttribute("name", PayloadName),
          new XAttribute("type", "tgz"),
          new XAttribute("size", payloadBytes.LongLength.ToString(CultureInfo.InvariantCulture)),
          new XElement("checksum", new XAttribute("checksum-type", "sha-256"), payloadSha256),
          new XElement("checksum",
            new XAttribute("checksum-type", "sha-256"),
            new XAttribute("verify-process", "gunzip"),
            tarSha256),
          new XElement("checksum",
            new XAttribute("checksum-type", "sha-1"),
            new XAttribute("verify-process", "gunzip"),
            tarSha1))));

    return Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
  }

  private static string NormalizePath(string path, bool isDirectory) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    var normalized = path.Replace('\\', '/');
    if (normalized.StartsWith("/", StringComparison.Ordinal) ||
        (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
      throw new ArgumentException($"VIB payload paths must be relative: '{path}'.", nameof(path));

    if (isDirectory)
      normalized = normalized.TrimEnd('/');
    if (normalized.Length == 0)
      throw new ArgumentException("VIB payload path must not be empty.", nameof(path));

    foreach (var segment in normalized.Split('/')) {
      if (segment.Length == 0 || segment is "." or "..")
        throw new ArgumentException($"Unsafe VIB payload path '{path}'.", nameof(path));
      if (segment.Any(char.IsControl))
        throw new ArgumentException($"VIB payload path contains control characters: '{path}'.", nameof(path));
    }
    return normalized;
  }

  private static void ValidateOptions(VibWriterOptions options) {
    if (string.IsNullOrWhiteSpace(options.Name) ||
        options.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
      throw new ArgumentException("VIB package name must contain only ASCII letters, digits, '.', '_' or '-'.", nameof(options));
    if (string.IsNullOrWhiteSpace(options.Version) || options.Version.Any(char.IsControl))
      throw new ArgumentException("VIB version must not be empty or contain control characters.", nameof(options));
    if (string.IsNullOrWhiteSpace(options.Vendor) || options.Vendor.Any(char.IsControl))
      throw new ArgumentException("VIB vendor must not be empty or contain control characters.", nameof(options));
    if (options.Summary.Any(char.IsControl) || options.Description.Any(char.IsControl))
      throw new ArgumentException("VIB summary/description must not contain control characters.", nameof(options));
    if (options.FileMode is < 0 or > 0xFFF || options.DirectoryMode is < 0 or > 0xFFF)
      throw new ArgumentOutOfRangeException(nameof(options), "VIB payload Unix modes must be in the 0000..7777 range.");
  }

  private static string XmlBool(bool value) => value ? "true" : "false";

  /// <inheritdoc />
    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      this.Finish();
    if (!this._leaveOpen)
      this._output.Dispose();
  }

  private sealed record VibInput(string Path, byte[] Data, bool IsDirectory);
}
