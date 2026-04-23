using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Zip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wheel;

/// <summary>
/// Descriptor for a Python <b>wheel</b> distribution (<c>.whl</c>) — a ZIP archive
/// that obeys the on-disk layout mandated by PEP 427.
/// </summary>
/// <remarks>
/// <para>
/// A wheel must contain, at the root, a single
/// <c><i>distribution</i>-<i>version</i>.dist-info/</c> directory holding at minimum:
/// </para>
/// <list type="bullet">
///   <item><c>METADATA</c> — RFC 822 / email-header style metadata (Name, Version, Summary, …).</item>
///   <item><c>WHEEL</c> — wheel-version, generator, root-is-purelib, build tag.</item>
///   <item><c>RECORD</c> — CSV of file paths, SHA256 hashes and sizes.</item>
/// </list>
/// <para>
/// Detection requires both the <c>.whl</c> extension and the presence of exactly one
/// <c>*.dist-info/METADATA</c> file inside the ZIP. The descriptor adds a parsed
/// <c>metadata.ini</c> entry at the root summarising the most-used METADATA and
/// WHEEL fields. The underlying ZIP is read via <see cref="ZipReader"/>.
/// </para>
/// <para>
/// Reference: <see href="https://peps.python.org/pep-0427/"/>.
/// </para>
/// </remarks>
public sealed class WheelFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc/>
  public string Id => "Wheel";

  /// <inheritdoc/>
  public string DisplayName => "Python Wheel";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <inheritdoc/>
  public string DefaultExtension => ".whl";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".whl"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  /// <remarks>Empty: outer container is ZIP; detection is by extension to avoid
  /// shadowing the ZIP descriptor.</remarks>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description =>
    "Python wheel package (PEP 427) — ZIP with mandatory dist-info/METADATA, " +
    "WHEEL and RECORD files at the root.";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var zip = new ZipReader(stream, leaveOpen: true, password: password);
    var distInfo = LocateDistInfo(zip);
    var metadata = TryReadEntry(zip, distInfo + "/METADATA");
    var wheel = TryReadEntry(zip, distInfo + "/WHEEL");
    var record = TryReadEntry(zip, distInfo + "/RECORD");
    var summary = BuildMetadata(distInfo, metadata, wheel, record);

    var entries = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", summary.Length, summary.Length, "Tag", false, false, null),
    };
    var idx = 1;
    foreach (var e in zip.Entries) {
      entries.Add(new(idx++, e.FileName, e.UncompressedSize, e.CompressedSize,
        e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified));
    }
    return entries;
  }

  /// <inheritdoc/>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var zip = new ZipReader(stream, leaveOpen: true, password: password);
    var distInfo = LocateDistInfo(zip);
    var metadata = TryReadEntry(zip, distInfo + "/METADATA");
    var wheel = TryReadEntry(zip, distInfo + "/WHEEL");
    var record = TryReadEntry(zip, distInfo + "/RECORD");
    var summary = BuildMetadata(distInfo, metadata, wheel, record);

    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", summary);

    foreach (var e in zip.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.FileName, files))
        continue;
      if (e.IsDirectory) {
        Directory.CreateDirectory(Path.Combine(outputDir, e.FileName));
        continue;
      }
      WriteFile(outputDir, e.FileName, zip.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Returns the dist-info directory name (without trailing slash). Throws
  /// <see cref="InvalidDataException"/> if the ZIP doesn't contain exactly one
  /// <c>*.dist-info/METADATA</c> file at the root.
  /// </summary>
  private static string LocateDistInfo(ZipReader zip) {
    string? found = null;
    foreach (var e in zip.Entries) {
      var name = e.FileName.Replace('\\', '/');
      if (!name.EndsWith("/METADATA", StringComparison.Ordinal))
        continue;
      var dir = name[..^"/METADATA".Length];
      // Must be at the root (no inner slash) and end in `.dist-info`.
      if (dir.Contains('/') || !dir.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase))
        continue;
      if (found != null)
        throw new InvalidDataException(
          $"Wheel must contain exactly one *.dist-info/METADATA file at the root; found '{found}' and '{dir}'.");
      found = dir;
    }
    if (found == null)
      throw new InvalidDataException(
        "Not a Python wheel: missing root-level *.dist-info/METADATA file (PEP 427).");
    return found;
  }

  private static byte[]? TryReadEntry(ZipReader zip, string name) {
    foreach (var e in zip.Entries) {
      if (e.FileName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase))
        return zip.ExtractEntry(e);
    }
    return null;
  }

  /// <summary>
  /// Materialises a key=value INI summarising the parsed METADATA and WHEEL files
  /// plus the RECORD line count. Both METADATA and WHEEL use RFC 822 single-line
  /// "Field: value" syntax which we parse here without an HTTP-headers dependency.
  /// </summary>
  private static byte[] BuildMetadata(string distInfo, byte[]? metadata, byte[]? wheel, byte[]? record) {
    var sb = new StringBuilder();
    sb.AppendLine("[wheel]");
    sb.Append("dist_info = ").AppendLine(distInfo);

    if (metadata is { Length: > 0 }) {
      var fields = ParseRfc822(metadata);
      sb.AppendLine();
      sb.AppendLine("[metadata]");
      foreach (var key in new[] { "Name", "Version", "Summary", "Home-page", "Author", "Author-email", "License", "Requires-Python" }) {
        if (fields.TryGetValue(key, out var values) && values.Count > 0)
          sb.Append(NormaliseKey(key)).Append(" = ").AppendLine(values[0]);
      }
      if (fields.TryGetValue("Requires-Dist", out var deps) && deps.Count > 0) {
        sb.Append("requires_dist_count = ").Append(deps.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
        for (var i = 0; i < deps.Count; ++i)
          sb.Append("requires_dist_").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" = ").AppendLine(deps[i]);
      }
    }

    if (wheel is { Length: > 0 }) {
      var fields = ParseRfc822(wheel);
      sb.AppendLine();
      sb.AppendLine("[wheel_meta]");
      foreach (var key in new[] { "Wheel-Version", "Generator", "Root-Is-Purelib", "Build" }) {
        if (fields.TryGetValue(key, out var values) && values.Count > 0)
          sb.Append(NormaliseKey(key)).Append(" = ").AppendLine(values[0]);
      }
      if (fields.TryGetValue("Tag", out var tags) && tags.Count > 0) {
        sb.Append("tag_count = ").Append(tags.Count.ToString(CultureInfo.InvariantCulture)).AppendLine();
        for (var i = 0; i < tags.Count; ++i)
          sb.Append("tag_").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" = ").AppendLine(tags[i]);
      }
    }

    if (record is { Length: > 0 }) {
      var lines = 0;
      foreach (var b in record)
        if (b == (byte)'\n') ++lines;
      sb.AppendLine();
      sb.AppendLine("[record]");
      sb.Append("entry_count = ").Append(lines.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }

    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  /// <summary>
  /// Parses a wheel METADATA / WHEEL file (RFC 822 single-line "Field: value" format,
  /// optional continuation lines with leading whitespace). Multi-valued fields
  /// (Requires-Dist, Tag) accumulate every occurrence; single-valued fields keep
  /// only the most recent.
  /// </summary>
  private static Dictionary<string, List<string>> ParseRfc822(byte[] body) {
    var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var text = Encoding.UTF8.GetString(body);
    string? currentKey = null;
    foreach (var rawLine in text.Split('\n')) {
      var line = rawLine.TrimEnd('\r');
      if (line.Length == 0) {
        currentKey = null;
        continue; // body separator — anything after is the long description
      }
      if ((line[0] == ' ' || line[0] == '\t') && currentKey != null && result.TryGetValue(currentKey, out var existing) && existing.Count > 0) {
        existing[^1] += "\n" + line.TrimStart();
        continue;
      }
      var colon = line.IndexOf(':');
      if (colon <= 0) continue;
      var key = line[..colon].Trim();
      var value = line[(colon + 1)..].Trim();
      if (!result.TryGetValue(key, out var bucket)) {
        bucket = [];
        result[key] = bucket;
      }
      bucket.Add(value);
      currentKey = key;
    }
    return result;
  }

  private static string NormaliseKey(string key) => key.ToLowerInvariant().Replace('-', '_');
}
