using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Tar;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gem;

/// <summary>
/// Descriptor for a Ruby <b>gem</b> package (<c>.gem</c>) — a TAR archive whose
/// first three entries are <c>metadata.gz</c> (gzipped YAML metadata),
/// <c>data.tar.gz</c> (gzipped TAR of the gem contents) and
/// <c>checksums.yaml.gz</c>.
/// </summary>
/// <remarks>
/// <para>
/// Detection requires both the <c>.gem</c> extension and the canonical entry order:
/// declaring the TAR magic here would shadow plain TAR. The descriptor inflates
/// <c>metadata.gz</c> to extract the gem's YAML attributes (name, version, summary,
/// dependencies, license) and surfaces them in <c>metadata.ini</c>; it then
/// inflates the inner <c>data.tar.gz</c> and walks it to expose every payload
/// file under a <c>data/</c> prefix.
/// </para>
/// <para>
/// Reference: <see href="https://docs.ruby-lang.org/en/3.0/Gem/Format.html"/>.
/// </para>
/// </remarks>
public sealed class GemFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc/>
  public string Id => "Gem";

  /// <inheritdoc/>
  public string DisplayName => "Ruby Gem";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <inheritdoc/>
  public string DefaultExtension => ".gem";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions => [".gem"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  /// <remarks>Empty: outer container is TAR; detection is by extension to avoid
  /// shadowing the TAR descriptor.</remarks>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gzip", "Gzip")];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description =>
    "Ruby gem package — TAR with metadata.gz, data.tar.gz and checksums.yaml.gz.";

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
  /// Reads the outer TAR, picks out <c>metadata.gz</c>, <c>data.tar.gz</c> and
  /// <c>checksums.yaml.gz</c>, inflates them and pre-materialises every entry
  /// the descriptor will surface (a <c>metadata.ini</c> summary, the raw gzip
  /// blobs, and the recursively-extracted contents of the inner TAR).
  /// </summary>
  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    byte[]? metadataGz = null;
    byte[]? dataTarGz = null;
    byte[]? checksumsGz = null;
    var seenNames = new List<string>();

    var reader = new TarReader(stream);
    while (reader.GetNextEntry() is { } entry) {
      if (entry.IsDirectory) {
        reader.Skip();
        continue;
      }
      var name = entry.Name;
      seenNames.Add(name);
      using var entryStream = reader.GetEntryStream();
      using var ms = new MemoryStream();
      entryStream.CopyTo(ms);
      var data = ms.ToArray();
      switch (name) {
        case "metadata.gz": metadataGz = data; break;
        case "data.tar.gz": dataTarGz = data; break;
        case "checksums.yaml.gz": checksumsGz = data; break;
      }
    }

    EnsureCanonicalLayout(seenNames);

    var metadataYaml = metadataGz is { Length: > 0 } ? GunzipAll(metadataGz) : null;
    var checksumsYaml = checksumsGz is { Length: > 0 } ? GunzipAll(checksumsGz) : null;

    var result = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadataIni(metadataYaml), "Tag"),
    };
    if (metadataYaml is { Length: > 0 })
      result.Add(("metadata.yaml", metadataYaml, "Manifest"));
    if (checksumsYaml is { Length: > 0 })
      result.Add(("checksums.yaml", checksumsYaml, "Manifest"));

    if (dataTarGz is { Length: > 0 }) {
      var innerTar = GunzipAll(dataTarGz);
      using var innerMs = new MemoryStream(innerTar);
      var innerReader = new TarReader(innerMs);
      while (innerReader.GetNextEntry() is { } entry) {
        if (entry.IsDirectory) {
          innerReader.Skip();
          continue;
        }
        using var es = innerReader.GetEntryStream();
        using var bms = new MemoryStream();
        es.CopyTo(bms);
        result.Add(("data/" + entry.Name.Replace('\\', '/'), bms.ToArray(), "Payload"));
      }
    }

    return result;
  }

  /// <summary>
  /// Verifies the seen entry names contain exactly the three canonical gem files;
  /// throws <see cref="InvalidDataException"/> otherwise so <c>FormatDetector</c>
  /// can fall back to plain TAR.
  /// </summary>
  private static void EnsureCanonicalLayout(IReadOnlyList<string> seenNames) {
    var hasMetadata = false;
    var hasData = false;
    var hasChecksums = false;
    foreach (var name in seenNames) {
      if (name == "metadata.gz") hasMetadata = true;
      else if (name == "data.tar.gz") hasData = true;
      else if (name == "checksums.yaml.gz") hasChecksums = true;
    }
    if (!hasMetadata || !hasData || !hasChecksums)
      throw new InvalidDataException(
        "Not a Ruby gem: TAR must contain metadata.gz, data.tar.gz and checksums.yaml.gz.");
  }

  private static byte[] GunzipAll(byte[] data) {
    using var input = new MemoryStream(data);
    using var gz = new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    gz.CopyTo(output);
    return output.ToArray();
  }

  /// <summary>
  /// Extracts a small set of well-known gem fields from the raw YAML metadata
  /// stream. The parser handles the trivial <c>name: value</c> and
  /// <c>name: !ruby/object …</c> + nested <c>name: value</c> shapes that
  /// <c>gem build</c> emits; nothing here pretends to be a full YAML parser.
  /// </summary>
  private static byte[] BuildMetadataIni(byte[]? metadataYaml) {
    var sb = new StringBuilder();
    sb.AppendLine("[gem]");

    if (metadataYaml is { Length: > 0 }) {
      var text = Encoding.UTF8.GetString(metadataYaml);
      sb.Append("name = ").AppendLine(ExtractScalar(text, "name") ?? "(unknown)");
      sb.Append("version = ").AppendLine(ExtractVersion(text) ?? "(unknown)");
      sb.Append("summary = ").AppendLine(ExtractScalar(text, "summary") ?? "(unknown)");
      sb.Append("license = ").AppendLine(ExtractScalar(text, "license") ?? "(unknown)");
      sb.Append("homepage = ").AppendLine(ExtractScalar(text, "homepage") ?? "(unknown)");
      var deps = CountTopLevelDependencies(text);
      sb.Append("runtime_dependencies = ").Append(deps.ToString(CultureInfo.InvariantCulture)).AppendLine();
    } else {
      sb.AppendLine("note = metadata.gz absent or empty");
    }

    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  /// <summary>
  /// Returns the inline scalar value for a top-level YAML mapping key
  /// (e.g. <c>name: rspec</c>) or <see langword="null"/> if the key isn't
  /// present at column 0 with a non-empty inline value.
  /// </summary>
  private static string? ExtractScalar(string yaml, string key) {
    foreach (var rawLine in yaml.Split('\n')) {
      var line = rawLine.TrimEnd('\r');
      if (line.Length == 0 || (line[0] != '-' && char.IsWhiteSpace(line[0])))
        continue;
      var prefix = key + ":";
      if (!line.StartsWith(prefix, StringComparison.Ordinal))
        continue;
      var value = line[prefix.Length..].Trim();
      if (value.Length == 0) continue;
      return Unquote(value);
    }
    return null;
  }

  /// <summary>
  /// Locates the first nested <c>version: x.y.z</c> under any <c>!ruby/object:Gem::Version</c>
  /// — this is the encoding gemspec emits for the gem version.
  /// </summary>
  private static string? ExtractVersion(string yaml) {
    var lines = yaml.Split('\n');
    for (var i = 0; i < lines.Length; ++i) {
      var line = lines[i].TrimEnd('\r');
      if (!line.Contains("!ruby/object:Gem::Version", StringComparison.Ordinal))
        continue;
      // Scan up to the next 4 lines for `  version: ...`.
      for (var j = i + 1; j < Math.Min(lines.Length, i + 5); ++j) {
        var sub = lines[j].TrimEnd('\r');
        var trimmed = sub.TrimStart();
        if (trimmed.StartsWith("version:", StringComparison.Ordinal))
          return Unquote(trimmed["version:".Length..].Trim());
      }
    }
    return null;
  }

  private static int CountTopLevelDependencies(string yaml) {
    var inDeps = false;
    var count = 0;
    foreach (var rawLine in yaml.Split('\n')) {
      var line = rawLine.TrimEnd('\r');
      if (line.StartsWith("dependencies:", StringComparison.Ordinal)) {
        inDeps = true;
        continue;
      }
      if (!inDeps) continue;
      if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
        break; // left the dependencies block
      if (line.StartsWith("- !ruby/object:Gem::Dependency", StringComparison.Ordinal))
        ++count;
    }
    return count;
  }

  private static string Unquote(string value) {
    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
      return value[1..^1];
    if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
      return value[1..^1];
    return value;
  }
}
