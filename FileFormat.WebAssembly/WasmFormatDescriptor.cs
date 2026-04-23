#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WebAssembly;

/// <summary>
/// Pseudo-archive descriptor for WebAssembly binary modules. Each section is
/// surfaced as an entry — well-known sections (type, import, function, code …)
/// get descriptive names, and custom sections (e.g. <c>name</c>, <c>producers</c>,
/// <c>.debug_info</c>) carry their embedded name in the entry filename.
/// </summary>
public sealed class WasmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Wasm";
  public string DisplayName => "WebAssembly module";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".wasm";
  public IReadOnlyList<string> Extensions => [".wasm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x61, 0x73, 0x6D], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "WebAssembly binary module — surfaces each section (type, import, function, " +
    "code, data, custom …) as a separate entry plus a metadata.ini summary.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored",
      false, false, null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static IEnumerable<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var module = WasmReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    yield return ("metadata.ini", BuildMetadata(module));

    // Track section indices per id so multiple custom sections (or duplicates) get
    // unique filenames.
    var indexById = new Dictionary<int, int>();
    foreach (var s in module.Sections) {
      indexById.TryGetValue(s.Id, out var idx);
      indexById[s.Id] = idx + 1;

      string name;
      if (s.Id == 0 && !string.IsNullOrEmpty(s.CustomName)) {
        // Sanitize the custom name for filesystem use.
        var safe = SanitizeForFilename(s.CustomName);
        name = $"custom_{safe}.bin";
      } else {
        name = $"section_{s.Id:D2}_{s.TypeName}.bin";
      }
      // Disambiguate repeated names (rare but legal — multiple custom sections with the same name).
      if (idx > 0) name = Path.GetFileNameWithoutExtension(name) + $".{idx}" + Path.GetExtension(name);

      yield return (name, s.Body);
    }
  }

  private static byte[] BuildMetadata(WasmReader.Module module) {
    var sb = new StringBuilder();
    sb.AppendLine("[wasm]");
    sb.Append(CultureInfo.InvariantCulture, $"version = {module.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {module.Sections.Count}\n");
    foreach (var s in module.Sections) {
      var label = s.Id == 0 && !string.IsNullOrEmpty(s.CustomName) ? $"custom:{s.CustomName}" : s.TypeName;
      sb.Append(CultureInfo.InvariantCulture, $"section_{s.Id:D2} = {label} ({s.Body.Length} bytes)\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string SanitizeForFilename(string name) {
    var chars = name.ToCharArray();
    for (var i = 0; i < chars.Length; i++)
      if (chars[i] is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || chars[i] < 0x20)
        chars[i] = '_';
    return new string(chars);
  }
}
