using System.Globalization;
using System.Text;
using Compression.Registry;
using FileSystem.SquashFs;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Snap;

/// <summary>
/// Descriptor for Canonical snap packages.
/// A <c>.snap</c> file is a SquashFS v4 image whose root contains
/// <c>meta/snap.yaml</c>. The descriptor parses that manifest to surface
/// identity metadata in a synthetic <c>metadata.ini</c> entry and then
/// exposes every SquashFS entry verbatim under its original path.
/// </summary>
public sealed class SnapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>Unique format identifier.</summary>
  public string Id => "Snap";

  /// <summary>Human-readable name.</summary>
  public string DisplayName => "Snap package";

  /// <summary>This format describes an archive container.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Capabilities supported by this descriptor.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>Preferred extension.</summary>
  public string DefaultExtension => ".snap";

  /// <summary>Extensions recognised as Snap packages.</summary>
  public IReadOnlyList<string> Extensions => [".snap"];

  /// <summary>Compound extensions are not used by this format.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// No magic bytes are advertised here even though the SquashFS <c>hsqs</c>
  /// signature is present: detection is extension-based to avoid first-match
  /// conflicts with the generic SquashFS descriptor.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>Compression methods are whatever the embedded SquashFS uses.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("squashfs", "SquashFS")];

  /// <summary>Not a TAR-compound format.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Algorithmic family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Short description.</summary>
  public string Description => "Canonical snap package (SquashFS + meta/snap.yaml)";

  /// <summary>
  /// Lists a synthetic <c>metadata.ini</c> entry derived from <c>meta/snap.yaml</c>,
  /// followed by every entry inside the SquashFS image.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    _ = password;
    using var r = new SquashFsReader(stream, leaveOpen: true);
    var metadata = BuildMetadata(r);

    var entries = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", metadata.Length, metadata.Length, "yaml", false, false, null),
    };
    for (var i = 0; i < r.Entries.Count; i++) {
      var e = r.Entries[i];
      entries.Add(new ArchiveEntryInfo(
        i + 1, e.FullPath, e.Size, -1,
        "squashfs", e.IsDirectory, false, e.ModifiedTime));
    }
    return entries;
  }

  /// <summary>
  /// Extracts SquashFS contents to <paramref name="outputDir"/> and also emits
  /// <c>metadata.ini</c> when no explicit filter is supplied or when the filter
  /// explicitly names it.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    _ = password;
    using var r = new SquashFsReader(stream, leaveOpen: true);
    var metadata = BuildMetadata(r);

    if (files == null || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", metadata);

    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (e.IsSymlink) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  private static byte[] BuildMetadata(SquashFsReader r) {
    var yaml = TryReadSnapYaml(r);
    var parsed = yaml is null ? new Dictionary<string, string>() : ParseTopLevelYaml(yaml);

    var sb = new StringBuilder();
    sb.Append("[snap]\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_entries = {r.Entries.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"snap_yaml_present = {(yaml is not null)}\n");

    AppendIfPresent(sb, parsed, "name");
    AppendIfPresent(sb, parsed, "version");
    AppendIfPresent(sb, parsed, "summary");
    AppendIfPresent(sb, parsed, "description");
    AppendIfPresent(sb, parsed, "confinement");
    AppendIfPresent(sb, parsed, "base");
    AppendIfPresent(sb, parsed, "grade");
    AppendIfPresent(sb, parsed, "type");
    AppendIfPresent(sb, parsed, "architectures");

    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static void AppendIfPresent(StringBuilder sb, Dictionary<string, string> map, string key) {
    if (map.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
      sb.Append(CultureInfo.InvariantCulture, $"{key} = {v}\n");
  }

  private static string? TryReadSnapYaml(SquashFsReader r) {
    foreach (var e in r.Entries) {
      if (e.IsDirectory || e.IsSymlink) continue;
      if (!e.FullPath.Equals("meta/snap.yaml", StringComparison.Ordinal)) continue;
      try {
        var bytes = r.Extract(e);
        return Encoding.UTF8.GetString(bytes);
      } catch {
        return null;
      }
    }
    return null;
  }

  /// <summary>
  /// Minimal parser that extracts only top-level scalar <c>key: value</c> pairs
  /// from a YAML document. Nested mappings, sequences, and block scalars are
  /// intentionally ignored — we only need identity fields for <c>metadata.ini</c>.
  /// </summary>
  private static Dictionary<string, string> ParseTopLevelYaml(string yaml) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var lines = yaml.Split('\n');
    foreach (var raw in lines) {
      var line = raw.TrimEnd('\r');
      if (line.Length == 0) continue;
      if (line[0] == '#') continue;
      // Top-level keys are unindented.
      if (line[0] == ' ' || line[0] == '\t') continue;
      if (line[0] == '-') continue;
      var colon = line.IndexOf(':');
      if (colon <= 0) continue;
      var key = line[..colon].Trim();
      var value = line[(colon + 1)..].Trim();
      if (value.Length == 0) continue; // block-start or block-mapping — skip
      // Strip trailing inline comments (simple case: " # ...").
      var hashIdx = value.IndexOf(" #", StringComparison.Ordinal);
      if (hashIdx >= 0) value = value[..hashIdx].TrimEnd();
      // Strip surrounding quotes.
      if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
        value = value[1..^1];
      result[key] = value;
    }
    return result;
  }
}
