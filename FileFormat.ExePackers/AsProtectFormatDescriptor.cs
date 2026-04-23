#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for ASProtect-protected Win32 executables.
/// ASProtect (Solodovnikov, 2000+) is the commercial sibling of ASPack —
/// adds anti-debug, code morphing, and registration-key checking on top of
/// the same compressor core. The unpacker stub almost always embeds the
/// ASCII literal <c>"ASProtect"</c> (and frequently the legacy banner
/// <c>"Stripped by ASPACK"</c>). Section names overlap with ASPack
/// (<c>.aspack</c>, <c>.adata</c>) so the <c>"ASProtect"</c> literal is the
/// reliable distinguishing fingerprint.
/// </summary>
public sealed class AsProtectFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "AsProtect";
  public string DisplayName => "ASProtect (Win32 PE)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ASProtect (Solodovnikov) Win32 PE protector — surfaces the embedded " +
    "\"ASProtect\" / \"Stripped by ASPACK\" literals and the .aspack/.adata " +
    "section table. Decompression delegated to RL!dePacker / QUnpack / Strip-X.";

  private static ReadOnlySpan<byte> AsProtectLiteral => "ASProtect"u8;
  private static ReadOnlySpan<byte> StrippedByLiteral => "Stripped by ASPACK"u8;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

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
      throw new InvalidDataException("ASProtect: not a valid PE.");

    var span = bytes.AsSpan();
    var asProtectIdx = span.IndexOf(AsProtectLiteral);
    var strippedIdx = span.IndexOf(StrippedByLiteral);

    if (asProtectIdx < 0)
      throw new InvalidDataException("ASProtect: \"ASProtect\" literal not found anywhere in file.");

    var sections = PackerScanner.GetPeSections(bytes);
    var aspackSection = sections.FirstOrDefault(s =>
      s.Name.Equals(".aspack", StringComparison.OrdinalIgnoreCase) ||
      s.Name.Equals(".adata", StringComparison.OrdinalIgnoreCase));

    return [
      ("metadata.ini", BuildMetadata(sections, aspackSection.Name, asProtectIdx, strippedIdx)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(IReadOnlyList<(string Name, uint Characteristics)> sections,
      string aspackSectionName, int asProtectIdx, int strippedIdx) {
    var sb = new StringBuilder();
    sb.AppendLine("[asprotect]");
    sb.Append(CultureInfo.InvariantCulture, $"asprotect_literal_offset = 0x{asProtectIdx:X6}\n");
    sb.Append(CultureInfo.InvariantCulture, $"stripped_literal_offset = {(strippedIdx < 0 ? "(not found)" : $"0x{strippedIdx:X6}")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"aspack_section = {(string.IsNullOrEmpty(aspackSectionName) ? "(none)" : aspackSectionName)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {sections.Count}\n");
    foreach (var (name, chars) in sections)
      sb.Append(CultureInfo.InvariantCulture, $"section = {name} flags=0x{chars:X8}\n");
    sb.Append("note = decompression delegated to RL!dePacker / QUnpack / Strip-X (no official unpacker)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
