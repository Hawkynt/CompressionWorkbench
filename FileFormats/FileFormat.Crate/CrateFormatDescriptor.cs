using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Tar;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Crate;

/// <summary>
/// Descriptor for a Rust <b>crate</b> package (<c>.crate</c>) — a gzipped TAR
/// containing a single <c>name-version/</c> top-level directory with a
/// <c>Cargo.toml</c> and the crate's source files.
/// </summary>
/// <remarks>
/// <para>
/// Detection requires both the <c>.crate</c> extension and the canonical layout:
/// declaring TAR or gzip magic here would shadow those descriptors. The descriptor
/// inflates the gzip layer, walks the inner TAR, parses <c>Cargo.toml</c> for the
/// <c>[package]</c> fields most callers care about (name, version, authors,
/// description, license, edition) and surfaces a <c>metadata.ini</c> entry
/// alongside every crate file.
/// </para>
/// <para>
/// Reference:
/// <see href="https://doc.rust-lang.org/cargo/reference/registries.html#publish"/>.
/// </para>
/// </remarks>
public sealed class CrateFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc/>
  public string Id => "Crate";

  /// <inheritdoc/>
  public string DisplayName => "Rust Crate";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <inheritdoc/>
  public string DefaultExtension => ".crate";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".crate"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  /// <remarks>Empty: outer container is gzip; detection is by extension to avoid
  /// shadowing the gzip / TAR descriptors.</remarks>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gzip", "Gzip")];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => "Gzip";

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description =>
    "Rust crate package — TAR.GZ with a single name-version/ directory containing " +
    "Cargo.toml and source files.";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = BuildEntries(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i,
      Name: e.Name,
      OriginalSize: e.Data.Length,
      CompressedSize: e.Data.Length,
      Method: e.Method,
      IsDirectory: false,
      IsEncrypted: false,
      LastModified: null)).ToList();
  }

  /// <inheritdoc/>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Inflates the gzip wrapper, walks the inner TAR, validates the canonical
  /// single-top-level-directory layout, parses <c>Cargo.toml</c> and pre-materialises
  /// every entry the descriptor will surface.
  /// </summary>
  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    byte[] inner;
    try {
      using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
      using var ms = new MemoryStream();
      gz.CopyTo(ms);
      inner = ms.ToArray();
    } catch (InvalidDataException ex) {
      throw new InvalidDataException("Not a Rust crate: outer gzip layer failed to inflate.", ex);
    }

    string? topDir = null;
    byte[]? cargoToml = null;
    var payload = new List<(string Name, byte[] Data)>();
    using (var innerMs = new MemoryStream(inner)) {
      var reader = new TarReader(innerMs);
      while (reader.GetNextEntry() is { } entry) {
        if (entry.IsDirectory) {
          reader.Skip();
          continue;
        }
        var name = entry.Name.Replace('\\', '/').TrimStart('/');
        var slash = name.IndexOf('/');
        if (slash <= 0)
          throw new InvalidDataException(
            $"Not a Rust crate: entry '{name}' is not under a single top-level directory.");
        var dir = name[..slash];
        if (topDir == null)
          topDir = dir;
        else if (!dir.Equals(topDir, StringComparison.Ordinal))
          throw new InvalidDataException(
            $"Not a Rust crate: multiple top-level directories ('{topDir}', '{dir}').");

        using var es = reader.GetEntryStream();
        using var bytes = new MemoryStream();
        es.CopyTo(bytes);
        var data = bytes.ToArray();
        if (name.Equals(topDir + "/Cargo.toml", StringComparison.Ordinal))
          cargoToml = data;
        payload.Add((name, data));
      }
    }

    if (topDir == null || cargoToml == null)
      throw new InvalidDataException(
        "Not a Rust crate: missing <name-version>/Cargo.toml under a single top-level directory.");

    var result = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadataIni(topDir, cargoToml, payload.Count), "Tag"),
    };
    foreach (var (name, data) in payload)
      result.Add((name, data, "Payload"));
    return result;
  }

  /// <summary>
  /// Parses the <c>[package]</c> table of the crate's <c>Cargo.toml</c> and emits a
  /// human-readable INI summary. The TOML parser is intentionally minimal — it
  /// only handles top-level keys inside the first <c>[package]</c> table, which is
  /// sufficient for surfacing the well-known crate manifest fields.
  /// </summary>
  private static byte[] BuildMetadataIni(string topDir, byte[] cargoToml, int fileCount) {
    var sb = new StringBuilder();
    sb.AppendLine("[crate]");
    sb.Append("top_directory = ").AppendLine(topDir);
    sb.Append("file_count = ").Append(fileCount.ToString(CultureInfo.InvariantCulture)).AppendLine();

    var pkg = ParsePackageTable(Encoding.UTF8.GetString(cargoToml));
    sb.AppendLine();
    sb.AppendLine("[package]");
    foreach (var key in new[] { "name", "version", "edition", "rust-version", "description", "license", "license-file", "homepage", "repository" }) {
      if (pkg.TryGetValue(key, out var value))
        sb.Append(NormaliseKey(key)).Append(" = ").AppendLine(value);
    }
    if (pkg.TryGetValue("authors", out var authors))
      sb.Append("authors = ").AppendLine(authors);
    if (pkg.TryGetValue("keywords", out var keywords))
      sb.Append("keywords = ").AppendLine(keywords);
    if (pkg.TryGetValue("categories", out var categories))
      sb.Append("categories = ").AppendLine(categories);

    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  /// <summary>
  /// Returns a key→value dictionary scraped from the first <c>[package]</c> table
  /// in a Cargo manifest. String values are unquoted, array values are joined with
  /// "; " for one-line display.
  /// </summary>
  private static Dictionary<string, string> ParsePackageTable(string toml) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var inPackage = false;
    var lines = toml.Split('\n');
    for (var i = 0; i < lines.Length; ++i) {
      var line = lines[i].TrimEnd('\r').TrimStart();
      if (line.Length == 0 || line[0] == '#') continue;
      if (line.StartsWith('[')) {
        inPackage = line.StartsWith("[package]", StringComparison.Ordinal);
        continue;
      }
      if (!inPackage) continue;
      var equals = line.IndexOf('=');
      if (equals <= 0) continue;
      var key = line[..equals].Trim();
      var rawValue = line[(equals + 1)..].Trim();

      // Multi-line array? Continue accumulating until we see the closing ']'.
      if (rawValue.StartsWith('[') && !rawValue.Contains(']')) {
        var sb = new StringBuilder(rawValue);
        while (++i < lines.Length) {
          var more = lines[i].TrimEnd('\r').TrimStart();
          sb.Append(' ').Append(more);
          if (more.Contains(']')) break;
        }
        rawValue = sb.ToString();
      }

      result[key] = NormaliseTomlValue(rawValue);
    }
    return result;
  }

  /// <summary>
  /// Converts a TOML scalar literal or array literal to a plain display string.
  /// Strings drop their surrounding quotes; arrays are flattened to "a; b; c".
  /// </summary>
  private static string NormaliseTomlValue(string raw) {
    raw = raw.TrimEnd();
    // Strip trailing inline comments.
    var hash = raw.IndexOf('#');
    if (hash >= 0) raw = raw[..hash].TrimEnd();
    if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
      return raw[1..^1];
    if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
      return raw[1..^1];
    if (raw.StartsWith('[') && raw.EndsWith(']')) {
      var inner = raw[1..^1];
      var parts = SplitTopLevel(inner);
      for (var i = 0; i < parts.Count; ++i)
        parts[i] = NormaliseTomlValue(parts[i].Trim());
      return string.Join("; ", parts.Where(p => p.Length > 0));
    }
    return raw;
  }

  /// <summary>
  /// Splits a TOML array body on top-level commas, ignoring commas inside string
  /// literals. Sufficient for the simple <c>authors = ["a", "b"]</c> shape.
  /// </summary>
  private static List<string> SplitTopLevel(string body) {
    var parts = new List<string>();
    var sb = new StringBuilder();
    var inString = false;
    var quoteChar = '\0';
    foreach (var c in body) {
      if (inString) {
        sb.Append(c);
        if (c == quoteChar) inString = false;
        continue;
      }
      if (c == '"' || c == '\'') {
        inString = true;
        quoteChar = c;
        sb.Append(c);
        continue;
      }
      if (c == ',') {
        parts.Add(sb.ToString());
        sb.Clear();
        continue;
      }
      sb.Append(c);
    }
    if (sb.Length > 0) parts.Add(sb.ToString());
    return parts;
  }

  private static string NormaliseKey(string key) => key.ToLowerInvariant().Replace('-', '_');
}
