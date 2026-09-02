#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Elf;

/// <summary>
/// Read-only archive view of an ELF executable, shared object, or relocatable object.
/// Every non-null section is surfaced as an entry under <c>sections/</c>, with
/// type-specific aliases (<c>interp.txt</c>, <c>symbols.txt</c>, <c>notes/*.bin</c>).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.sco.com/developers/gabi/</c> — System V gABI — the defining ELF specification</description></item>
///   <item><description><c>https://man7.org/linux/man-pages/man5/elf.5.html</c> — elf(5) man page</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Executable_and_Linkable_Format</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class ElfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Elf";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ELF executable";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".elf";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".elf", ".so", ".o", ".ko"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x7F 'E' 'L' 'F' — same magic for 32-bit and 64-bit (EI_CLASS at offset 4 disambiguates).
    new([0x7F, (byte)'E', (byte)'L', (byte)'F'], Confidence: 0.95),
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
    "ELF executable / shared object / relocatable object surfaced as an archive of sections, " +
    "with decoded symbol tables and notes.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new ElfReader().ReadAll(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new ElfReader().ReadAll(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}
