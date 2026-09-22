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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://webassembly.github.io/spec/core/</c> — WebAssembly Core Specification — binary format chapter</description></item>
///   <item><description><c>https://webassembly.org/</c> — project home</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/WebAssembly</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class WasmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Wasm";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "WebAssembly module";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".wasm";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".wasm"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x00, 0x61, 0x73, 0x6D], Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "WebAssembly binary module — surfaces each section (type, import, function, " +
    "code, data, custom …) as a separate entry plus a metadata.ini summary.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored",
      false, false, null)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) {
    var module = WasmReader.ReadLayout(archive);
    var metadata = BuildMetadata(module);
    var result = new List<ArchiveEntryInfo> {
      new(0, "metadata.ini", metadata.LongLength, metadata.LongLength,
        "stored", false, false, null),
    };

    var indexById = new Dictionary<int, int>();
    foreach (var section in module.Sections) {
      var name = GetSectionName(section.Id, section.TypeName, section.CustomName, indexById);
      result.Add(new ArchiveEntryInfo(
        result.Count, name, section.BodyLength, section.BodyLength, "stored",
        false, false, null));
    }
    return result;
  }

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    var module = WasmReader.ReadLayout(archive);

    if (files is null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadata(module));

    var indexById = new Dictionary<int, int>();
    foreach (var section in module.Sections) {
      var name = GetSectionName(section.Id, section.TypeName, section.CustomName, indexById);
      if (files is { Length: > 0 } && !MatchesFilter(name, files))
        continue;

      using var target = CreateEntryFile(outputDir, name);
      target.Write(archive.Slice(section.BodyOffset, section.BodyLength));
    }
  }

  private static IEnumerable<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var module = WasmReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    yield return ("metadata.ini", BuildMetadata(module));

    var indexById = new Dictionary<int, int>();
    foreach (var s in module.Sections)
      yield return (GetSectionName(s.Id, s.TypeName, s.CustomName, indexById), s.Body);
  }

  private static byte[] BuildMetadata(WasmReader.ModuleLayout module) {
    var sb = new StringBuilder();
    sb.AppendLine("[wasm]");
    sb.Append(CultureInfo.InvariantCulture, $"version = {module.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {module.Sections.Count}\n");
    foreach (var s in module.Sections) {
      var label = s.Id == 0 && !string.IsNullOrEmpty(s.CustomName) ? $"custom:{s.CustomName}" : s.TypeName;
      sb.Append(CultureInfo.InvariantCulture, $"section_{s.Id:D2} = {label} ({s.BodyLength} bytes)\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
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

  private static string GetSectionName(
      int id, string typeName, string? customName, Dictionary<int, int> indexById) {
    indexById.TryGetValue(id, out var index);
    indexById[id] = index + 1;

    string name;
    if (id == 0 && !string.IsNullOrEmpty(customName)) {
      var safe = SanitizeForFilename(customName);
      name = $"custom_{safe}.bin";
    } else {
      name = $"section_{id:D2}_{typeName}.bin";
    }

    if (index > 0)
      name = Path.GetFileNameWithoutExtension(name) + $".{index}" + Path.GetExtension(name);
    return name;
  }

  private static string SanitizeForFilename(string name) {
    var chars = name.ToCharArray();
    for (var i = 0; i < chars.Length; i++)
      if (chars[i] is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || chars[i] < 0x20)
        chars[i] = '_';
    return new string(chars);
  }
}
