#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Elf;

/// <summary>
/// Read-only archive view of an ELF executable, shared object, or relocatable object.
/// Every non-null section is surfaced as an entry under <c>sections/</c>, with
/// type-specific aliases (<c>interp.txt</c>, <c>symbols.txt</c>, <c>notes/*.bin</c>).
/// </summary>
public sealed class ElfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Elf";
  public string DisplayName => "ELF executable";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".elf";
  public IReadOnlyList<string> Extensions => [".elf", ".so", ".o", ".ko"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x7F 'E' 'L' 'F' — same magic for 32-bit and 64-bit (EI_CLASS at offset 4 disambiguates).
    new([0x7F, (byte)'E', (byte)'L', (byte)'F'], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ELF executable / shared object / relocatable object surfaced as an archive of sections, " +
    "with decoded symbol tables and notes.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new ElfReader().ReadAll(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new ElfReader().ReadAll(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}
