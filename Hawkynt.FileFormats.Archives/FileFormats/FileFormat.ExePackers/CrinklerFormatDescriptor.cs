#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for Crinkler-packed Win32 executables. Crinkler
/// (Mentor &amp; Blueberry, 2005+) is the de facto 4K Windows executable
/// compressor of the demoscene; it produces extremely small, atypically-laid
/// out PE files (often only 1-2 sections, no real import directory) and
/// embeds the literal string <c>"Crinkler"</c> somewhere in the file.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/runestubbe/Crinkler</c> — Crinkler source (open-sourced 2020)</description></item>
///   <item><description><c>http://crinkler.net</c> — official Crinkler site</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Structural-only detection (small/weird PE) is unreliable, so we require
/// the embedded literal in addition to <see cref="PackerScanner.IsPe"/>.
/// </remarks>
public sealed class CrinklerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Crinkler";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Crinkler (Win32 PE 4K)";
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
  public string DefaultExtension => ".exe";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
    "Crinkler (Mentor & Blueberry) — 4K Win32 PE compressor used in the " +
    "Windows demoscene. Detection by embedded \"Crinkler\" literal in an " +
    "atypical PE. Current support is detection and diagnostic artifact output; " +
    "native Crinkler decompression is not implemented.";

  private static ReadOnlySpan<byte> CrinklerLiteralUpper => "Crinkler"u8;
  private static ReadOnlySpan<byte> CrinklerLiteralLower => "crinkler"u8;
  private static ReadOnlySpan<byte> CrinklerLiteralAllCaps => "CRINKLER"u8;

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();

    if (!PackerScanner.IsPe(bytes))
      throw new InvalidDataException("Crinkler: not a valid PE.");

    var span = bytes.AsSpan();
    var idx = span.IndexOf(CrinklerLiteralUpper);
    if (idx < 0) idx = span.IndexOf(CrinklerLiteralLower);
    if (idx < 0) idx = span.IndexOf(CrinklerLiteralAllCaps);
    if (idx < 0)
      throw new InvalidDataException("Crinkler: \"Crinkler\" literal not found anywhere in file.");

    var sections = PackerScanner.GetPeSections(bytes);
    return [
      ("metadata.json", BuildMetadataJson(sections, idx, bytes.Length)),
      ("diagnostics.json", BuildDiagnosticsJson()),
      ("metadata.ini", BuildMetadata(sections, idx, bytes.Length)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("original_packed.bin", bytes),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      int literalOffset, int totalSize) {
    var sb = new StringBuilder();
    sb.AppendLine("[crinkler]");
    sb.Append(CultureInfo.InvariantCulture, $"crinkler_literal_offset = 0x{literalOffset:X6}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {totalSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("capability_level = DetectionOnly\n");
    sb.Append("can_build_memory_image = false\n");
    sb.Append("can_rebuild_executable = false\n");
    sb.Append("note = native Crinkler decompression is not implemented\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildMetadataJson(IReadOnlyList<(string Name, uint Characteristics)> sections,
      int literalOffset, int totalSize) {
    var sb = new StringBuilder();
    sb.AppendLine("{");
    sb.AppendLine("  \"packer\": \"crinkler\",");
    sb.AppendLine("  \"container\": \"pe\",");
    sb.AppendLine("  \"capabilityLevel\": \"DetectionOnly\",");
    sb.AppendLine("  \"canBuildMemoryImage\": false,");
    sb.AppendLine("  \"canRebuildExecutable\": false,");
    sb.Append(CultureInfo.InvariantCulture, $"  \"crinklerLiteralOffset\": {literalOffset},\n");
    sb.Append(CultureInfo.InvariantCulture, $"  \"fileSize\": {totalSize},\n");
    sb.AppendLine("  \"sections\": [");
    for (var i = 0; i < sections.Count; i++) {
      var (name, chars) = sections[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"    {{ \"name\": \"{EscapeJson(name)}\", \"characteristics\": \"0x{chars:X8}\" }}");
      sb.AppendLine(i + 1 == sections.Count ? "" : ",");
    }
    sb.AppendLine("  ],");
    sb.AppendLine("  \"outputs\": [");
    sb.AppendLine("    \"metadata.json\",");
    sb.AppendLine("    \"diagnostics.json\",");
    sb.AppendLine("    \"metadata.ini\",");
    sb.AppendLine("    \"mz_header.bin\",");
    sb.AppendLine("    \"original_packed.bin\",");
    sb.AppendLine("    \"packed_payload.bin\"");
    sb.AppendLine("  ]");
    sb.AppendLine("}");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildDiagnosticsJson() =>
    Encoding.UTF8.GetBytes("""
      {
        "packer": "crinkler",
        "container": "pe",
        "capabilityLevel": "DetectionOnly",
        "canLocatePayload": false,
        "canDecompressPayload": false,
        "canBuildMemoryImage": false,
        "canRebuildExecutable": false,
        "warnings": [
          "Crinkler native decompression is not implemented.",
          "Crinkler is a compressing linker; a byte-identical pre-Crinkler executable is not generally reconstructable from the packed image."
        ],
        "outputs": [
          "metadata.json",
          "diagnostics.json",
          "metadata.ini",
          "mz_header.bin",
          "original_packed.bin",
          "packed_payload.bin"
        ]
      }
      """);

  private static string EscapeJson(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
